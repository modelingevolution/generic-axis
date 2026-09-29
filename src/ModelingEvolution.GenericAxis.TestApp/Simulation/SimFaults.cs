namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// Defects injected into the simulated PLC (design § Simulator, <c>SimFaults</c>). Immutable: the UI and the tests
/// replace the whole record, so a scan never sees half an update. Every member binds from configuration
/// (<c>Simulator:Faults:&lt;Name&gt;</c>), so a process can start already faulted.
/// </summary>
public sealed record SimFaults
{
    public static readonly SimFaults None = new();

    /// <summary>The listener is gated: connections dropped, none accepted. Registers untouched.</summary>
    public bool CommunicationDown { get; init; }

    /// <summary><c>FaultCode 1</c> while set.</summary>
    public bool DriveFault { get; init; }

    /// <summary><c>FaultCode 3</c> at 50 % of every MoveAbsolute started while set.</summary>
    public bool FollowingErrorAtHalfway { get; init; }

    /// <summary>The home sensor never fires; homing ends at the limit switch with <c>FaultCode 5</c>.</summary>
    public bool HomeSensorDead { get; init; }

    /// <summary>The minimum limit switch is active.</summary>
    public bool ForceLimitMin { get; init; }

    /// <summary>The maximum limit switch is active.</summary>
    public bool ForceLimitMax { get; init; }

    /// <summary><c>FaultCode 7</c> while set (E-stop chain open).</summary>
    public bool SafetyStop { get; init; }

    /// <summary><c>FaultCode 6</c> while set (PLC lost its drive).</summary>
    public bool DriveLinkLost { get; init; }

    /// <summary>Commands are never accepted: <c>CommandAck</c> never follows <c>CommandSeq</c>.</summary>
    public bool SuppressAck { get; init; }

    /// <summary>No watchdog network at all — the vendor-ladder case the conformance check must catch.</summary>
    public bool WatchdogDisabled { get; init; }

    /// <summary>The PLC reads and writes its 32-bit values high word first — a protocol violation to be caught.</summary>
    public bool SwappedWordOrder { get; init; }

    /// <summary>
    /// The PLC's Modbus task hangs: TCP connections are accepted but no request is ever answered. Setting it drops the
    /// established connections, so every client meets the silence on its next request; registers are untouched.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>
    /// A slow drive: a move is accepted and acknowledged at once (State 3 or 4), but the axis starts moving only this
    /// long afterwards. Zero (the default) means no delay. Makes a check exceed its motion budget in tests.
    /// </summary>
    public TimeSpan MotionStartDelay { get; init; }

    /// <summary>Names of the faults in force, for logs and the UI.</summary>
    public IEnumerable<string> Active()
    {
        if (CommunicationDown) yield return nameof(CommunicationDown);
        if (DriveFault) yield return nameof(DriveFault);
        if (FollowingErrorAtHalfway) yield return nameof(FollowingErrorAtHalfway);
        if (HomeSensorDead) yield return nameof(HomeSensorDead);
        if (ForceLimitMin) yield return nameof(ForceLimitMin);
        if (ForceLimitMax) yield return nameof(ForceLimitMax);
        if (SafetyStop) yield return nameof(SafetyStop);
        if (DriveLinkLost) yield return nameof(DriveLinkLost);
        if (SuppressAck) yield return nameof(SuppressAck);
        if (WatchdogDisabled) yield return nameof(WatchdogDisabled);
        if (SwappedWordOrder) yield return nameof(SwappedWordOrder);
        if (Silent) yield return nameof(Silent);
        if (MotionStartDelay > TimeSpan.Zero) yield return $"{nameof(MotionStartDelay)} {MotionStartDelay.TotalSeconds:0.#} s";
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var active = string.Join(", ", Active());
        return active.Length == 0 ? "none" : active;
    }
}
