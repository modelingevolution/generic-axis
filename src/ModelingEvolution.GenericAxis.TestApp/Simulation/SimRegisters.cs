namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// The register map of <c>docs/protocol.md</c> as the PLC sees it: offsets inside the command block (C+n) and the
/// status block (S+n). Written from the protocol alone (ADR-13) — the simulator does not use the driver's
/// <c>RegisterMap</c>, so a mistake in one is caught by the other.
/// </summary>
public static class SimRegisters
{
    // Command block — written by the driver, read by the PLC (C+0 … C+11).
    public const int Command = 0;
    public const int CommandSeq = 1;
    public const int TargetPosition = 2;   // int32, 2–3
    public const int Velocity = 4;         // int32, 4–5
    public const int Acceleration = 6;     // int32, 6–7
    public const int Heartbeat = 8;
    public const int LeaseOwner = 9;
    public const int WatchdogFault = 10;
    public const int WatchdogTrips = 11;   // PLC-owned
    public const int CommandLength = 12;

    // Status block — written by the PLC (S+0 … S+14).
    public const int State = 0;
    public const int Flags = 1;
    public const int ActualPosition = 2;   // int32, 2–3
    public const int ActualVelocity = 4;   // int32, 4–5
    public const int FaultCode = 6;
    public const int CommandAck = 7;
    public const int TravelMin = 8;        // int32, 8–9
    public const int TravelMax = 10;       // int32, 10–11
    public const int MaxVelocity = 12;     // int32, 12–13
    public const int MapVersion = 14;
    public const int StatusLength = 15;

    /// <summary>Raw units per axis unit: positions 0.001 mm/°, velocities 0.001 unit/s.</summary>
    public const double Scale = 1000.0;
}

/// <summary>Bits of the <c>Command</c> register (protocol § Command block).</summary>
[Flags]
public enum SimCommandBits : ushort
{
    None = 0,
    Enable = 1 << 0,
    Home = 1 << 1,
    MoveAbsolute = 1 << 2,
    MoveVelocity = 1 << 3,
    Stop = 1 << 4,
    Reset = 1 << 5,
}

/// <summary>Bits of the <c>Flags</c> register (protocol § Status block).</summary>
[Flags]
public enum SimStatusFlags : ushort
{
    None = 0,
    Homed = 1 << 0,
    InPosition = 1 << 1,
    LimitMin = 1 << 2,
    LimitMax = 1 << 3,
    HomeSensor = 1 << 4,
    DriveReady = 1 << 5,
    Moving = 1 << 6,
}

/// <summary><c>State</c> register values — the numbers of the SDK <c>AxisState</c> enum.</summary>
public enum SimAxisState : ushort
{
    Disabled = 0,
    Standstill = 1,
    Homing = 2,
    DiscreteMotion = 3,
    ContinuousMotion = 4,
    Stopping = 6,
    ErrorStop = 7,
}

/// <summary><c>FaultCode</c> register values.</summary>
public enum SimFaultCode : ushort
{
    None = 0,
    DriveFault = 1,
    LimitSwitch = 2,
    FollowingError = 3,
    Watchdog = 4,
    HomingFailed = 5,
    DriveLinkLost = 6,
    SafetyStop = 7,
}
