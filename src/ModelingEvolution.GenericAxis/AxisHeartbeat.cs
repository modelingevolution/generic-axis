using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The client half of FR-11 (protocol § FR-11; mirrors delta-positioner's <c>DeltaHeartbeat</c>): lease
/// acquisition, the connection-lifetime beat, and the status tick.
///
/// <para>
/// <b>One tick is both heartbeat and status poll</b> (ADR-15): write <c>Heartbeat</c>, read C+9…C+11, read
/// S+0…S+14, all on <see cref="ChannelPriority.Heartbeat"/>. A failed tick raises <see cref="TickFailed"/> and the
/// loop carries on — the channel reconnects on the next tick's transaction.
/// </para>
///
/// <para>
/// <b>Never beat 0.</b> The register powers up at 0, so a first beat of 0 is no change and would leave the PLC
/// watchdog unarmed.
/// </para>
/// </summary>
internal sealed class AxisHeartbeat : IAsyncDisposable
{
    private readonly IModbusChannel _channel;
    private readonly byte _unit;
    private readonly RegisterMap _map;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly string _axis;

    private CancellationTokenSource? _stop;
    private Task? _loop;
    private ushort _beat;
    private long _ticks;
    private volatile bool _leaseHeld;
    private ushort _lastWatchdogFault;
    private string? _lastRefusal; // tick loop only

    public AxisHeartbeat(string axis, IModbusChannel channel, byte unit, RegisterMap map, ushort ownerId,
        TimeSpan interval, ILogger? logger = null, TimeProvider? time = null)
    {
        if (ownerId == AdvisoryLease.Unowned)
            throw new ArgumentOutOfRangeException(nameof(ownerId),
                "0 is the unowned marker in LeaseOwner; an owner id must be a non-zero station-unique 16-bit value");

        _axis = axis;
        _channel = channel;
        _unit = unit;
        _map = map;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        OwnerId = ownerId;
        Interval = interval;
    }

    /// <summary>This driver's station-unique owner id.</summary>
    public ushort OwnerId { get; }

    /// <summary>The tick period (≥ 5 Hz).</summary>
    public TimeSpan Interval { get; }

    /// <summary>The last value written into <c>Heartbeat</c>; never 0 once beating.</summary>
    public ushort LastBeat => Volatile.Read(ref _beat);

    /// <summary>Completed ticks since construction.</summary>
    public long TickCount => Interlocked.Read(ref _ticks);

    /// <summary>Whether this driver wrote its id into <c>LeaseOwner</c> and has not released it.</summary>
    public bool LeaseHeld => _leaseHeld;

    /// <summary>Whether the tick loop runs.</summary>
    public bool IsBeating => _loop is { IsCompleted: false };

    /// <summary>Raised after every successful tick with what it read.</summary>
    public event EventHandler<PlcSnapshot>? Ticked;

    /// <summary>Raised when a tick fails after the channel's retry.</summary>
    public event EventHandler<MotionException>? TickFailed;

    /// <summary>
    /// Issue #7: run inside the tick when it reads <c>LeaseOwner = 0</c> while this driver holds the lease — a PLC
    /// restart or a clean release, never another commander (protocol § FR-11 "Advisory lease"). It re-attaches in
    /// place and returns the snapshot the tick then reports, or <see langword="null"/> when the device is not attached
    /// for commanding (the tick then reports what it read). A throw fails the tick like a channel failure.
    /// </summary>
    internal Func<PlcSnapshot, CancellationToken, Task<PlcSnapshot?>>? UnownedLease { get; set; }

    // ═══════════════════════ lease ═══════════════════════

    /// <summary>
    /// Takes the lease (protocol § FR-11 "Advisory lease"). Unowned or own id → write own id. Foreign id → watch
    /// <c>Heartbeat</c> (and <c>LeaseOwner</c>) every <see cref="Interval"/>, <b>continuously</b> (ADR-7), and take it
    /// once the beat has been unchanged for <see cref="AdvisoryLease.Expiry"/>.
    /// </summary>
    /// <param name="timeout">How long a live foreign lease is watched; <see langword="null"/> = until
    /// <paramref name="ct"/> fires.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="MotionException"><see cref="MotionError.LeaseHeld"/> naming the owner when the timeout
    /// elapses with the incumbent still beating; <see cref="MotionError.CommunicationLost"/> on a dead channel.</exception>
    public async Task<LeaseDecision> AcquireAsync(TimeSpan? timeout, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var (beat, owner) = await ReadBeatAndOwnerAsync(ct).ConfigureAwait(false);
        var lastChange = _time.GetTimestamp();
        var lastRefusalLog = long.MinValue;

        while (true)
        {
            var decision = AdvisoryLease.Evaluate(owner, _time.GetElapsedTime(lastChange), AdvisoryLease.Expiry, OwnerId);
            if (decision.Granted)
            {
                await _channel.WriteRegisterAsync(_unit, _map.LeaseOwner, OwnerId, "take lease",
                    ChannelPriority.Heartbeat, ct).ConfigureAwait(false);
                _leaseHeld = true;
                _logger?.LogInformation(
                    "{Axis}: lease taken on {Host}:{Port} — LeaseOwner ({Register}) = {Owner}; {Reason}",
                    _axis, _channel.Host, _channel.Port, _map.Describe(RegisterField.LeaseOwner), OwnerId, decision.Reason);
                return decision;
            }

            if (lastRefusalLog == long.MinValue || _time.GetElapsedTime(lastRefusalLog) >= AdvisoryLease.Expiry)
            {
                _logger?.LogInformation("{Axis}: not attaching to {Host}:{Port} yet — {Reason}",
                    _axis, _channel.Host, _channel.Port, decision.Reason);
                lastRefusalLog = _time.GetTimestamp();
            }

            if (timeout is { } t && _time.GetElapsedTime(started) >= t)
                throw AxisErrors.Create(_axis, MotionError.LeaseHeld,
                    $"cannot attach to {_channel.Host}:{_channel.Port}: another commander kept beating for the whole "
                    + $"lease timeout of {t.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} s",
                    AxisErrors.Read(_map, RegisterField.LeaseOwner, owner, $"0 or {OwnerId}"),
                    AxisErrors.Read(_map, RegisterField.Heartbeat,
                        $"{beat} (changed {_time.GetElapsedTime(lastChange).TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} s ago)",
                        $"unchanged for {AdvisoryLease.Expiry.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} s"));

            await Task.Delay(Interval, _time, ct).ConfigureAwait(false);

            var (nextBeat, nextOwner) = await ReadBeatAndOwnerAsync(ct).ConfigureAwait(false);
            if (nextOwner != owner || nextBeat != beat) lastChange = _time.GetTimestamp();
            beat = nextBeat;
            owner = nextOwner;
        }
    }

    private async Task<(ushort Beat, ushort Owner)> ReadBeatAndOwnerAsync(CancellationToken ct)
    {
        // C+8 Heartbeat and C+9 LeaseOwner are adjacent: one transaction observes both.
        var words = await _channel.ReadHoldingAsync(_unit, _map.Heartbeat, 2, "read heartbeat and lease owner",
            ChannelPriority.Heartbeat, ct).ConfigureAwait(false);
        return (words[0], words[1]);
    }

    /// <summary>Re-takes an unowned lease while attached (issue #7): writes own id into <c>LeaseOwner</c>, no
    /// watching — 0 is "take it" in the advisory-lease rule.</summary>
    internal async Task RetakeAsync(CancellationToken ct)
    {
        await _channel.WriteRegisterAsync(_unit, _map.LeaseOwner, OwnerId, "re-take unowned lease",
            ChannelPriority.Heartbeat, ct).ConfigureAwait(false);
        _leaseHeld = true;
    }

    /// <summary>Writes <c>WatchdogFault = 0</c> on the stop lane (design § Device lifecycle step 4).</summary>
    public async Task ClearWatchdogFaultAsync(CancellationToken ct)
    {
        await _channel.WriteRegisterAsync(_unit, _map.WatchdogFault, 0, "clear watchdog fault", ChannelPriority.Stop, ct).ConfigureAwait(false);
        _lastWatchdogFault = 0;
        _logger?.LogInformation("{Axis}: WatchdogFault ({Register}) = 0 written on {Host}:{Port}",
            _axis, _map.Describe(RegisterField.WatchdogFault), _channel.Host, _channel.Port);
    }

    // ═══════════════════════ tick ═══════════════════════

    /// <summary>Starts the tick loop. Idempotent while running.</summary>
    public void Start()
    {
        if (IsBeating) return;
        _stop = new CancellationTokenSource();
        // The timer is created here, synchronously, so the first tick is due one interval after Start on the
        // injected clock — not one interval after a thread-pool hop.
        var timer = new PeriodicTimer(Interval, _time);
        // On the thread pool, never on the caller's SynchronizationContext (GA-U-129): the beat is the commander's
        // proof of life to the watchdog and must not queue behind a UI dispatcher or a test scheduler. Started from
        // a context, the loop's awaits resumed on it — on a 2-CPU runner an 850 ms beat gap, a false NotAcknowledged
        // and a real watchdog trip.
        var token = _stop.Token;
        _loop = Task.Run(() => RunAsync(timer, token));
        _logger?.LogInformation("{Axis}: heartbeat started on {Host}:{Port} every {Interval} ms as owner {Owner}",
            _axis, _channel.Host, _channel.Port, Interval.TotalMilliseconds, OwnerId);
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        using var _ = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await TickOnceAsync(ct).ConfigureAwait(false);
                    _lastRefusal = null; // the link recovered: the next refusal is news again
                }
                catch (MotionException ex)
                {
                    // Protocol rule 3 (ADR-37): a refusal is never retried, so a refusing PLC fails every tick. A
                    // refusal identical to the previous tick's logs at Debug; the first one, a changed one, and the
                    // first after a good tick log at Warning. Every other tick failure logs at Warning.
                    var repeated = ModbusChannel.IsRefusal(ex) && ex.Message == _lastRefusal;
                    _lastRefusal = ModbusChannel.IsRefusal(ex) ? ex.Message : null;
                    if (repeated)
                        _logger?.LogDebug("{Axis}: heartbeat tick refused again. {Message}", _axis, ex.Message);
                    else
                        _logger?.LogWarning(ex, "{Axis}: heartbeat tick failed. {Message}", _axis, ex.Message);
                    Raise(TickFailed, ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Review #11: the loop never dies silently. A defect that is neither a transport failure nor the
                    // PLC's answer (those arrive as MotionException) is logged under its own CLR type; no error class
                    // is claimed and no overlay is latched — a CommunicationLost here would be a lie.
                    _logger?.LogError(ex, "{Axis}: heartbeat tick threw {Type}: {Message}; the beat continues",
                        _axis, ex.GetType().FullName, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Asked to stop.
        }
    }

    /// <summary>One tick: beat, watchdog read, status read. Internal so tests can step it deterministically.</summary>
    internal async Task<PlcSnapshot> TickOnceAsync(CancellationToken ct)
    {
        var next = NextBeat(_beat);
        await _channel.WriteRegisterAsync(_unit, _map.Heartbeat, next, "heartbeat", ChannelPriority.Heartbeat, ct).ConfigureAwait(false);
        Volatile.Write(ref _beat, next);
        _logger?.LogTrace("{Axis}: Heartbeat ({Register}) = {Beat}", _axis, _map.Describe(RegisterField.Heartbeat), next);

        var snapshot = await ReadSnapshotAsync(ChannelPriority.Heartbeat, ct).ConfigureAwait(false);
        _logger?.LogTrace(
            "{Axis}: tick read LeaseOwner {Owner}, WatchdogFault {Fault}, trips {Trips}, State {State}, Flags 0x{Flags:X4}, "
            + "ActualPosition {Position}, CommandAck {Ack}",
            _axis, snapshot.LeaseOwner, snapshot.WatchdogFault, snapshot.WatchdogTrips, snapshot.Status.State,
            (ushort)snapshot.Status.Flags, snapshot.Status.ActualPosition, snapshot.Status.CommandAck);

        if (_leaseHeld && snapshot.LeaseOwner == AdvisoryLease.Unowned && UnownedLease is { } retake
            && await retake(snapshot, ct).ConfigureAwait(false) is { } reattached)
            snapshot = reattached;

        if (snapshot.WatchdogFault != 0 && _lastWatchdogFault == 0)
            _logger?.LogError("{Message}", AxisErrors.Message(_axis, MotionError.WatchdogTripped,
                $"WATCHDOG TRIPPED on {_channel.Host}:{_channel.Port}; recovery is Reset, then re-command (no re-home)",
                AxisErrors.Read(_map, RegisterField.WatchdogFault, snapshot.WatchdogFault, "0"),
                AxisErrors.Read(_map, RegisterField.WatchdogTrips, snapshot.WatchdogTrips)));
        _lastWatchdogFault = snapshot.WatchdogFault;

        Interlocked.Increment(ref _ticks);
        Raise(Ticked, snapshot);
        return snapshot;
    }

    /// <summary>Reads holding C+9…C+11 (FC03) and input S+0…S+14 (FC04) on <paramref name="lane"/>. Used by the tick and
    /// by attach.</summary>
    internal async Task<PlcSnapshot> ReadSnapshotAsync(ChannelPriority lane, CancellationToken ct)
    {
        var watchdog = await _channel.ReadHoldingAsync(_unit, _map.LeaseOwner, RegisterMap.WatchdogBlockLength,
            "read lease and watchdog", lane, ct).ConfigureAwait(false);
        CheckLength(watchdog, RegisterSpace.Holding, _map.LeaseOwner, RegisterMap.WatchdogBlockLength, "lease and watchdog block");
        var status = await _channel.ReadInputAsync(_unit, _map.Status, RegisterMap.StatusLength,
            "read status block", lane, ct).ConfigureAwait(false);
        CheckLength(status, RegisterSpace.Input, _map.Status, RegisterMap.StatusLength, "status block");
        return new PlcSnapshot(StatusBlock.Parse(status), watchdog[0], watchdog[1], watchdog[2], _time.GetTimestamp());
    }

    /// <summary>
    /// A read answered with the wrong number of registers is the PLC answering outside the protocol
    /// (<c>ProtocolMismatch</c>, review #11), never an <see cref="ArgumentException"/> from the parser.
    /// </summary>
    private void CheckLength(ushort[] words, RegisterSpace space, ushort address, int expected, string block)
    {
        if (words.Length == expected) return;
        throw AxisErrors.Create(_axis, MotionError.ProtocolMismatch,
            $"the PLC answered a read of the {block} with {words.Length} registers",
            new RegisterRead(block, _map.DescribeRange(space, address, expected),
                $"{words.Length} registers", expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private void Raise<T>(EventHandler<T>? handler, T value)
    {
        try
        {
            handler?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            // A subscriber's defect must not stop the beat: the beat is the PLC's proof that we are alive.
            _logger?.LogError(ex, "{Axis}: a heartbeat subscriber threw", _axis);
        }
    }

    /// <summary>The next beat, 1…65535, skipping 0.</summary>
    internal static ushort NextBeat(ushort current) => Words.NextNonZero(current);

    // ═══════════════════════ stop ═══════════════════════

    /// <summary>
    /// Stops beating and releases the lease: reads <c>LeaseOwner</c> and writes 0 only if it still holds our id.
    /// Writing 0 disarms the PLC watchdog without a trip (protocol "Clean release disarms").
    /// </summary>
    public async Task StopAsync()
    {
        await StopLoopAsync().ConfigureAwait(false);
        if (!_leaseHeld) return;

        try
        {
            var owner = (await _channel.ReadHoldingAsync(_unit, _map.LeaseOwner, 1, "read lease owner",
                ChannelPriority.Stop, CancellationToken.None).ConfigureAwait(false))[0];
            if (owner == OwnerId)
            {
                await _channel.WriteRegisterAsync(_unit, _map.LeaseOwner, AdvisoryLease.Unowned, "release lease",
                    ChannelPriority.Stop, CancellationToken.None).ConfigureAwait(false);
                _logger?.LogInformation("{Axis}: lease released on {Host}:{Port} — LeaseOwner ({Register}) = 0",
                    _axis, _channel.Host, _channel.Port, _map.Describe(RegisterField.LeaseOwner));
            }
            else
            {
                _logger?.LogWarning(
                    "{Axis}: not releasing the lease on {Host}:{Port} — LeaseOwner is {Owner}, not ours ({Mine})",
                    _axis, _channel.Host, _channel.Port, owner, OwnerId);
            }
        }
        catch (MotionException ex)
        {
            _logger?.LogWarning("{Axis}: could not release the lease on {Host}:{Port}; it expires one watchdog window "
                + "after the last beat — {Message}", _axis, _channel.Host, _channel.Port, ex.Message);
        }

        _leaseHeld = false;
    }

    private async Task StopLoopAsync()
    {
        var stop = _stop;
        var loop = _loop;
        if (stop is null || loop is null) return;

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        stop.Dispose();
        _stop = null;
        _loop = null;
    }

    /// <summary>
    /// Stops beating with <b>no network I/O</b> — the teardown a synchronous <c>Dispose</c> can honestly perform,
    /// and the in-process "kill". The lease is left to expire; the PLC watchdog stops the axis one window later.
    /// </summary>
    public void Abandon()
    {
        var stop = _stop;
        if (stop is null) return;
        try { stop.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
        _stop = null;
        _loop = null;
        _logger?.LogWarning("{Axis}: heartbeat abandoned on {Host}:{Port} without releasing the lease; the PLC watchdog "
            + "trips {Window} s after the last beat", _axis, _channel.Host, _channel.Port,
            RegisterMap.WatchdogWindow.TotalSeconds);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
