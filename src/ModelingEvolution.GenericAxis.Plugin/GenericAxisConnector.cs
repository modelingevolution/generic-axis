using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Automation;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Plugin;

/// <summary>
/// Keeps every configured generic axis attached (design.md § Plugin): a 1 s tick attempts
/// <c>ConnectAsync</c> on each tracked, unconnected device.
///
/// <para>
/// The motion contract has no connect surface, so a host-side connector could only reach it by
/// casting to a vendor type. The lifecycle therefore lives in the plugin. The factory hands every
/// instance it builds to <see cref="Track"/>, because the live device sits in the host's registry,
/// which a plugin cannot read.
/// </para>
///
/// <para>
/// Retry and reporting policy: see <see cref="ReportFailure"/>. Every failed attempt is logged with its
/// error class in front and its message verbatim (protocol.md § Errors and debugging).
/// </para>
/// </summary>
public sealed class GenericAxisConnector : BackgroundService
{
    /// <summary>How often the tracked devices are reconciled.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a device waits after a failure other than <c>LeaseHeld</c>.</summary>
    internal static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<DeviceId, ModbusAxisDevice> _devices = new();
    // Only the key carries meaning: "already warned about this outage".
    private readonly ConcurrentDictionary<DeviceId, byte> _reported = new();
    // Earliest time (TimeProvider timestamp) of the next attempt after a non-lease failure.
    private readonly ConcurrentDictionary<DeviceId, long> _notBefore = new();
    // The one attach attempt in flight per device. A connect can spend the whole lease timeout (30 s by
    // default, unbounded when configured 0) waiting on a live commander, so the tick never awaits it:
    // each device attaches on its own task and one slow axis cannot hold up another (review #13).
    private readonly ConcurrentDictionary<DeviceId, Task> _attempts = new();
    private readonly IDeviceQuery? _devicesQuery;
    private readonly ILogger<GenericAxisConnector>? _logger;
    private readonly TimeProvider _time;

    /// <param name="devicesQuery">The host's read port, used to forget a device the operator removed.</param>
    /// <param name="logger">Where a refused lease and a dead PLC are told apart.</param>
    /// <param name="timeProvider">Clock for the retry schedule; the system clock when absent.</param>
    public GenericAxisConnector(
        IDeviceQuery? devicesQuery = null,
        ILogger<GenericAxisConnector>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _devicesQuery = devicesQuery;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Takes a newly built device, replacing any earlier instance for the same id (the read model
    /// rebuilds and disposes the device on every reconfigure).
    /// </summary>
    public void Track(DeviceId id, ModbusAxisDevice device)
    {
        _devices[id] = device;
        _notBefore.TryRemove(id, out _);
        _logger?.LogInformation("Tracking generic axis {Device} ({Address}) for connection", id, device.Address);
    }

    /// <summary>Stops reconciling a device that has been removed from the hub.</summary>
    public void Forget(DeviceId id)
    {
        _reported.TryRemove(id, out _);
        _notBefore.TryRemove(id, out _);
        if (_devices.TryRemove(id, out _))
            _logger?.LogInformation("No longer tracking generic axis {Device}", id);
    }

    /// <summary>True when <paramref name="device"/> is the instance tracked under <paramref name="id"/>.</summary>
    internal bool IsTracking(DeviceId id, ModbusAxisDevice device) =>
        _devices.TryGetValue(id, out var current) && ReferenceEquals(current, device);

    /// <summary>True when the next tick may attempt <paramref name="id"/>.</summary>
    internal bool IsDue(DeviceId id) =>
        !_notBefore.TryGetValue(id, out var notBefore) || _time.GetTimestamp() >= notBefore;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.LogInformation(
            "Generic axis connector running (tick {Tick} s, retry after a failure {Retry} s)",
            TickInterval.TotalSeconds, FailureRetryInterval.TotalSeconds);

        using var timer = new PeriodicTimer(TickInterval, _time);
        try
        {
            do
            {
                foreach (var (id, device) in _devices)
                {
                    try
                    {
                        Reconcile(id, device, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        // One device's failure (a host query that throws, for example) must not stop the
                        // connector for every other generic axis (review #27).
                        ReportFailure(id, ex);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown; the in-flight attempts see the same token.
        }
        finally
        {
            await DrainAsync().ConfigureAwait(false);
        }
    }

    private void Reconcile(DeviceId id, ModbusAxisDevice device, CancellationToken ct)
    {
        // Removed from the hub: the read model disposed it and will never rebuild it.
        if (_devicesQuery is not null && _devicesQuery.GetById(id) is null)
        {
            Forget(id);
            return;
        }

        // A stale instance a reconfigure already replaced.
        if (!IsTracking(id, device)) return;

        if (device.IsConnected)
        {
            _reported.TryRemove(id, out _);
            return;
        }

        if (!IsDue(id)) return;

        _ = StartAttempt(id, device.OwnerId, device.ConnectAsync, ct);
    }

    /// <summary>
    /// Starts one attach attempt for <paramref name="id"/> on its own task, unless one is already in
    /// flight for that device. The caller never waits for it.
    /// </summary>
    /// <returns>The attempt that was started, or <see langword="null"/> when the device already has one
    /// in flight. A finished attempt removes itself from <see cref="AttemptFor"/> at once, so a caller
    /// that needs to await it holds this reference rather than looking it up later (review #28).</returns>
    internal Task? StartAttempt(DeviceId id, int ownerId, Func<CancellationToken, Task> connect, CancellationToken ct)
    {
        var start = new Task<Task>(() => AttachAsync(id, ownerId, connect, ct));
        var attempt = start.Unwrap();
        if (!_attempts.TryAdd(id, attempt)) return null;

        attempt.ContinueWith(
            finished =>
            {
                _attempts.TryRemove(new KeyValuePair<DeviceId, Task>(id, finished));
                // Observed here: the only exception AttachAsync lets out is the shutdown cancellation.
                _ = finished.Exception;
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        start.Start(TaskScheduler.Default);
        return attempt;
    }

    /// <summary>The attempt in flight for <paramref name="id"/>, or <see langword="null"/>.</summary>
    internal Task? AttemptFor(DeviceId id) => _attempts.TryGetValue(id, out var attempt) ? attempt : null;

    /// <summary>Waits for every attempt in flight to finish; used at shutdown.</summary>
    internal async Task DrainAsync()
    {
        try
        {
            await Task.WhenAll(_attempts.Values).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The attempts were cancelled by the shutdown token; nothing to report.
        }
    }

    /// <summary>
    /// Runs one attach attempt and applies the reporting and retry policy to its outcome. Takes the
    /// connect as a delegate so the policy is tested against every failure shape, including the raw
    /// socket exceptions that do not arrive as <see cref="MotionException"/>.
    /// </summary>
    internal async Task AttachAsync(DeviceId id, int ownerId, Func<CancellationToken, Task> connect,
        CancellationToken ct)
    {
        try
        {
            await connect(ct).ConfigureAwait(false);
            _reported.TryRemove(id, out _);
            _notBefore.TryRemove(id, out _);
            _logger?.LogInformation("Generic axis {Device} attached as owner {Owner}", id, ownerId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ReportFailure(id, ex);
        }
    }

    /// <summary>
    /// Reports a failed attach by its error class (protocol.md § Errors and debugging) and schedules
    /// the next attempt. Every line leads with the class and carries the failure's message verbatim.
    /// Nothing is re-worded, and nothing is reported under a class it does not belong to.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Commander / LeaseHeld</b>: Information on every attempt, retried on the next 1 s tick,
    /// so the lease is taken as soon as the other commander dies.</item>
    /// <item><b>Protocol</b> (<c>ProtocolMismatch</c> at attach), other <b>Commander</b> refusals
    /// (<c>OutOfRange</c>: the configured reading range contradicts the published travel) and
    /// <b>unclassified</b> failures: Error on every attempt, retried every 5 s. None of them fixes
    /// itself and none may read like a cable fault. Retrying lets a corrected PLC or configuration
    /// attach without a restart.</item>
    /// <item><b>Transport</b>, and <b>Machine</b> should one ever reach attach (an axis in ErrorStop
    /// still attaches and shows its fault): Warning once per outage, then Debug, retried every 5 s. A
    /// successful attach and <see cref="Forget"/> re-arm the warning.</item>
    /// </list>
    /// </remarks>
    internal void ReportFailure(DeviceId id, Exception ex)
    {
        var failure = Classify(ex);

        if (ex is MotionException { Error: MotionError.LeaseHeld })
        {
            _notBefore.TryRemove(id, out _);
            _logger?.LogInformation(
                "{ErrorClass}: generic axis {Device} did not attach: {ErrorMessage} (next attempt in {Seconds} s)",
                failure.Label, id, failure.Message, TickInterval.TotalSeconds);
            return;
        }

        _notBefore[id] = _time.GetTimestamp() + (long)(FailureRetryInterval.TotalSeconds * _time.TimestampFrequency);

        if (failure.Class is ErrorClass.Protocol or ErrorClass.Commander or null)
        {
            _logger?.LogError(ex,
                "{ErrorClass}: generic axis {Device} did not attach: {ErrorMessage} (next attempt in {Seconds} s)",
                failure.Label, id, failure.Message, FailureRetryInterval.TotalSeconds);
        }
        else if (_reported.TryAdd(id, 0))
        {
            _logger?.LogWarning(ex,
                "{ErrorClass}: generic axis {Device} did not attach: {ErrorMessage} (next attempt in {Seconds} s)",
                failure.Label, id, failure.Message, FailureRetryInterval.TotalSeconds);
        }
        else
        {
            _logger?.LogDebug(ex,
                "{ErrorClass}: generic axis {Device} still did not attach: {ErrorMessage}",
                failure.Label, id, failure.Message);
        }
    }

    /// <summary>The class of an attach failure, what the log line leads with, and its message.</summary>
    /// <param name="Class">The protocol class, or <see langword="null"/> when no class can be claimed.</param>
    /// <param name="Label">The class name, or the exception's type name when unclassified.</param>
    /// <param name="Message">The failure's own message, unchanged. A non-motion Transport exception is
    /// prefixed with its type so the reader knows what threw it.</param>
    internal readonly record struct AttachFailure(ErrorClass? Class, string Label, string Message);

    /// <summary>
    /// Classifies an attach failure. A <see cref="MotionException"/> is classified only by
    /// <see cref="MotionErrorClasses.Of"/>, the driver's single map. A socket, IO or timeout exception
    /// from the Modbus client is <see cref="ErrorClass.Transport"/>: the connect or the socket failed.
    /// Any other exception is unclassified, because no observed cause justifies a class.
    /// </summary>
    internal static AttachFailure Classify(Exception ex) => ex switch
    {
        MotionException { Error: { } error } when MotionErrorClasses.Of(error) is var c =>
            new(c, c.ToString(), ex.Message),
        SocketException or IOException or TimeoutException =>
            new(ErrorClass.Transport, nameof(ErrorClass.Transport), $"{ex.GetType().FullName}: {ex.Message}"),
        _ => new(null, ex.GetType().FullName!, ex.Message),
    };
}
