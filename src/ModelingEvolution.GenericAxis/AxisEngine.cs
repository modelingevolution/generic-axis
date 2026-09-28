using System.Globalization;
using Microsoft.Extensions.Logging;
using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>A move's requested speed: an absolute value in unit/s, a share of <c>MaxVelocity</c>, or neither
/// (then <see cref="GenericAxisOptions.DefaultSpeed"/> applies, ADR-18).</summary>
internal readonly record struct SpeedRequest(double? Absolute, Percentage? Share)
{
    public static SpeedRequest Default => new(null, null);

    public static SpeedRequest Of(double? absolute) => new(absolute, null);

    public static SpeedRequest Of(Percentage share) => new(null, share);
}

/// <summary>
/// The unit-free axis (design § Driver components, ADR-6): the state mirror, the nine guards, the command
/// handshake and the supervision budgets, over <see cref="double"/> axis units (mm or °).
///
/// <para>
/// <b>The PLC owns the state machine</b> (ADR-1). <see cref="State"/> is derived from the last tick's snapshot on
/// every read and is never stored as a second copy; the only local additions are the two latched overlays
/// <c>CommunicationLost</c> (ADR-10) and <c>LeaseHeld</c> (ADR-23).
/// </para>
///
/// <para>
/// Lock discipline: one <see cref="Lock"/>, no await under it, events raised outside it. Guards are evaluated under
/// the lock before anything is written; a refusal writes nothing.
/// </para>
/// </summary>
internal sealed class AxisEngine : IDisposable
{
    /// <summary>Design § Command handshake, MoveVelocity row: ContinuousMotion must show within 500 ms of the ack.</summary>
    public static readonly TimeSpan ContinuousMotionConfirmTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Lock _sync = new();
    private readonly GenericAxisOptions _options;
    private readonly IModbusChannel _channel;
    private readonly byte _unit;
    private readonly RegisterMap _map;
    private readonly ushort _ownerId;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly string _unitSymbol;
    private readonly string _speedSymbol;

    private bool _attached;
    private PlcSnapshot? _snapshot;
    private long _tickNo;
    private Overlay? _overlay;
    private bool _tickOkSinceOverlay;
    private ushort _seq;
    private bool _enable;
    private Running? _running;
    private LimitState _limits = LimitState.None;
    private string? _limitWarning;
    private TaskCompletionSource _changed = NewSignal();

    public AxisEngine(GenericAxisOptions options, IModbusChannel channel, ushort ownerId, ILogger? logger,
        TimeProvider time)
    {
        _options = options;
        _channel = channel;
        _unit = (byte)options.UnitId;
        _map = options.Map;
        _ownerId = ownerId;
        _logger = logger;
        _time = time;
        _unitSymbol = options.Kind == AxisKind.Linear ? "mm" : "°";
        _speedSymbol = options.Kind == AxisKind.Linear ? "mm/s" : "°/s";
    }

    /// <summary>The axis name.</summary>
    public string Name => _options.Name;

    /// <summary>The axis options.</summary>
    public GenericAxisOptions Options => _options;

    /// <summary>Raised on every successful tick and on every overlay change.</summary>
    public event EventHandler<AxisStatus>? StatusChanged;

    /// <summary>The PLC state, or the overlay's ErrorStop, or Disabled while detached.</summary>
    public AxisState State
    {
        get { lock (_sync) return StateOf(); }
    }

    /// <summary>The current status (design § State model).</summary>
    public AxisStatus Status
    {
        get { lock (_sync) return StatusOf(); }
    }

    /// <summary>The effective limits and their source.</summary>
    public AxisLimits Limits
    {
        get { lock (_sync) return _limits.Units; }
    }

    /// <summary>The last tick's snapshot, for diagnostics.</summary>
    public PlcSnapshot? LastSnapshot
    {
        get { lock (_sync) return _snapshot; }
    }

    /// <summary>The last <c>CommandSeq</c> this engine wrote (or continued from at attach).</summary>
    public ushort Sequence
    {
        get { lock (_sync) return _seq; }
    }

    /// <summary>The name of the verb in flight, if any.</summary>
    public string? RunningVerb
    {
        get { lock (_sync) return _running?.Verb; }
    }

    /// <summary>The successor of a <c>CommandSeq</c>: 1…65535, never 0.</summary>
    internal static ushort NextSeq(ushort current) => Words.NextNonZero(current);

    // ═══════════════════════ state derivation ═══════════════════════

    private AxisState StateOf() =>
        _overlay is not null
            ? AxisState.ErrorStop
            : _attached && _snapshot is { } s
                ? MapState(s.Status.State)
                : AxisState.Disabled;

    private AxisStatus StatusOf()
    {
        var state = StateOf();
        if (!_attached || _snapshot is not { } s)
            return new AxisStatus(state, null, 0, LimitSwitchState.None, _overlay?.Error);

        var block = s.Status;
        var limits = LimitSwitchState.None;
        if ((block.Flags & StatusFlags.LimitMin) != 0) limits |= LimitSwitchState.Min;
        if ((block.Flags & StatusFlags.LimitMax) != 0) limits |= LimitSwitchState.Max;

        MotionError? error = _overlay?.Error ?? (state == AxisState.ErrorStop ? FaultError(block) : null);
        return new AxisStatus(state, block.Homed ? Words.FromRaw(block.ActualPosition) : null,
            Words.FromRaw(block.ActualVelocity), limits, error);
    }

    /// <summary>Protocol <c>State</c> → SDK <see cref="AxisState"/>: 0–4, 6, 7 are the same numbers; 5 and anything
    /// else read as ErrorStop.</summary>
    internal static AxisState MapState(ushort raw) => raw switch
    {
        0 => AxisState.Disabled,
        1 => AxisState.Standstill,
        2 => AxisState.Homing,
        3 => AxisState.DiscreteMotion,
        4 => AxisState.ContinuousMotion,
        6 => AxisState.Stopping,
        _ => AxisState.ErrorStop,
    };

    private static bool IsKnownState(ushort raw) => raw is <= 4 or 6 or 7;

    /// <summary>Design § State model, <c>MapFault</c>: the <see cref="MotionError"/> of a snapshot in ErrorStop.
    /// A State outside the protocol, or ErrorStop without a fault code, is the PLC breaking the protocol
    /// (<see cref="MotionError.ProtocolMismatch"/>); everything else is the machine's fault by remedy.</summary>
    internal static MotionError FaultError(StatusBlock s) =>
        !IsKnownState(s.State)
            ? MotionError.ProtocolMismatch
            : s.FaultCode switch
            {
                0 => MotionError.ProtocolMismatch,
                1 => MotionError.DriveFault,
                2 => MotionError.LimitTripped,
                3 => MotionError.MotionFailed,
                4 => MotionError.WatchdogTripped,
                5 => MotionError.HomeLatchFailed,
                6 => MotionError.DriveFault,
                7 => MotionError.SafetyStop,
                _ => MotionError.DriveFault,
            };

    /// <summary>The exception for a snapshot in ErrorStop, saying what was read (protocol § Errors and debugging).</summary>
    internal static MotionException FaultException(string axis, RegisterMap map, PlcSnapshot snapshot)
    {
        var s = snapshot.Status;
        var error = FaultError(s);
        if (!IsKnownState(s.State))
            return AxisErrors.Create(axis, error, "the PLC reports a State map version 1 does not define",
                AxisErrors.Read(map, "State", map.State, s.State, "0–4, 6, 7"));

        var fault = AxisErrors.Read(map, "FaultCode", map.FaultCode, s.FaultCode);
        return s.FaultCode switch
        {
            0 => AxisErrors.Create(axis, error, "the PLC reports ErrorStop without a fault code",
                AxisErrors.Read(map, "State", map.State, s.State),
                AxisErrors.Read(map, "FaultCode", map.FaultCode, s.FaultCode, "1–7 or ≥ 100")),
            1 => AxisErrors.Create(axis, error, "drive fault", fault),
            2 => AxisErrors.Create(axis, error, $"limit switch tripped ({LimitText(s.Flags)})", fault,
                AxisErrors.Read(map, "Flags", map.Flags, $"0x{(ushort)s.Flags:X4}")),
            3 => AxisErrors.Create(axis, error, "following error", fault),
            4 => AxisErrors.Create(axis, error, "the PLC watchdog tripped; Reset, then re-command (no re-home)", fault,
                AxisErrors.Read(map, "WatchdogFault", map.WatchdogFault, snapshot.WatchdogFault),
                AxisErrors.Read(map, "WatchdogTrips", map.WatchdogTrips, snapshot.WatchdogTrips)),
            5 => AxisErrors.Create(axis, error, "homing failed in the PLC sequence", fault),
            6 => AxisErrors.Create(axis, error, "the PLC lost its drive link", fault),
            7 => AxisErrors.Create(axis, error, "safety circuit (E-stop / guard)", fault),
            _ => AxisErrors.Create(axis, error, $"vendor fault {s.FaultCode}", fault),
        };
    }

    private static string LimitText(StatusFlags flags) =>
        (flags & (StatusFlags.LimitMin | StatusFlags.LimitMax)) switch
        {
            StatusFlags.LimitMin => "LimitMin",
            StatusFlags.LimitMax => "LimitMax",
            StatusFlags.LimitMin | StatusFlags.LimitMax => "LimitMin and LimitMax",
            _ => "no switch flag set",
        };

    // ═══════════════════════ attach / detach ═══════════════════════

    /// <summary>
    /// Design § Command handshake step 1: continue the sequence from <c>CommandAck</c>, take the Enable level from the
    /// PLC state, and clear stale edge bits a dead predecessor may have left — one FC16 of
    /// <c>[Command, CommandSeq = CommandAck]</c>, which is not a command (the sequence equals the ack).
    /// </summary>
    public async Task AttachAsync(PlcSnapshot fresh, CancellationToken ct)
    {
        var s = fresh.Status;
        var state = MapState(s.State);
        var enable = state is not (AxisState.Disabled or AxisState.ErrorStop);
        var word = (ushort)(enable ? CommandBits.Enable : CommandBits.None);

        await _channel.WriteRegistersAsync(_unit, _map.Command, [word, s.CommandAck], "clear stale edges at attach",
            ChannelPriority.Move, ct);
        _logger?.LogInformation(
            "{Axis}: attach — Command ({Command}) = 0x{Word:X4}, CommandSeq ({Seq}) = {Ack} (continuing from CommandAck); "
            + "PLC state {State}",
            Name, _map.Describe(_map.Command), word, _map.Describe(_map.CommandSeq), s.CommandAck, state);

        AxisStatus status;
        lock (_sync)
        {
            _seq = s.CommandAck;
            _enable = enable;
            _snapshot = fresh;
            _overlay = null;
            _tickOkSinceOverlay = false;
            _attached = true;
            _tickNo++;
            UpdateLimits(s, attach: true);
            status = StatusOf();
            SignalLocked();
        }

        RaiseStatus(status);
    }

    /// <summary>Stops mirroring: pending commands fail with CommunicationLost, the state reads Disabled.</summary>
    public void Detach()
    {
        Running? running;
        AxisStatus status;
        lock (_sync)
        {
            if (!_attached) return;
            _attached = false;
            running = _running;
            status = StatusOf();
            SignalLocked();
        }

        running?.Cancel();
        RaiseStatus(status);
    }

    // ═══════════════════════ ticks ═══════════════════════

    /// <summary>A successful heartbeat tick.</summary>
    public void OnTick(PlcSnapshot snapshot)
    {
        AxisStatus status;
        AxisState oldState, newState;
        PlcSnapshot? previous;
        bool leaseLost, overlayWasCommsLost;
        lock (_sync)
        {
            if (!_attached) return;
            oldState = StateOf();
            previous = _snapshot;
            overlayWasCommsLost = _overlay?.Error == MotionError.CommunicationLost && !_tickOkSinceOverlay;
            _snapshot = snapshot;
            _tickNo++;
            if (_overlay?.Error == MotionError.CommunicationLost) _tickOkSinceOverlay = true;

            leaseLost = snapshot.LeaseOwner != _ownerId && _overlay?.Error != MotionError.LeaseHeld;
            if (leaseLost)
                _overlay = new Overlay(MotionError.LeaseHeld, AxisErrors.Message(Name, MotionError.LeaseHeld,
                    "another commander took the axis; commanding stopped. Reconnect to take it back",
                    AxisErrors.Read(_map, "LeaseOwner", _map.LeaseOwner, snapshot.LeaseOwner, _ownerId.ToString(Inv))));

            UpdateLimits(snapshot.Status, attach: false);
            newState = StateOf();
            status = StatusOf();
            SignalLocked();
        }

        if (leaseLost)
            _logger?.LogError("{Message}", AxisErrors.Message(Name, MotionError.LeaseHeld,
                "LEASE LOST: another commander took the axis; commanding stopped, the axis shows ErrorStop / LeaseHeld "
                + "until reconnect", AxisErrors.Read(_map, "LeaseOwner", _map.LeaseOwner, snapshot.LeaseOwner,
                    _ownerId.ToString(Inv))));
        if (overlayWasCommsLost)
            _logger?.LogInformation("{Axis}: the PLC answers again; the CommunicationLost overlay stays until ResetAsync",
                Name);

        LogTransition(oldState, newState, previous, snapshot);
        RaiseStatus(status);
    }

    /// <summary>A heartbeat tick that failed after the channel's retry: latch <c>ErrorStop / CommunicationLost</c>.</summary>
    public void OnTickFailed(MotionException error)
    {
        AxisStatus status;
        AxisState oldState;
        bool latched;
        lock (_sync)
        {
            if (!_attached) return;
            oldState = StateOf();
            latched = _overlay is null;
            if (latched)
                _overlay = new Overlay(MotionError.CommunicationLost, error.Message);
            _tickOkSinceOverlay = false;
            status = StatusOf();
            SignalLocked();
        }

        if (latched)
            _logger?.LogError("{Axis}: state {Old} → ErrorStop (overlay CommunicationLost). {Message}",
                Name, oldState, error.Message);
        RaiseStatus(status);
    }

    private void LogTransition(AxisState oldState, AxisState newState, PlcSnapshot? previous, PlcSnapshot snapshot)
    {
        var raw = snapshot.Status;
        // The PLC's own ErrorStop, logged once per new cause — also under an overlay, so a watchdog trip the PLC
        // reports after a link loss is its own Machine/WatchdogTripped line (protocol § Errors and debugging, rule 2).
        var plcFaulted = MapState(raw.State) == AxisState.ErrorStop;
        var wasFaulted = previous is { } p && MapState(p.Status.State) == AxisState.ErrorStop;
        var sameCause = wasFaulted && previous!.Value.Status.State == raw.State
                                   && previous.Value.Status.FaultCode == raw.FaultCode;
        if (plcFaulted && !sameCause)
            _logger?.LogError("{Message}", FaultException(Name, _map, snapshot).Message);

        if (oldState == newState) return;
        _logger?.LogInformation(
            "{Axis}: state {Old} → {New} (State ({Register}) = {Raw}, FaultCode ({FaultRegister}) = {Fault}, Flags 0x{Flags:X4})",
            Name, oldState, newState, _map.Describe(_map.State), raw.State, _map.Describe(_map.FaultCode),
            raw.FaultCode, (ushort)raw.Flags);
    }

    private void UpdateLimits(StatusBlock s, bool attach)
    {
        var next = ComputeLimits(s, _options);
        var changed = next != _limits;
        _limits = next;
        if (!changed && !attach) return;

        switch (next.Units.Source)
        {
            case LimitSource.Plc:
                var mismatch = MismatchText(next.Units, _options);
                if (mismatch is not null && mismatch != _limitWarning)
                    _logger?.LogWarning("{Axis}: the PLC publishes limits that differ from the configured ones — {Mismatch}. "
                        + "The PLC's values are used", Name, mismatch);
                _limitWarning = mismatch;
                break;
            case LimitSource.Configuration:
                _logger?.LogInformation(
                    "{Axis}: the PLC publishes no limits (S+8…S+13 all 0); using the configured values TravelMin {Min} "
                    + "{Unit}, TravelMax {Max} {Unit}, MaxVelocity {MaxV} {Speed}",
                    Name, Fmt(next.Units.TravelMin), _unitSymbol, Fmt(next.Units.TravelMax), _unitSymbol,
                    Fmt(next.Units.MaxVelocity), _speedSymbol);
                break;
            default:
                _logger?.LogWarning(s.LimitsPublished
                        ? "{Axis}: the PLC publishes an invalid limit set (TravelMin {Min}, TravelMax {Max}, MaxVelocity "
                          + "{MaxV} raw); every move is refused"
                        : "{Axis}: no limit source — the PLC publishes none and none are configured. The axis reports "
                          + "status and homes; every move is refused",
                    Name, s.TravelMin, s.TravelMax, s.MaxVelocity);
                break;
        }
    }

    /// <summary>Design § Limits: PLC publication → Configuration → None. Raw values kept for exact comparisons.</summary>
    internal static LimitState ComputeLimits(StatusBlock s, GenericAxisOptions o)
    {
        if (s.LimitsPublished)
        {
            if (!s.LimitsValid) return LimitState.None;
            return new LimitState(s.TravelMin, s.TravelMax, s.MaxVelocity,
                new AxisLimits(Words.FromRaw(s.TravelMin), Words.FromRaw(s.TravelMax), Words.FromRaw(s.MaxVelocity),
                    LimitSource.Plc));
        }

        if (o.ConfiguredTravelMin is { } min && o.ConfiguredTravelMax is { } max && o.ConfiguredMaxVelocity is { } v)
        {
            int rmin = Words.ToRaw(min, "ConfiguredTravelMin", o.Name), rmax = Words.ToRaw(max, "ConfiguredTravelMax", o.Name);
            int rv = Words.ToRaw(v, "ConfiguredMaxVelocity", o.Name);
            return new LimitState(rmin, rmax, rv,
                new AxisLimits(Words.FromRaw(rmin), Words.FromRaw(rmax), Words.FromRaw(rv), LimitSource.Configuration));
        }

        return LimitState.None;
    }

    private string? MismatchText(AxisLimits plc, GenericAxisOptions o)
    {
        var parts = new List<string>(3);
        if (o.ConfiguredTravelMin is { } min && Words.ToRaw(min, "ConfiguredTravelMin") != Words.ToRaw(plc.TravelMin, "TravelMin"))
            parts.Add($"TravelMin PLC {Fmt(plc.TravelMin)} {_unitSymbol} vs configured {Fmt(min)} {_unitSymbol}");
        if (o.ConfiguredTravelMax is { } max && Words.ToRaw(max, "ConfiguredTravelMax") != Words.ToRaw(plc.TravelMax, "TravelMax"))
            parts.Add($"TravelMax PLC {Fmt(plc.TravelMax)} {_unitSymbol} vs configured {Fmt(max)} {_unitSymbol}");
        if (o.ConfiguredMaxVelocity is { } v && Words.ToRaw(v, "ConfiguredMaxVelocity") != Words.ToRaw(plc.MaxVelocity, "MaxVelocity"))
            parts.Add($"MaxVelocity PLC {Fmt(plc.MaxVelocity)} {_speedSymbol} vs configured {Fmt(v)} {_speedSymbol}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // ═══════════════════════ verbs ═══════════════════════

    /// <summary><c>PowerAsync</c>: on = Enable 1 and await Standstill; off = Stop if moving, Enable 0, await Disabled.</summary>
    public Task PowerAsync(bool on, CancellationToken ct) => on
        ? RunVerbAsync("PowerAsync(true)", (_, state) =>
            {
                if (state is not (AxisState.Disabled or AxisState.Standstill))
                    throw RefuseState("PowerAsync(true)", state);
            },
            async token =>
            {
                bool alreadyOn;
                lock (_sync) alreadyOn = _enable && StateOf() == AxisState.Standstill;
                if (!alreadyOn) await EnergiseAsync(token);
            }, ct)
        : RunVerbAsync("PowerAsync(false)", null, async token =>
            {
                var state = State;
                if (state is AxisState.Homing or AxisState.DiscreteMotion or AxisState.ContinuousMotion
                    or AxisState.Stopping)
                    await StopCoreAsync(token);

                var (_, ackTick) = await SendCommandAsync("Enable 0", CommandBits.None, null, false,
                    ChannelPriority.Move, token);
                await AwaitAsync(ackTick - 1,
                    s => MapState(s.Status.State) is AxisState.Disabled or AxisState.ErrorStop,
                    failOnErrorStop: false, _options.EnableTimeout,
                    () => Error(MotionError.DriveFault,
                        $"did not reach Disabled within {Secs(_options.EnableTimeout)} after Enable 0",
                        StateRead("0 (Disabled)")),
                    token);
            }, ct);

    /// <summary>Energise: a fresh 0→1 of Enable (protocol § Enable), then Standstill within the enable budget.</summary>
    private async Task EnergiseAsync(CancellationToken ct)
    {
        bool levelHigh;
        lock (_sync) levelHigh = _enable;
        if (levelHigh)
        {
            // The register already holds Enable 1 while the PLC is Disabled: the PLC energises only on a fresh
            // 0→1, so drop the level first.
            await SendCommandAsync("Enable 0 before a fresh Enable", CommandBits.None, null, false, ChannelPriority.Move, ct);
        }

        var (_, ackTick) = await SendCommandAsync("Enable 1", CommandBits.None, null, true, ChannelPriority.Move, ct);
        await AwaitAsync(ackTick - 1, s => MapState(s.Status.State) == AxisState.Standstill,
            failOnErrorStop: true, _options.EnableTimeout,
            () => Error(MotionError.DriveFault,
                $"did not reach Standstill within {Secs(_options.EnableTimeout)} after Enable 1",
                StateRead("1 (Standstill)")),
            ct);
    }

    /// <summary><c>HomeAsync</c>: energise first when Disabled (FR-3), then the Home edge; Standstill + Homed.</summary>
    public Task HomeAsync(CancellationToken ct) =>
        RunVerbAsync("HomeAsync", (_, state) =>
            {
                if (state is not (AxisState.Disabled or AxisState.Standstill))
                    throw RefuseState("HomeAsync", state);
            },
            async token =>
            {
                if (State == AxisState.Disabled) await EnergiseAsync(token);

                var (_, ackTick) = await SendCommandAsync("Home", CommandBits.Home, null, null, ChannelPriority.Move,
                    token);
                try
                {
                    await AwaitAsync(ackTick - 1,
                        s => MapState(s.Status.State) == AxisState.Standstill && s.Status.Homed,
                        failOnErrorStop: true, _options.HomingTimeout, () => new BudgetExceeded(), token);
                }
                catch (BudgetExceeded)
                {
                    await StopCoreAsync(CancellationToken.None);
                    throw Error(MotionError.HomeLatchFailed,
                        $"homing did not finish within {Secs(_options.HomingTimeout)}; the axis was stopped",
                        StateRead("1 (Standstill)"), FlagsRead("Homed set"));
                }
            }, ct);

    /// <summary><c>MoveAbsoluteAsync</c> (G1–G9). A <paramref name="sense"/> is given only by a rotary axis.</summary>
    public Task MoveAbsoluteAsync(double target, SpeedRequest speed, RotationSense? sense, CancellationToken ct)
    {
        int rawTarget = 0, rawSpeed = 0;
        double reading = 0;
        return RunVerbAsync("MoveAbsoluteAsync", (s, state) =>
            {
                GuardDiscreteMove("MoveAbsoluteAsync", s, state, out reading);
                rawTarget = GuardTarget(target);
                rawSpeed = GuardSpeed(ResolveSpeed(speed), signed: false);
                GuardSense(sense);
            },
            token => DiscreteMoveAsync("MoveAbsoluteAsync", rawTarget, rawSpeed, reading, token), ct);
    }

    /// <summary><c>MoveRelativeAsync</c>: target = reading + Δ, then as MoveAbsolute.</summary>
    public Task MoveRelativeAsync(double delta, SpeedRequest speed, CancellationToken ct)
    {
        int rawTarget = 0, rawSpeed = 0;
        double reading = 0;
        return RunVerbAsync("MoveRelativeAsync", (s, state) =>
            {
                GuardDiscreteMove("MoveRelativeAsync", s, state, out reading);
                rawTarget = GuardTarget(reading + delta);
                rawSpeed = GuardSpeed(ResolveSpeed(speed), signed: false);
            },
            token => DiscreteMoveAsync("MoveRelativeAsync", rawTarget, rawSpeed, reading, token), ct);
    }

    /// <summary><c>MoveVelocityAsync</c>: skips G4 and G7 (ADR-22); returns once ContinuousMotion is observed.</summary>
    public Task MoveVelocityAsync(double velocity, CancellationToken ct)
    {
        int rawVelocity = 0;
        return RunVerbAsync("MoveVelocityAsync", (s, state) =>
            {
                if (state != AxisState.Standstill) throw RefuseState("MoveVelocityAsync", state);
                GuardReadingRange(s);
                GuardLimitSource();
                rawVelocity = GuardSpeed(velocity, signed: true);
            },
            async token =>
            {
                var (_, ackTick) = await SendCommandAsync("MoveVelocity", CommandBits.MoveVelocity,
                    [0, rawVelocity, RawAcceleration()], null, ChannelPriority.Move, token);
                await AwaitAsync(ackTick - 1, s => MapState(s.Status.State) == AxisState.ContinuousMotion,
                    failOnErrorStop: true, ContinuousMotionConfirmTimeout,
                    () => Error(MotionError.MotionFailed,
                        $"ContinuousMotion not observed within {ContinuousMotionConfirmTimeout.TotalMilliseconds} ms "
                        + "of the MoveVelocity ack", StateRead("4 (ContinuousMotion)")),
                    token);
            }, ct);
    }

    /// <summary>
    /// <c>StopAsync</c>: cancels the running verb first — so it cannot issue a write after the Stop — then writes
    /// the Stop edge on the stop lane, whatever the local state (no G1, no G2).
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        Running? running;
        lock (_sync) running = _running;
        running?.Cancel();
        await StopCoreAsync(ct);
    }

    /// <summary>
    /// Disconnect step "Enable 0" (ADR-8): a command write that bypasses the verb guards, because the verb a Stop just
    /// cancelled may still be unwinding. Awaits the ack only; the PLC drops the drive on its own.
    /// </summary>
    internal async Task DisableForDetachAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_attached) return;
        }

        await SendCommandAsync("disconnect: Enable 0", CommandBits.None, null, false, ChannelPriority.Move, ct,
            honourOverlay: false);
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (!_attached)
                throw Error(MotionError.CommunicationLost,
                    "Stop not sent: the device is not attached (ConnectAsync has not completed); nothing of ours is moving");
        }

        var (_, ackTick) = await SendCommandAsync("Stop", CommandBits.Stop, null, null, ChannelPriority.Stop, ct,
            honourOverlay: false);
        await AwaitAsync(ackTick - 1,
            s => MapState(s.Status.State) is AxisState.Standstill or AxisState.Disabled or AxisState.ErrorStop,
            failOnErrorStop: false, _options.StopTimeout,
            () => Error(MotionError.MotionFailed, $"still moving {Secs(_options.StopTimeout)} after Stop",
                StateRead("1 (Standstill)"), VelocityRead()),
            ct, honourOverlay: false);
    }

    /// <summary>
    /// <c>ResetAsync</c>: clear the CommunicationLost overlay (once a tick succeeded again), clear
    /// <c>WatchdogFault</c> if set, and from ErrorStop write Enable 0 then the Reset edge; done when the PLC leaves
    /// ErrorStop (ADR-9: it lands in Disabled). Never clears LeaseHeld.
    /// </summary>
    public Task ResetAsync(CancellationToken ct) =>
        RunVerbAsync("ResetAsync", (_, _) =>
            {
                switch (_overlay)
                {
                    case { Error: MotionError.LeaseHeld } lease:
                        throw new MotionException(MotionError.LeaseHeld,
                            $"{lease.Message} ResetAsync does not clear a lost lease; reconnect.", Name);
                    case { Error: MotionError.CommunicationLost } overlay when !_tickOkSinceOverlay:
                        throw new MotionException(MotionError.CommunicationLost,
                            $"{overlay.Message} ResetAsync refused: no tick has succeeded since; retry once the link is "
                            + "back.", Name);
                    case { Error: MotionError.CommunicationLost }:
                        _overlay = null;
                        _logger?.LogInformation("{Axis}: CommunicationLost overlay cleared by ResetAsync", Name);
                        SignalLocked();
                        break;
                }
            },
            async token =>
            {
                PlcSnapshot snapshot;
                lock (_sync) snapshot = _snapshot!.Value;

                if (snapshot.WatchdogFault != 0)
                {
                    await _channel.WriteRegisterAsync(_unit, _map.WatchdogFault, 0, "clear watchdog fault",
                        ChannelPriority.Move, token);
                    _logger?.LogInformation("{Axis}: ResetAsync — WatchdogFault ({Register}) = 0", Name,
                        _map.Describe(_map.WatchdogFault));
                }

                if (MapState(snapshot.Status.State) != AxisState.ErrorStop) return;

                await SendCommandAsync("Enable 0 (before Reset)", CommandBits.None, null, false, ChannelPriority.Move, token);
                var (_, ackTick) = await SendCommandAsync("Reset", CommandBits.Reset, null, null,
                    ChannelPriority.Move, token);
                await AwaitAsync(ackTick - 1, s => MapState(s.Status.State) != AxisState.ErrorStop,
                    failOnErrorStop: false, _options.EnableTimeout,
                    () => Error(MotionError.DriveFault,
                        $"the fault would not reset within {Secs(_options.EnableTimeout)} after the Reset edge",
                        StateRead("not 7 (ErrorStop)"), FaultRead()),
                    token);
            }, ct, requireConnection: false);

    private StatusBlock LastBlock()
    {
        lock (_sync) return _snapshot?.Status ?? default;
    }

    private async Task DiscreteMoveAsync(string verb, int rawTarget, int rawSpeed, double reading,
        CancellationToken ct)
    {
        var (_, ackTick) = await SendCommandAsync("MoveAbsolute", CommandBits.MoveAbsolute,
            [rawTarget, rawSpeed, RawAcceleration()], null, ChannelPriority.Move, ct);

        var distance = Math.Abs(Words.FromRaw(rawTarget) - reading);
        var budget = TimeSpan.FromSeconds(distance / Words.FromRaw(rawSpeed)) + _options.MoveTimeoutMargin;
        PlcSnapshot arrived;
        try
        {
            (arrived, _) = await AwaitAsync(ackTick - 1, s => MapState(s.Status.State) == AxisState.Standstill,
                failOnErrorStop: true, budget, () => new BudgetExceeded(), ct);
        }
        catch (BudgetExceeded)
        {
            await StopCoreAsync(CancellationToken.None);
            throw Error(MotionError.MotionFailed,
                $"{verb} to {Fmt(Words.FromRaw(rawTarget))} {_unitSymbol} did not arrive within {Secs(budget)} "
                + "(distance ÷ speed + margin); the axis was stopped",
                StateRead("1 (Standstill)"), PositionRead(rawTarget));
        }

        if (!arrived.Status.InPosition)
            throw Error(MotionError.MotionFailed,
                $"{verb} to {Fmt(Words.FromRaw(rawTarget))} {_unitSymbol} stopped outside the in-position window",
                AxisErrors.Read(_map, "ActualPosition", _map.ActualPosition, arrived.Status.ActualPosition,
                    rawTarget.ToString(Inv)),
                AxisErrors.Read(_map, "Flags", _map.Flags, $"0x{(ushort)arrived.Status.Flags:X4}", "InPosition set"));
    }

    // ═══════════════════════ guards ═══════════════════════

    private void GuardDiscreteMove(string verb, PlcSnapshot s, AxisState state, out double reading)
    {
        if (state != AxisState.Standstill) throw RefuseState(verb, state);
        if (!s.Status.Homed)
            throw Error(MotionError.NotHomed, $"{verb} needs a homed axis; home the axis first",
                AxisErrors.Read(_map, "Flags", _map.Flags, $"0x{(ushort)s.Status.Flags:X4}", "Homed set"));
        GuardReadingRange(s);
        GuardLimitSource();
        reading = Words.FromRaw(s.Status.ActualPosition);
    }

    /// <summary>G5: while Homed, the reading must lie inside ReadMin..ReadMax (feature-001 FR-10), live every call.</summary>
    private void GuardReadingRange(PlcSnapshot s)
    {
        if (!s.Status.Homed) return;
        var reading = Words.FromRaw(s.Status.ActualPosition);
        var travel = _limits.Units;
        double? min = _options.ReadMin ?? (travel.Source == LimitSource.None ? null : travel.TravelMin);
        double? max = _options.ReadMax ?? (travel.Source == LimitSource.None ? null : travel.TravelMax);
        if ((min is { } lo && reading < lo) || (max is { } hi && reading > hi))
            throw Error(MotionError.NotHomed,
                $"position {Fmt(reading)} {_unitSymbol} is outside the reading range "
                + $"{(min is { } a ? Fmt(a) : "-∞")}..{(max is { } b ? Fmt(b) : "+∞")} {_unitSymbol}; the reading cannot "
                + "be trusted, home the axis first",
                AxisErrors.Read(_map, "ActualPosition", _map.ActualPosition, s.Status.ActualPosition));
    }

    /// <summary>G6: a limit source must exist.</summary>
    private void GuardLimitSource()
    {
        if (_limits.Units.Source == LimitSource.None)
            throw Error(MotionError.OutOfRange,
                "no limit source: the PLC publishes no TravelMin/TravelMax/MaxVelocity and none are configured; no move "
                + "is accepted until the machine's limits are known", LimitReads());
    }

    /// <summary>G7: the target inside TravelMin..TravelMax and representable; refused, never clamped.</summary>
    private int GuardTarget(double target)
    {
        var raw = Words.ToRaw(target, "target", Name);
        if (raw < _limits.RawMin || raw > _limits.RawMax)
            throw Error(MotionError.OutOfRange,
                $"{Fmt(target)} {_unitSymbol} is outside TravelMin..TravelMax {Fmt(_limits.Units.TravelMin)}.."
                + $"{Fmt(_limits.Units.TravelMax)} {_unitSymbol} ({_limits.Units.SourceText}); refused, not clamped",
                LimitReads());
        return raw;
    }

    /// <summary>G8: MinSpeed ≤ |v| ≤ MaxVelocity. Returns the raw register value (signed only for MoveVelocity).</summary>
    private int GuardSpeed(double speed, bool signed)
    {
        var magnitude = Math.Abs(speed);
        var raw = Math.Round(magnitude * Words.Scale, MidpointRounding.AwayFromZero);
        if (double.IsNaN(raw) || raw < 1 || raw > _limits.RawMaxVelocity)
            throw Error(MotionError.UnreachableSpeed,
                $"{Fmt(speed)} {_speedSymbol} is outside {Fmt(Words.Quantum)}..{Fmt(_limits.Units.MaxVelocity)} "
                + $"{_speedSymbol} ({_limits.Units.SourceText}); refused, not clamped", LimitReads());
        var value = (int)raw;
        return signed && speed < 0 ? -value : value;
    }

    /// <summary>G9: only Shortest is supported for a rotary MoveAbsolute (no wrap in map version 1).</summary>
    private void GuardSense(RotationSense? sense)
    {
        if (sense is { } s && s != RotationSense.Shortest)
            throw Error(MotionError.UnsupportedSense,
                $"RotationSense.{s} is not supported; map version 1 has limited rotary travel with no wrap, so only "
                + "Shortest (the direct move) exists");
    }

    private double ResolveSpeed(SpeedRequest request) =>
        request.Absolute ?? _limits.Units.MaxVelocity * (request.Share ?? _options.DefaultSpeed).Value / 100.0;

    private MotionException RefuseState(string verb, AxisState state) => state switch
    {
        AxisState.ErrorStop => Error(MotionError.Busy,
            $"{verb} refused in ErrorStop ({ErrorStopCause()}); call ResetAsync", StateRead()),
        AxisState.Disabled => Error(MotionError.Busy, $"{verb} refused while Disabled; power on or home first",
            StateRead()),
        _ => Error(MotionError.Busy, $"{verb} refused in state {state}", StateRead()),
    };

    private string ErrorStopCause() =>
        _overlay is { } o ? o.Error.ToString()
        : _snapshot is { } s ? $"{MotionErrorClasses.Of(FaultError(s.Status))}/{FaultError(s.Status)}"
        : "no status";

    private int RawAcceleration() =>
        _options.Acceleration is { } a ? Words.ToRaw(a, "Acceleration", Name) : 0;

    // ═══════════════════════ verb runner ═══════════════════════

    private async Task RunVerbAsync(string verb, Action<PlcSnapshot, AxisState>? guards,
        Func<CancellationToken, Task> body, CancellationToken ct, bool requireConnection = true)
    {
        ct.ThrowIfCancellationRequested();
        var running = new Running(verb, CancellationTokenSource.CreateLinkedTokenSource(ct));
        try
        {
            lock (_sync)
            {
                // G1: connected and no overlay (ResetAsync handles the overlay itself).
                if (!_attached || _snapshot is null)
                    throw Error(MotionError.CommunicationLost,
                        $"{verb} not sent: the device is not attached (ConnectAsync has not completed)");
                if (requireConnection && _overlay is { } overlay)
                    throw new MotionException(overlay.Error, $"{overlay.Message} {verb} refused until it clears.", Name);

                // G2: one verb at a time.
                if (_running is { } other)
                    throw Error(MotionError.Busy, $"{verb} refused: {other.Verb} is still running");

                guards?.Invoke(_snapshot.Value, StateOf());
                _running = running;
            }
        }
        catch (MotionException ex)
        {
            running.Dispose();
            _logger?.LogWarning("{Message} Nothing was written.", ex.Message);
            throw;
        }

        try
        {
            await body(running.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // AC-10: the caller cancelled — stop the axis, then surface the cancellation.
            _logger?.LogInformation("{Axis}: {Verb} cancelled by the caller; stopping the axis", Name, verb);
            try
            {
                await StopCoreAsync(CancellationToken.None);
            }
            catch (MotionException ex)
            {
                _logger?.LogWarning("{Axis}: Stop after cancelling {Verb} failed — {Message}", Name, verb, ex.Message);
            }

            throw;
        }
        catch (MotionException ex)
        {
            _logger?.LogWarning("{Message}", ex.Message);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_running, running)) _running = null;
            }

            running.Dispose();
        }
    }

    // ═══════════════════════ handshake ═══════════════════════

    /// <summary>
    /// Design § Command handshake steps 2–5: parameters (one FC16 of C+2…C+7) → <c>[Command, CommandSeq]</c> (one
    /// FC16 of C+0…C+1) → await <c>CommandAck == CommandSeq</c> ≤ 500 ms → clear the edge (same sequence).
    /// </summary>
    /// <returns>The sequence used and the tick number of the snapshot that carried the ack.</returns>
    private async Task<(ushort Seq, long AckTick)> SendCommandAsync(string verb, CommandBits edge, int[]? parameters,
        bool? enable, ChannelPriority lane, CancellationToken ct, bool honourOverlay = true)
    {
        if (parameters is not null)
        {
            var words = new ushort[RegisterMap.ParametersLength];
            for (var i = 0; i < parameters.Length; i++) Words.Write(words.AsSpan(i * 2), parameters[i]);
            await _channel.WriteRegistersAsync(_unit, _map.Parameters, words, $"{verb} parameters", lane, ct);
            _logger?.LogInformation(
                "{Axis}: {Verb} — TargetPosition ({Target}) = {TargetRaw}, Velocity ({Velocity}) = {VelocityRaw}, "
                + "Acceleration ({Acceleration}) = {AccelerationRaw} (raw, 0.001 unit)",
                Name, verb, _map.Describe(_map.TargetPosition), parameters[0], _map.Describe(_map.Velocity), parameters[1],
                _map.Describe(_map.Acceleration), parameters[2]);
        }

        ushort seq, word, level;
        long tickAtWrite;
        lock (_sync)
        {
            if (enable is { } e) _enable = e;
            _seq = NextSeq(_seq);
            seq = _seq;
            level = (ushort)(_enable ? CommandBits.Enable : CommandBits.None);
            word = (ushort)(level | (ushort)edge);
            tickAtWrite = _tickNo;
        }

        await _channel.WriteRegistersAsync(_unit, _map.Command, [word, seq], verb, lane, ct);
        _logger?.LogInformation("{Axis}: {Verb} — Command ({Command}) = 0x{Word:X4}, CommandSeq ({SeqRegister}) = {Seq}",
            Name, verb, _map.Describe(_map.Command), word, _map.Describe(_map.CommandSeq), seq);

        long ackTick;
        try
        {
            (_, ackTick) = await AwaitAsync(tickAtWrite, s => s.Status.CommandAck == seq, failOnErrorStop: false,
                RegisterMap.AckTimeout, () => new BudgetExceeded(), ct, honourOverlay);
        }
        catch (BudgetExceeded)
        {
            var seen = LastBlock();
            await ClearEdgeAsync(verb, level, seq, lane, CancellationToken.None, bestEffort: true);
            throw AxisErrors.Command(Name, MotionError.NotAcknowledged, $"{verb} not accepted", seq, seen.CommandAck,
                seen.State, $"after {RegisterMap.AckTimeout.TotalMilliseconds.ToString(Inv)} ms");
        }

        if (edge != CommandBits.None)
            await ClearEdgeAsync(verb, level, seq, lane, ct, bestEffort: false);
        return (seq, ackTick);
    }

    private async Task ClearEdgeAsync(string verb, ushort level, ushort seq, ChannelPriority lane, CancellationToken ct,
        bool bestEffort)
    {
        try
        {
            await _channel.WriteRegistersAsync(_unit, _map.Command, [level, seq], $"{verb} clear edge", lane, ct);
            _logger?.LogInformation(
                "{Axis}: {Verb} — clear edge: Command ({Command}) = 0x{Word:X4}, CommandSeq ({SeqRegister}) = {Seq}",
                Name, verb, _map.Describe(_map.Command), level, _map.Describe(_map.CommandSeq), seq);
        }
        catch (MotionException ex) when (bestEffort)
        {
            _logger?.LogWarning("{Axis}: could not clear the edge after {Verb} — {Message}", Name, verb, ex.Message);
        }
    }

    /// <summary>
    /// Waits for a snapshot read after tick <paramref name="afterTick"/> that satisfies <paramref name="done"/>.
    /// A snapshot in ErrorStop fails the wait with <see cref="FaultException"/> when <paramref name="failOnErrorStop"/>;
    /// an overlay fails it with the overlay's error; detaching fails it with CommunicationLost.
    /// </summary>
    private async Task<(PlcSnapshot Snapshot, long Tick)> AwaitAsync(long afterTick, Func<PlcSnapshot, bool> done,
        bool failOnErrorStop, TimeSpan budget, Func<Exception> onTimeout, CancellationToken ct,
        bool honourOverlay = true)
    {
        var started = _time.GetTimestamp();
        while (true)
        {
            Task signal;
            PlcSnapshot? snapshot;
            long tick;
            Overlay? overlay;
            bool attached;
            lock (_sync)
            {
                signal = _changed.Task;
                snapshot = _snapshot;
                tick = _tickNo;
                overlay = _overlay;
                attached = _attached;
            }

            if (!attached)
                throw Error(MotionError.CommunicationLost,
                    "the wait was abandoned: the device was disconnected or disposed");
            if (honourOverlay && overlay is not null)
                throw new MotionException(overlay.Error, overlay.Message, Name);

            if (snapshot is { } s && tick > afterTick)
            {
                if (done(s)) return (s, tick);
                if (failOnErrorStop && MapState(s.Status.State) == AxisState.ErrorStop)
                    throw FaultException(Name, _map, s);
            }

            var remaining = budget - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw onTimeout();

            try
            {
                await signal.WaitAsync(remaining, _time, ct);
            }
            catch (TimeoutException)
            {
                // Re-evaluated at the top; the budget check throws.
            }
        }
    }

    private void SignalLocked()
    {
        var old = _changed;
        _changed = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RaiseStatus(AxisStatus status)
    {
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{Axis}: a StatusChanged subscriber threw", Name);
        }
    }

    private static string Fmt(double value) => value.ToString("0.###", Inv);

    private MotionException Error(MotionError error, string what, params ReadOnlySpan<RegisterRead> reads) =>
        AxisErrors.Create(Name, error, what, reads);

    private RegisterRead StateRead(string? expected = null) =>
        AxisErrors.Read(_map, "State", _map.State, LastBlock().State, expected);

    private RegisterRead FlagsRead(string? expected = null) =>
        AxisErrors.Read(_map, "Flags", _map.Flags, $"0x{(ushort)LastBlock().Flags:X4}", expected);

    private RegisterRead FaultRead() => AxisErrors.Read(_map, "FaultCode", _map.FaultCode, LastBlock().FaultCode);

    private RegisterRead VelocityRead() =>
        AxisErrors.Read(_map, "ActualVelocity", _map.ActualVelocity, LastBlock().ActualVelocity, "0");

    private RegisterRead PositionRead(int expectedRaw) =>
        AxisErrors.Read(_map, "ActualPosition", _map.ActualPosition, LastBlock().ActualPosition, expectedRaw.ToString(Inv));

    /// <summary>The three limit registers as last read (a configured source still shows what the PLC publishes).</summary>
    private RegisterRead[] LimitReads()
    {
        var s = _snapshot?.Status ?? default;
        return
        [
            AxisErrors.Read(_map, "TravelMin", _map.TravelMin, s.TravelMin),
            AxisErrors.Read(_map, "TravelMax", _map.TravelMax, s.TravelMax),
            AxisErrors.Read(_map, "MaxVelocity", _map.MaxVelocity, s.MaxVelocity),
        ];
    }

    private static string Secs(TimeSpan t) => $"{t.TotalSeconds.ToString("0.###", Inv)} s";

    /// <inheritdoc/>
    public void Dispose()
    {
        Detach();
    }

    // ═══════════════════════ nested types ═══════════════════════

    private sealed record Overlay(MotionError Error, string Message);

    /// <summary>Effective limits in raw register values (exact comparisons) and in axis units.</summary>
    internal sealed record LimitState(int RawMin, int RawMax, int RawMaxVelocity, AxisLimits Units)
    {
        public static LimitState None { get; } = new(0, 0, 0, AxisLimits.None);
    }

    private sealed class Running(string verb, CancellationTokenSource cts) : IDisposable
    {
        private int _disposed;

        public string Verb { get; } = verb;

        public CancellationToken Token { get; } = cts.Token;

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* finished meanwhile */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) cts.Dispose();
        }
    }

    /// <summary>Internal marker for "the budget ran out", converted to the verb's own error by its caller.</summary>
    private sealed class BudgetExceeded : Exception;
}
