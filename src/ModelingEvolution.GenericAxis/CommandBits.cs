namespace ModelingEvolution.GenericAxis;

/// <summary>Register C+0 <c>Command</c> (protocol § Command block). Bits 6–15 are 0.</summary>
[Flags]
public enum CommandBits : ushort
{
    /// <summary>No bit set: drive off, no edge.</summary>
    None = 0,

    /// <summary>bit 0 — level: 1 = servo on.</summary>
    Enable = 1 << 0,

    /// <summary>bit 1 — edge: run the PLC's homing sequence.</summary>
    Home = 1 << 1,

    /// <summary>bit 2 — edge: move to <c>TargetPosition</c> at <c>Velocity</c>.</summary>
    MoveAbsolute = 1 << 2,

    /// <summary>bit 3 — edge: continuous motion at signed <c>Velocity</c>.</summary>
    MoveVelocity = 1 << 3,

    /// <summary>bit 4 — edge, priority over every other bit: decelerate and hold.</summary>
    Stop = 1 << 4,

    /// <summary>bit 5 — edge: clear <c>FaultCode</c>, ErrorStop → Disabled.</summary>
    Reset = 1 << 5,
}

/// <summary>Register S+1 <c>Flags</c> (protocol § Status block).</summary>
[Flags]
public enum StatusFlags : ushort
{
    /// <summary>No flag set.</summary>
    None = 0,

    /// <summary>bit 0 — the axis is referenced; <c>ActualPosition</c> is valid.</summary>
    Homed = 1 << 0,

    /// <summary>bit 1 — the last discrete move arrived.</summary>
    InPosition = 1 << 1,

    /// <summary>bit 2 — the minimum limit switch is active.</summary>
    LimitMin = 1 << 2,

    /// <summary>bit 3 — the maximum limit switch is active.</summary>
    LimitMax = 1 << 3,

    /// <summary>bit 4 — the home sensor is active.</summary>
    HomeSensor = 1 << 4,

    /// <summary>bit 5 — the drive reports ready.</summary>
    DriveReady = 1 << 5,

    /// <summary>bit 6 — the axis is moving.</summary>
    Moving = 1 << 6,
}

/// <summary>Register S+6 <c>FaultCode</c> (protocol § Status block). Values ≥ 100 are vendor-specific.</summary>
public enum PlcFaultCode : ushort
{
    /// <summary>0 — no fault.</summary>
    None = 0,

    /// <summary>1 — drive fault.</summary>
    DriveFault = 1,

    /// <summary>2 — limit switch tripped.</summary>
    LimitSwitch = 2,

    /// <summary>3 — following error.</summary>
    FollowingError = 3,

    /// <summary>4 — the FR-11 watchdog tripped.</summary>
    Watchdog = 4,

    /// <summary>5 — homing failed.</summary>
    HomingFailed = 5,

    /// <summary>6 — the PLC lost its link to the drive.</summary>
    DriveLinkLost = 6,

    /// <summary>7 — safety stop (E-stop chain, guard).</summary>
    SafetyStop = 7,

    /// <summary>First vendor-specific code; documented per PLC.</summary>
    VendorFirst = 100,
}
