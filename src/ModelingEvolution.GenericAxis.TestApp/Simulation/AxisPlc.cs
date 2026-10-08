using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// The PLC program of <c>docs/protocol.md</c> plus the drive physics. <see cref="Tick"/> is one PLC scan
/// (design § Simulator, scan order): read the command block → watchdog → accept a command → injected faults →
/// motion, homing and limits → publish the status block.
/// </summary>
/// <remarks>
/// Two frames: the <b>physical</b> position <c>x</c> (home sensor, limit switches and hard stops sit here) and the
/// <b>published</b> position <c>x + offset</c>. The offset is <see cref="SimulatedAxisOptions.UnhomedOffset"/> until
/// homed and is re-referenced on the home sensor's edge. With <see cref="SimulatedAxisOptions.HomedAtPowerUp"/> the
/// offset starts at 0, so both frames coincide. Not thread-safe: <see cref="SimulatorHost"/> serialises every call.
/// </remarks>
public sealed class AxisPlc
{

    private readonly SimulatedAxisOptions _o;
    private readonly PlcRegisterFile _r;
    private readonly ILogger _log;
    private readonly int _c;
    private readonly int _s;

    private double _x;
    private double _v;
    private double _offset;
    private bool _homed;
    private SimAxisState _state = SimAxisState.Disabled;
    private SimFaultCode _fault = SimFaultCode.None;
    private bool _energised;
    private bool _enableInhibit;
    private bool _inPosition;
    private ushort _ack;

    private (ushort Seq, TimeSpan Remaining)? _pendingEnable;

    private double _target;
    private double _vcmd;
    private double _accel;
    private double _decel;
    private double _moveStart;
    private bool _followingErrorArmed;
    private bool _homingBackOff;

    private TimeSpan _clock;
    private TimeSpan _motionAcceptedAt;
    private ushort _lastBeat;
    private TimeSpan _lastBeatAt;
    private bool _armed;
    private ushort _trips;

    private bool _prevSwitchMin;
    private bool _prevSwitchMax;
    private bool _prevSensor;

    private SimFaults _faults;

    public AxisPlc(SimulatedAxisOptions options, PlcRegisterFile registers, ILogger? logger = null)
    {
        _o = options.Validate();
        _r = registers;
        _log = logger ?? NullLogger.Instance;
        _c = options.CommandBase;
        _s = options.StatusBase;
        _faults = options.Faults;
        PublishLimits = options.PublishLimits;
        MapVersion = options.MapVersion;
        _acceleration = options.DefaultAcceleration;
        _deceleration = options.DefaultDeceleration;
        _quickStop = options.QuickStopDeceleration;
        _x = options.InitialPosition;
        _homed = options.HomedAtPowerUp;
        _offset = _homed ? 0 : options.UnhomedOffset;
        _prevSensor = SensorActive;
        (_prevSwitchMin, _prevSwitchMax) = (SwitchMin, SwitchMax);
        Publish();
    }

    public SimulatedAxisOptions Options => _o;

    /// <summary>Faults in force. The host logs the change.</summary>
    public SimFaults Faults
    {
        get => _faults;
        set => _faults = value ?? SimFaults.None;
    }

    /// <summary>When false, S+8…S+13 read 0 (a PLC that cannot publish its limits).</summary>
    public bool PublishLimits { get; set; }

    /// <summary>Value served in S+14.</summary>
    public ushort MapVersion { get; set; }

    private double _acceleration;
    private double _deceleration;
    private double _quickStop;

    /// <summary>The PLC's default ramp-up (unit/s²), used when the <c>Acceleration</c> register is 0. Runtime-editable (UI).</summary>
    public double Acceleration
    {
        get => _acceleration;
        set => _acceleration = Positive(value);
    }

    /// <summary>The PLC's default braking ramp (unit/s²): arrival, a homed travel limit, homing. Used when the register is 0.</summary>
    public double Deceleration
    {
        get => _deceleration;
        set => _deceleration = Positive(value);
    }

    /// <summary>The Stop ramp (unit/s²), independent of the move's ramps.</summary>
    public double QuickStopDeceleration
    {
        get => _quickStop;
        set => _quickStop = Positive(value);
    }

    private static double Positive(double value, [System.Runtime.CompilerServices.CallerMemberName] string name = "") =>
        value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "A ramp must be > 0");

    /// <summary>A non-zero <c>Acceleration</c> register sets both ramps of the move; 0 = the PLC defaults.</summary>
    private (double Accel, double Decel) Ramps(double register) => register > 0 ? (register, register) : (_acceleration, _deceleration);

    private double Published => _x + _offset;
    private bool SensorActive => !_faults.HomeSensorDead && Math.Abs(_x - _o.HomeSensorPosition) <= _o.HomeSensorWidth / 2;
    private bool SwitchMin => _faults.ForceLimitMin || _x <= _o.TravelMin - _o.LimitSwitchMargin;
    private bool SwitchMax => _faults.ForceLimitMax || _x >= _o.TravelMax + _o.LimitSwitchMargin;
    private double HardStopMin => _o.TravelMin - 2 * _o.LimitSwitchMargin;
    private double HardStopMax => _o.TravelMax + 2 * _o.LimitSwitchMargin;

    /// <summary>One PLC scan of <paramref name="dt"/>.</summary>
    public void Tick(TimeSpan dt)
    {
        if (dt < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(dt), dt, "A scan cannot run backwards");
        _clock += dt;

        // 1. Read the command block.
        var command = (SimCommandBits)_r.Holding.Read(_c + SimRegisters.Command);
        var seq = _r.Holding.Read(_c + SimRegisters.CommandSeq);

        Watchdog();                 // 2.
        Accept(command, seq, dt);   // 3.
        InjectedFaults();
        Motion(dt.TotalSeconds);    // 4.–6.
        Publish();                  // 7.
    }

    /// <summary>Moves the carriage by hand (UI and tests). Keeps the homing reference.</summary>
    public void Teleport(double publishedPosition)
    {
        _x = Math.Clamp(publishedPosition - _offset, HardStopMin, HardStopMax);
        _v = 0;
        _inPosition = false;
        _prevSensor = SensorActive;
        (_prevSwitchMin, _prevSwitchMax) = (SwitchMin, SwitchMax);
        _log.LogInformation("Teleported to {Position} {Unit} (physical {Physical})", Published, _o.Unit, _x);
        Publish();
    }

    /// <summary>
    /// PLC power cycle: command block and ack zeroed, drive off, watchdog disarmed, trips 0, faults cleared.
    /// <c>Homed</c> survives only when <see cref="SimulatedAxisOptions.HomedAtPowerUp"/> (an absolute encoder).
    /// </summary>
    public void PowerCycle()
    {
        for (var i = 0; i < SimRegisters.CommandLength; i++) _r.Holding.Write(_c + i, 0);
        _ack = 0;
        _v = 0;
        _energised = false;
        _enableInhibit = false;
        _pendingEnable = null;
        _inPosition = false;
        _fault = SimFaultCode.None;
        _state = SimAxisState.Disabled;
        _armed = false;
        _trips = 0;
        _lastBeat = 0;
        _lastBeatAt = _clock;
        if (!_o.HomedAtPowerUp)
        {
            _homed = false;
            _offset = _o.UnhomedOffset;
        }

        _log.LogWarning("PLC power cycle: Disabled, watchdog disarmed, trips 0, Homed {Homed}", _homed);
        Publish();
    }

    /// <summary>
    /// Rewrites every PLC-owned register from the PLC's state: the status block (input S+0…S+14) and holding C+11.
    /// Also called after a client write lands on C+11: the protocol says the PLC ignores such writes, and a read in the
    /// same batch must not see them. The status block needs no such guard: it is input registers, which no client can
    /// write (ADR-36).
    /// </summary>
    public void Publish()
    {
        var swapped = _faults.SwappedWordOrder;
        var flags = SimStatusFlags.None;
        if (_homed) flags |= SimStatusFlags.Homed;
        if (_inPosition) flags |= SimStatusFlags.InPosition;
        if (SwitchMin) flags |= SimStatusFlags.LimitMin;
        if (SwitchMax) flags |= SimStatusFlags.LimitMax;
        if (SensorActive) flags |= SimStatusFlags.HomeSensor;
        if (_energised && _state != SimAxisState.ErrorStop) flags |= SimStatusFlags.DriveReady;
        if (_v != 0) flags |= SimStatusFlags.Moving;

        _r.Input.Write(_s + SimRegisters.State, (ushort)_state);
        _r.Input.Write(_s + SimRegisters.Flags, (ushort)flags);
        _r.Input.WriteInt32(_s + SimRegisters.ActualPosition, ToRaw(Published), swapped);
        _r.Input.WriteInt32(_s + SimRegisters.ActualVelocity, ToRaw(_v), swapped);
        _r.Input.Write(_s + SimRegisters.FaultCode, (ushort)_fault);
        _r.Input.Write(_s + SimRegisters.CommandAck, _ack);
        _r.Input.WriteInt32(_s + SimRegisters.TravelMin, PublishLimits ? ToRaw(_o.TravelMin) : 0, swapped);
        _r.Input.WriteInt32(_s + SimRegisters.TravelMax, PublishLimits ? ToRaw(_o.TravelMax) : 0, swapped);
        _r.Input.WriteInt32(_s + SimRegisters.MaxVelocity, PublishLimits ? ToRaw(_o.MaxVelocity) : 0, swapped);
        _r.Input.Write(_s + SimRegisters.MapVersion, MapVersion);
        _r.Holding.Write(_c + SimRegisters.WatchdogTrips, _trips);
    }

    /// <summary>The PLC's state for the UI and tests.</summary>
    public SimSnapshot Snapshot(bool listening) => new()
    {
        Clock = _clock,
        TruePosition = _x,
        PublishedPosition = Published,
        Velocity = _v,
        State = _state,
        Flags = (SimStatusFlags)_r.Input.Read(_s + SimRegisters.Flags),
        FaultCode = _fault,
        Homed = _homed,
        Energised = _energised,
        CommandWord = _r.Holding.Read(_c + SimRegisters.Command),
        CommandSeq = _r.Holding.Read(_c + SimRegisters.CommandSeq),
        CommandAck = _ack,
        Heartbeat = _r.Holding.Read(_c + SimRegisters.Heartbeat),
        HeartbeatAge = _clock - _lastBeatAt,
        WatchdogArmed = _armed,
        WatchdogFault = _r.Holding.Read(_c + SimRegisters.WatchdogFault),
        WatchdogTrips = _trips,
        LeaseOwner = _r.Holding.Read(_c + SimRegisters.LeaseOwner),
        Faults = _faults,
        PublishLimits = PublishLimits,
        MapVersion = MapVersion,
        Listening = listening,
        CommandBlock = [.. _r.Holding.ReadBlock(_c, SimRegisters.CommandLength)],
        StatusBlock = [.. _r.Input.ReadBlock(_s, SimRegisters.StatusLength)],
    };

    // ---- 2. Watchdog (protocol § FR-11) ------------------------------------------------------------------------

    private void Watchdog()
    {
        var beat = _r.Holding.Read(_c + SimRegisters.Heartbeat);
        var lease = _r.Holding.Read(_c + SimRegisters.LeaseOwner);
        var latched = _r.Holding.Read(_c + SimRegisters.WatchdogFault);

        if (beat != _lastBeat)
        {
            _lastBeat = beat;
            _lastBeatAt = _clock;
        }

        if (_faults.WatchdogDisabled)
        {
            _armed = false;
            return;
        }

        if (_armed && lease == 0)
        {
            _armed = false;
            _log.LogInformation("Watchdog disarmed: LeaseOwner written 0 (clean release)");
            return;
        }

        if (!_armed)
        {
            // Arm on the first change observed while unlatched and leased — an idle PLC is never faulted.
            if (_lastBeatAt == _clock && latched == 0 && lease != 0)
            {
                _armed = true;
                _log.LogInformation("Watchdog armed by owner {Owner} (Heartbeat {Beat})", lease, beat);
            }

            return;
        }

        if (_clock - _lastBeatAt >= _o.WatchdogTimeout)
        {
            _trips++;
            _armed = false;
            _r.Holding.Write(_c + SimRegisters.WatchdogFault, 1);
            EnterErrorStop(SimFaultCode.Watchdog,
                $"watchdog tripped: Heartbeat {beat} unchanged for {(_clock - _lastBeatAt).TotalMilliseconds:F0} ms, owner {lease}, trips {_trips}");
        }
    }

    // ---- 3. Accept a command (protocol § Command semantics) ----------------------------------------------------

    private void Accept(SimCommandBits command, ushort seq, TimeSpan dt)
    {
        if (_faults.SuppressAck) return;

        if (_pendingEnable is { } pending)
        {
            if (seq == pending.Seq)
            {
                var remaining = pending.Remaining - dt;
                if (remaining > TimeSpan.Zero)
                {
                    _pendingEnable = (pending.Seq, remaining);
                    return;
                }

                _pendingEnable = null;
                if (_state == SimAxisState.Disabled) Energise();
                _ack = seq; // the ack lands in the scan that enters Standstill
                return;
            }

            _pendingEnable = null; // superseded by a newer command
        }

        if (seq == _ack) return;

        _log.LogInformation("Command 0x{Command:X4} ({Bits}) CommandSeq {Seq} in {State}", (ushort)command, command, seq, _state);
        if (Execute(command, seq)) _ack = seq;
    }

    /// <returns>True when the ack belongs to this scan; false when it waits for the drive (Enable delay).</returns>
    private bool Execute(SimCommandBits command, ushort seq)
    {
        var enable = command.HasFlag(SimCommandBits.Enable);
        if (!enable) _enableInhibit = false; // a 0 seen after an ErrorStop makes the next 1 a fresh edge

        if (command.HasFlag(SimCommandBits.Stop))
        {
            if (_state is SimAxisState.Homing or SimAxisState.DiscreteMotion or SimAxisState.ContinuousMotion)
                SetState(SimAxisState.Stopping, "Stop");
            if (!enable && _state is not (SimAxisState.Disabled or SimAxisState.ErrorStop)) Disable("Stop with Enable 0");
            return true;
        }

        if (command.HasFlag(SimCommandBits.Reset))
        {
            if (_state == SimAxisState.ErrorStop)
            {
                _log.LogInformation("Reset clears FaultCode {Fault}", _fault);
                _fault = SimFaultCode.None;
                _energised = false;
                SetState(SimAxisState.Disabled, "Reset");
            }

            return true;
        }

        if (!enable)
        {
            if (_state is not (SimAxisState.Disabled or SimAxisState.ErrorStop)) Disable("Enable 0");
            return true;
        }

        if (_state == SimAxisState.ErrorStop) return Ignored(command, "ErrorStop needs Reset");

        if (_state == SimAxisState.Disabled)
        {
            if (_enableInhibit) return Ignored(command, "Enable needs a fresh 0→1 after ErrorStop");
            if (_o.EnableDelay <= TimeSpan.Zero)
            {
                Energise();
                return true;
            }

            _pendingEnable = (seq, _o.EnableDelay);
            return false;
        }

        if (command.HasFlag(SimCommandBits.Home)) return StartHoming(command);
        if (command.HasFlag(SimCommandBits.MoveAbsolute)) return StartMoveAbsolute(command);
        if (command.HasFlag(SimCommandBits.MoveVelocity)) return StartMoveVelocity(command);
        return true;
    }

    private bool StartHoming(SimCommandBits command)
    {
        if (_state != SimAxisState.Standstill) return Ignored(command, $"Home needs Standstill, state is {_state}");
        _inPosition = false;
        (_accel, _decel) = Ramps(0);
        _homingBackOff = SensorActive; // already on the sensor: leave it first, so the edge is a real one
        SetState(SimAxisState.Homing, _homingBackOff ? "Home (backing off the sensor)" : "Home");
        return true;
    }

    private bool StartMoveAbsolute(SimCommandBits command)
    {
        if (!_homed) return Ignored(command, "MoveAbsolute needs Homed");
        if (!IsMotionState(_state) && _state != SimAxisState.Standstill)
            return Ignored(command, $"MoveAbsolute not accepted in {_state}");

        var swapped = _faults.SwappedWordOrder;
        var target = _r.Holding.ReadInt32(_c + SimRegisters.TargetPosition, swapped) / SimRegisters.Scale;
        var speed = Math.Abs(_r.Holding.ReadInt32(_c + SimRegisters.Velocity, swapped) / SimRegisters.Scale);
        var accel = _r.Holding.ReadInt32(_c + SimRegisters.Acceleration, swapped) / SimRegisters.Scale;

        if (target < _o.TravelMin || target > _o.TravelMax)
            return Ignored(command,
                $"TargetPosition (C+{SimRegisters.TargetPosition}) = {target} {_o.Unit}, outside TravelMin..TravelMax {_o.TravelMin}..{_o.TravelMax}");
        if (speed <= 0) return Ignored(command, "Velocity 0");
        if (speed > _o.MaxVelocity) return OverMaxVelocity(command, speed);
        if (TowardActiveSwitch(target - Published)) return Ignored(command, "motion toward an active limit switch");

        _target = target;
        _vcmd = speed;
        (_accel, _decel) = Ramps(accel);
        _moveStart = Published;
        _followingErrorArmed = _faults.FollowingErrorAtHalfway;
        _inPosition = false;
        _motionAcceptedAt = _clock;
        SetState(SimAxisState.DiscreteMotion, $"MoveAbsolute to {target} {_o.Unit} at {_vcmd} {_o.Unit}/s, a {_accel}");
        return true;
    }

    private bool StartMoveVelocity(SimCommandBits command)
    {
        if (!IsMotionState(_state) && _state != SimAxisState.Standstill)
            return Ignored(command, $"MoveVelocity not accepted in {_state}");

        var swapped = _faults.SwappedWordOrder;
        var velocity = _r.Holding.ReadInt32(_c + SimRegisters.Velocity, swapped) / SimRegisters.Scale;
        var accel = _r.Holding.ReadInt32(_c + SimRegisters.Acceleration, swapped) / SimRegisters.Scale;

        if (velocity == 0) return Ignored(command, "Velocity 0");
        if (Math.Abs(velocity) > _o.MaxVelocity) return OverMaxVelocity(command, velocity);
        if (TowardActiveSwitch(velocity)) return Ignored(command, "motion toward an active limit switch");

        _vcmd = velocity;
        (_accel, _decel) = Ramps(accel);
        _inPosition = false;
        _motionAcceptedAt = _clock;
        SetState(SimAxisState.ContinuousMotion, $"MoveVelocity {_vcmd} {_o.Unit}/s, a {_accel}");
        return true;
    }

    // Refuse, never clamp (ADR-3, protocol § Command semantics): a clamped speed would hide a regressed driver guard.
    private bool OverMaxVelocity(SimCommandBits command, double velocity) =>
        Ignored(command,
            $"Velocity (C+{SimRegisters.Velocity}) = {velocity} {_o.Unit}/s, above MaxVelocity {_o.MaxVelocity} {_o.Unit}/s");

    private bool Ignored(SimCommandBits command, string reason)
    {
        _log.LogWarning("Command {Bits} acknowledged and ignored: {Reason}", command, reason);
        return true;
    }

    private bool TowardActiveSwitch(double direction) =>
        (direction < 0 && SwitchMin) || (direction > 0 && SwitchMax);

    private static bool IsMotionState(SimAxisState s) =>
        s is SimAxisState.Homing or SimAxisState.DiscreteMotion or SimAxisState.ContinuousMotion or SimAxisState.Stopping;

    // ---- Injected faults ---------------------------------------------------------------------------------------

    private void InjectedFaults()
    {
        if (_state == SimAxisState.ErrorStop) return;
        if (_faults.SafetyStop) EnterErrorStop(SimFaultCode.SafetyStop, "safety stop (injected)");
        else if (_faults.DriveFault) EnterErrorStop(SimFaultCode.DriveFault, "drive fault (injected)");
        else if (_faults.DriveLinkLost) EnterErrorStop(SimFaultCode.DriveLinkLost, "communication to drive lost (injected)");
    }

    // ---- 4.–6. Motion, homing, limits --------------------------------------------------------------------------

    private void Motion(double dt)
    {
        double targetVelocity;

        // Injected: the drive starts a move only MotionStartDelay after the PLC accepted it (a slow drive).
        if (_state is SimAxisState.DiscreteMotion or SimAxisState.ContinuousMotion && _clock - _motionAcceptedAt < _faults.MotionStartDelay)
            return;

        switch (_state)
        {
            case SimAxisState.DiscreteMotion:
            {
                var d = _target - Published;
                if (Arrived(Math.Abs(d), _decel, dt))
                {
                    _x = _target - _offset;
                    _v = 0;
                    _inPosition = true;
                    SetState(SimAxisState.Standstill, $"in position at {_target} {_o.Unit}");
                    break;
                }

                targetVelocity = Math.CopySign(Math.Min(_vcmd, BrakingSpeed(Math.Abs(d), _decel, dt)), d);
                Integrate(targetVelocity, _accel, _decel, dt);
                if (_followingErrorArmed && Math.Abs(Published - _moveStart) >= Math.Abs(_target - _moveStart) / 2)
                {
                    _followingErrorArmed = false;
                    EnterErrorStop(SimFaultCode.FollowingError, "following error at half the move (injected)");
                }

                break;
            }

            case SimAxisState.ContinuousMotion:
                if (_homed)
                {
                    // Homed: TravelMin/Max is a controlled stop, braked so the axis stands exactly on the limit.
                    var limit = _vcmd > 0 ? _o.TravelMax : _o.TravelMin;
                    var ahead = Math.Max(0, (limit - Published) * Math.Sign(_vcmd));
                    if (Arrived(ahead, _decel, dt))
                    {
                        if (ahead > 0) _x = limit - _offset;
                        _v = 0;
                        SetState(SimAxisState.Standstill, $"travel limit {limit} {_o.Unit} reached (controlled stop)");
                        break;
                    }

                    targetVelocity = Math.CopySign(Math.Min(Math.Abs(_vcmd), BrakingSpeed(ahead, _decel, dt)), _vcmd);
                }
                else
                {
                    targetVelocity = _vcmd;
                }

                Integrate(targetVelocity, _accel, _decel, dt);
                break;

            case SimAxisState.Stopping:
                Integrate(0, _quickStop, _quickStop, dt);
                if (_v == 0) SetState(SimAxisState.Standstill, "stopped");
                break;

            case SimAxisState.Homing:
                if (_homingBackOff && !SensorActive) _homingBackOff = false;
                Integrate(_homingBackOff ? _o.HomingVelocity : -_o.HomingVelocity, _accel, _decel, dt);
                break;

            default:
                _v = 0;
                break;
        }

        HomeSensorEdge();
        LimitSwitches();
    }

    /// <summary>One scan of the velocity ramp: <paramref name="accel"/> while the speed grows, <paramref name="decel"/> while it falls or reverses.</summary>
    private void Integrate(double targetVelocity, double accel, double decel, double dt)
    {
        var speedingUp = _v == 0 || (Math.Sign(targetVelocity) == Math.Sign(_v) && Math.Abs(targetVelocity) > Math.Abs(_v));
        var step = (speedingUp ? accel : decel) * dt;
        _v += Math.Clamp(targetVelocity - _v, -step, step);
        _x += _v * dt;
        if (_x < HardStopMin || _x > HardStopMax)
        {
            _x = Math.Clamp(_x, HardStopMin, HardStopMax);
            _v = 0;
            _log.LogWarning("Carriage hit the hard stop at {Physical}", _x);
        }
    }

    /// <summary>Slow enough to stop, and within the window or reachable in this scan.</summary>
    private bool Arrived(double distance, double accel, double dt) =>
        Math.Abs(_v) <= accel * dt && distance <= Math.Max(_o.InPositionWindow, Math.Abs(_v) * dt);

    /// <summary>
    /// The highest speed from which a ramp of <c>a·dt</c> per scan, integrated as <c>x += v·dt</c>, stops within
    /// <paramref name="distance"/> — the discrete-time form of √(2·a·d), which arrives without overshoot.
    /// </summary>
    private static double BrakingSpeed(double distance, double accel, double dt)
    {
        if (dt <= 0) return Math.Sqrt(2 * accel * distance);
        var u = accel * dt;
        return u * (Math.Sqrt(0.25 + 2 * distance / (u * dt)) - 0.5);
    }

    private void HomeSensorEdge()
    {
        var sensor = SensorActive;
        var rising = sensor && !_prevSensor;
        _prevSensor = sensor;
        if (!rising || _state != SimAxisState.Homing || _homingBackOff) return;

        _offset = _o.HomeSensorPosition - _x;
        _homed = true;
        _v = 0;
        SetState(SimAxisState.Standstill, $"homed on the sensor edge: ActualPosition = {_o.HomeSensorPosition} {_o.Unit}");
    }

    private void LimitSwitches()
    {
        var min = SwitchMin;
        var max = SwitchMax;
        var entered = (min && !_prevSwitchMin) || (max && !_prevSwitchMax);
        (_prevSwitchMin, _prevSwitchMax) = (min, max);
        if (!entered || !IsMotionState(_state)) return;

        var which = min ? "minimum" : "maximum";
        if (_state == SimAxisState.Homing)
            EnterErrorStop(SimFaultCode.HomingFailed, $"homing reached the {which} limit switch before the home sensor");
        else
            EnterErrorStop(SimFaultCode.LimitSwitch, $"{which} limit switch tripped");
    }

    // ---- Transitions -------------------------------------------------------------------------------------------

    private void Energise()
    {
        _energised = true;
        SetState(SimAxisState.Standstill, "Enable 1: drive energised");
    }

    private void Disable(string reason)
    {
        _energised = false;
        _v = 0;
        _inPosition = false;
        SetState(SimAxisState.Disabled, reason);
    }

    private void EnterErrorStop(SimFaultCode code, string reason)
    {
        _v = 0;
        _energised = false;
        _enableInhibit = true;
        _pendingEnable = null;
        _inPosition = false;
        _fault = code;
        _log.LogWarning("ErrorStop, FaultCode {Code} ({Fault}): {Reason}", (ushort)code, code, reason);
        SetState(SimAxisState.ErrorStop, reason);
    }

    private void SetState(SimAxisState next, string reason)
    {
        if (next == _state) return;
        _log.LogInformation("State {Old} -> {New}: {Reason}", _state, next, reason);
        _state = next;
    }

    private static int ToRaw(double units)
    {
        var raw = Math.Round(units * SimRegisters.Scale, MidpointRounding.AwayFromZero);
        // Never clamped: options that cannot be published are refused by SimulatedAxisOptions.Validate.
        if (raw is < int.MinValue or > int.MaxValue)
            throw new OverflowException($"{units} does not fit an int32 register at scale {SimRegisters.Scale}");
        return (int)raw;
    }
}
