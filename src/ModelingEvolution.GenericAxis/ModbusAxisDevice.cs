using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// One axis behind one PLC endpoint: one channel, one heartbeat, one engine (design § Device lifecycle; mirrors
/// delta-positioner's <c>DeltaPositioner</c>). <see cref="ModbusLinearTrack"/> and <see cref="ModbusPositioner"/>
/// each add exactly one device marker, so <see cref="IMotionDevice.Kind"/> is never ambiguous.
/// </summary>
public abstract class ModbusAxisDevice : IMotionDevice, IAsyncDisposable
{
    private readonly IModbusChannel _channel;
    private readonly AxisHeartbeat _heartbeat;
    private readonly AxisEngine _engine;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private volatile bool _connected;
    private volatile bool _disposed;

    private protected ModbusAxisDevice(DeviceId id, GenericAxisOptions options, ushort ownerId,
        ILoggerFactory? loggerFactory, TimeProvider? time, Func<GenericAxisOptions, ILogger?, IModbusChannel>? channelFactory,
        Func<AxisEngine, IMotionAxis> leafFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (ownerId == AdvisoryLease.Unowned)
            throw new ArgumentOutOfRangeException(nameof(ownerId),
                "0 is the unowned marker in LeaseOwner; give this station a non-zero unique id");

        Id = id;
        Options = options;
        OwnerId = ownerId;
        var clock = time ?? TimeProvider.System;
        _logger = loggerFactory?.CreateLogger(GetType().FullName ?? nameof(ModbusAxisDevice));

        _channel = channelFactory is null
            ? new ModbusChannel(options.Host, options.Port, _logger, new PriorityGate(clock), options.Name, options.Map)
            : channelFactory(options, _logger);
        _heartbeat = new AxisHeartbeat(options.Name, _channel, (byte)options.UnitId, options.Map, ownerId,
            options.HeartbeatInterval, _logger, clock);
        _engine = new AxisEngine(options, _channel, ownerId, _logger, clock);
        _heartbeat.Ticked += (_, snapshot) => _engine.OnTick(snapshot);
        _heartbeat.TickFailed += (_, error) => _engine.OnTickFailed(error);

        Axis = leafFactory(_engine);
        Axes = [Axis];
        Address = new Uri($"modbus://{options.Host}:{options.Port}/{options.UnitId}");
    }

    /// <inheritdoc/>
    public DeviceId Id { get; }

    /// <summary>The axis options, validated.</summary>
    public GenericAxisOptions Options { get; }

    /// <summary>This station's owner id in <c>LeaseOwner</c>.</summary>
    public ushort OwnerId { get; }

    /// <summary><c>modbus://host:port/unit</c>.</summary>
    public Uri Address { get; }

    /// <summary>The attach lifecycle flag (not the socket state): true from a completed <see cref="ConnectAsync"/>
    /// until <see cref="DisconnectAsync"/> or disposal.</summary>
    public bool IsConnected => _connected;

    /// <summary>The effective machine limits and their source.</summary>
    public AxisLimits Limits => _engine.Limits;

    /// <summary>The last heartbeat tick's snapshot, for diagnostics UIs.</summary>
    public PlcSnapshot? LastSnapshot => _engine.LastSnapshot;

    /// <summary>The device's one axis.</summary>
    public IMotionAxis Axis { get; }

    /// <inheritdoc/>
    public IReadOnlyList<IMotionAxis> Axes { get; }

    /// <inheritdoc/>
    public IMotionAxis this[string name] =>
        string.Equals(name, Axis.Name, StringComparison.OrdinalIgnoreCase)
            ? Axis
            : throw AxisErrors.Create(name, MotionError.UnknownAxis,
                $"device '{Id}' has no axis named '{name}'; its axis is '{Axis.Name}'");

    /// <summary>Raised after a completed attach.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised after a disconnect.</summary>
    public event EventHandler? Disconnected;

    internal AxisHeartbeat Heartbeat => _heartbeat;

    internal AxisEngine Engine => _engine;

    /// <summary>
    /// Attaches (design § Device lifecycle): connect → read the status block before any write (map version and
    /// limit publication checked; a PLC that speaks another map is never written to) → take the lease → clear a
    /// predecessor's <c>WatchdogFault</c> → engine attach → start the beat.
    /// </summary>
    /// <exception cref="MotionException"><c>CommunicationLost</c> (Transport) for an unreachable PLC;
    /// <c>ProtocolMismatch</c> (Protocol) for another map version or an invalid limit publication (ADR-35);
    /// <c>OutOfRange</c> (Commander) when the configured reading range does not contain the travel; <c>LeaseHeld</c>
    /// (Commander) when another commander keeps beating.</exception>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connected) return;
            await AttachAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task AttachAsync(CancellationToken ct)
    {
        var o = Options;
        var leaseTaken = false;
        try
        {
            await _channel.ConnectAsync(ct).ConfigureAwait(false);

            var first = await _heartbeat.ReadSnapshotAsync(ChannelPriority.Move, ct).ConfigureAwait(false);
            CheckMap(first.Status);

            await _heartbeat.AcquireAsync(o.LeaseTimeout, ct).ConfigureAwait(false);
            leaseTaken = true;

            await _heartbeat.ClearWatchdogFaultAsync(ct).ConfigureAwait(false);

            var fresh = await _heartbeat.ReadSnapshotAsync(ChannelPriority.Move, ct).ConfigureAwait(false);
            CheckMap(fresh.Status);
            await _engine.AttachAsync(fresh, ct).ConfigureAwait(false);

            _heartbeat.Start();
        }
        catch (Exception ex)
        {
            _engine.Detach();
            if (leaseTaken) await _heartbeat.StopAsync().ConfigureAwait(false);
            try
            {
                await _channel.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception closeError)
            {
                _logger?.LogDebug(closeError, "{Axis}: closing the channel after a failed attach threw", o.Name);
            }

            if (ex is MotionException { Error: MotionError.CommunicationLost or MotionError.LeaseHeld })
                _logger?.LogWarning("{Axis}: attach to {Address} failed. {Message}", o.Name, Address, ex.Message);
            else if (ex is MotionException { Error: MotionError.ProtocolMismatch } && !ReferenceEquals(ex, _refusalLogged))
                _logger?.LogError("{Axis}: attach to {Address} refused. {Message}", o.Name, Address, ex.Message);
            throw;
        }

        _connected = true;
        var limits = _engine.Limits;
        _logger?.LogInformation(
            "{Axis}: attached as owner {Owner}: {Name}@{Host}:{Port}/{Unit} (holding C {CommandBase}, input S {StatusBase}), limits "
            + "{Min}..{Max}, max velocity {MaxVelocity} ({Source})",
            o.Name, OwnerId, o.Name, o.Host, o.Port, o.UnitId, o.Map.CommandBase, o.Map.StatusBase,
            limits.TravelMin, limits.TravelMax, limits.MaxVelocity, limits.SourceText);
        Connected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Map version (S+14) and limit publication (S+8…S+13) before any write — both <c>ProtocolMismatch</c>, the PLC
    /// answering outside the protocol (ADR-35) — and the configured reading range against the effective travel
    /// (<c>OutOfRange</c>: the configuration contradicts the machine). Every refusal logs at Error as well as throwing.
    /// </summary>
    private void CheckMap(StatusBlock s)
    {
        var o = Options;
        var m = o.Map;
        MotionException? refusal = null;
        if (AxisEngine.BlockViolation(m, s, out var mapVersion) is { } violation)
        {
            refusal = AxisErrors.Create(o.Name, MotionError.ProtocolMismatch,
                mapVersion
                    ? "attach refused; nothing was written"
                    : "attach refused: the limit publication is partial or not sane; nothing was written",
                violation);
        }
        else
        {
            var limits = AxisEngine.ComputeLimits(s, o).Units;
            if (limits.Source != LimitSource.None)
            {
                var plc = limits.Source == LimitSource.Plc;
                if (o.ReadMin is { } rmin && rmin > limits.TravelMin)
                    refusal = AxisErrors.Create(o.Name, MotionError.OutOfRange,
                        $"attach refused: configured ReadMin {Fmt(rmin)} is above TravelMin {Fmt(limits.TravelMin)} "
                        + $"({limits.SourceText}), expected ReadMin ≤ TravelMin",
                        plc ? [AxisErrors.Read(m, RegisterField.TravelMin, s.TravelMin)] : []);
                else if (o.ReadMax is { } rmax && rmax < limits.TravelMax)
                    refusal = AxisErrors.Create(o.Name, MotionError.OutOfRange,
                        $"attach refused: configured ReadMax {Fmt(rmax)} is below TravelMax {Fmt(limits.TravelMax)} "
                        + $"({limits.SourceText}), expected ReadMax ≥ TravelMax",
                        plc ? [AxisErrors.Read(m, RegisterField.TravelMax, s.TravelMax)] : []);
            }
        }

        if (refusal is null) return;
        _logger?.LogError("{Message}", refusal.Message);
        _refusalLogged = refusal;
        throw refusal;
    }

    /// <summary>The attach refusal <see cref="CheckMap"/> already logged, so the attach path does not log it twice.</summary>
    private MotionException? _refusalLogged;

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static string Fmt(double value) => value.ToString("0.###", Inv);

    /// <summary>
    /// Clean disconnect (ADR-8): Stop (failure logged, teardown continues) → Enable 0 → stop beating and release the
    /// lease (the PLC disarms without a trip) → close the channel.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connected) return;
            await DetachAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task DetachAsync()
    {
        _engine.BeginDetach();
        try
        {
            await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MotionException or OperationCanceledException)
        {
            _logger?.LogWarning("{Axis}: Stop before disconnecting failed — {Message}", Options.Name, ex.Message);
        }

        try
        {
            await _engine.DisableForDetachAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MotionException or OperationCanceledException)
        {
            _logger?.LogWarning("{Axis}: Enable 0 before disconnecting failed — {Message}", Options.Name, ex.Message);
        }

        _engine.Detach();
        await _heartbeat.StopAsync().ConfigureAwait(false);
        try
        {
            await _channel.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{Axis}: closing the channel threw", Options.Name);
        }

        _connected = false;
        _logger?.LogInformation("{Axis}: disconnected from {Address}", Options.Name, Address);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public Task HomeAllAsync(CancellationToken ct = default) => Axis.HomeAsync(ct);

    /// <summary>Stops the axis. rw2's station STOP reaches every <see cref="IMotionDevice"/> through here (FR-5).</summary>
    public Task StopAllAsync(CancellationToken ct = default) => Axis.StopAsync(ct);

    /// <summary><see cref="DisconnectAsync"/> then teardown.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Axis}: disconnect during disposal failed", Options.Name);
        }

        Teardown();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Abandons the beat and tears down with <b>no network I/O</b> — the in-process "kill". The lease is not
    /// released; the PLC watchdog stops the axis one window after the last beat.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _heartbeat.Abandon();
        Teardown();
        GC.SuppressFinalize(this);
    }

    private void Teardown()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;
        _engine.Dispose();
        _channel.Dispose();
    }
}

/// <summary>A PLC-fronted linear track: one <see cref="ModbusLinearAxis"/> (<c>carriage</c> from the plugin).</summary>
public sealed class ModbusLinearTrack : ModbusAxisDevice, ILinearTrack
{
    /// <summary>Builds the device. No network I/O until <see cref="ModbusAxisDevice.ConnectAsync"/>.</summary>
    /// <exception cref="ArgumentException">An option breaks its rule.</exception>
    public ModbusLinearTrack(DeviceId id, GenericAxisOptions options, ushort ownerId,
        ILoggerFactory? loggerFactory = null, TimeProvider? time = null)
        : this(id, options, ownerId, loggerFactory, time, null)
    {
    }

    internal ModbusLinearTrack(DeviceId id, GenericAxisOptions options, ushort ownerId, ILoggerFactory? loggerFactory,
        TimeProvider? time, Func<GenericAxisOptions, ILogger?, IModbusChannel>? channelFactory)
        : base(id, (options ?? throw new ArgumentNullException(nameof(options))) with { Kind = AxisKind.Linear },
            ownerId, loggerFactory, time, channelFactory, engine => new ModbusLinearAxis(engine))
    {
    }

    /// <summary>The track's axis.</summary>
    public ModbusLinearAxis Carriage => (ModbusLinearAxis)Axis;
}

/// <summary>A PLC-fronted rotary positioner: one <see cref="ModbusRotaryAxis"/> (<c>turntable</c> from the plugin).</summary>
public sealed class ModbusPositioner : ModbusAxisDevice, IPositioner
{
    /// <summary>Builds the device. No network I/O until <see cref="ModbusAxisDevice.ConnectAsync"/>.</summary>
    /// <exception cref="ArgumentException">An option breaks its rule.</exception>
    public ModbusPositioner(DeviceId id, GenericAxisOptions options, ushort ownerId,
        ILoggerFactory? loggerFactory = null, TimeProvider? time = null)
        : this(id, options, ownerId, loggerFactory, time, null)
    {
    }

    internal ModbusPositioner(DeviceId id, GenericAxisOptions options, ushort ownerId, ILoggerFactory? loggerFactory,
        TimeProvider? time, Func<GenericAxisOptions, ILogger?, IModbusChannel>? channelFactory)
        : base(id, (options ?? throw new ArgumentNullException(nameof(options))) with { Kind = AxisKind.Rotary },
            ownerId, loggerFactory, time, channelFactory, engine => new ModbusRotaryAxis(engine))
    {
    }

    /// <summary>The positioner's axis.</summary>
    public ModbusRotaryAxis Turntable => (ModbusRotaryAxis)Axis;
}
