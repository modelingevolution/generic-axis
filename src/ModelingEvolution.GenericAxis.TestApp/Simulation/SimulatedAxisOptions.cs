namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>Axis kind: the map is the same, only the unit differs (0.001 mm or 0.001°).</summary>
public enum SimAxisKind
{
    Linear,
    Rotary,
}

/// <summary>
/// Configuration section <c>Simulator</c>. These are simulator test values, not machine numbers (ADR-21): Pamet's
/// physics stays on the bench. Positions in axis units (mm or °), velocities in unit/s, accelerations in unit/s².
/// </summary>
public sealed record SimulatedAxisOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Modbus TCP port. 0 binds a free port (tests); <see cref="SimulatorHost.Port"/> reports it.</summary>
    public int Port { get; init; } = 5020;

    public byte UnitId { get; init; } = 1;
    /// <summary>Holding-register base C of the command block (protocol § Transport).</summary>
    public int CommandBase { get; init; } = 0;
    /// <summary>Input-register base S of the status block, in its own address space (ADR-36).</summary>
    public int StatusBase { get; init; } = 0;
    public SimAxisKind Kind { get; init; } = SimAxisKind.Linear;
    public double TravelMin { get; init; } = 0;
    public double TravelMax { get; init; } = 10_000;
    public double MaxVelocity { get; init; } = 500;
    /// <summary>Ramp-up when the <c>Acceleration</c> register (C+6…C+7) is 0 = PLC default.</summary>
    public double DefaultAcceleration { get; init; } = 1_000;
    /// <summary>Braking ramp (arrival, travel limit, homing) when the <c>Acceleration</c> register is 0.</summary>
    public double DefaultDeceleration { get; init; } = 1_000;
    public double QuickStopDeceleration { get; init; } = 5_000;
    public double HomingVelocity { get; init; } = 50;
    public double HomeSensorPosition { get; init; } = 0;
    public double HomeSensorWidth { get; init; } = 5;
    public double LimitSwitchMargin { get; init; } = 20;
    public double InPositionWindow { get; init; } = 0.005;
    public double InitialPosition { get; init; } = 500;
    public bool HomedAtPowerUp { get; init; } = true;
    public double UnhomedOffset { get; init; } = 1234.567;
    public TimeSpan EnableDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    /// <summary>FR-11 stall window: the watchdog trips this long after the last beat change. The protocol allows
    /// 1.0–1.5 s; tests use the upper end for a PLC that trips late (review #35).</summary>
    public TimeSpan WatchdogTimeout { get; init; } = TimeSpan.FromSeconds(1);
    public bool PublishLimits { get; init; } = true;
    public ushort MapVersion { get; init; } = 1;
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(10);
    public SimFaults Faults { get; init; } = SimFaults.None;

    /// <summary>Unit label for logs and the UI.</summary>
    public string Unit => Kind == SimAxisKind.Rotary ? "°" : "mm";

    /// <summary>Throws <see cref="ArgumentException"/> naming the first invalid field.</summary>
    public SimulatedAxisOptions Validate()
    {
        if (Port is < 0 or > 65535) throw new ArgumentException($"{nameof(Port)} must be 0–65535, got {Port}", nameof(Port));
        if (CommandBase < 0 || CommandBase + SimRegisters.CommandLength > 65536)
            throw new ArgumentException($"{nameof(CommandBase)} {CommandBase} puts the command block past 65535", nameof(CommandBase));
        if (StatusBase < 0 || StatusBase + SimRegisters.StatusLength > 65536)
            throw new ArgumentException($"{nameof(StatusBase)} {StatusBase} puts the status block past 65535", nameof(StatusBase));
        if (TravelMin >= TravelMax) throw new ArgumentException($"{nameof(TravelMin)} {TravelMin} must be below {nameof(TravelMax)} {TravelMax}", nameof(TravelMin));
        if (MaxVelocity <= 0) throw new ArgumentException($"{nameof(MaxVelocity)} must be > 0", nameof(MaxVelocity));
        if (DefaultAcceleration <= 0) throw new ArgumentException($"{nameof(DefaultAcceleration)} must be > 0", nameof(DefaultAcceleration));
        if (DefaultDeceleration <= 0) throw new ArgumentException($"{nameof(DefaultDeceleration)} must be > 0", nameof(DefaultDeceleration));
        if (QuickStopDeceleration <= 0) throw new ArgumentException($"{nameof(QuickStopDeceleration)} must be > 0", nameof(QuickStopDeceleration));
        if (HomingVelocity <= 0) throw new ArgumentException($"{nameof(HomingVelocity)} must be > 0", nameof(HomingVelocity));
        if (HomeSensorWidth <= 0) throw new ArgumentException($"{nameof(HomeSensorWidth)} must be > 0", nameof(HomeSensorWidth));
        if (LimitSwitchMargin <= 0) throw new ArgumentException($"{nameof(LimitSwitchMargin)} must be > 0", nameof(LimitSwitchMargin));
        if (InPositionWindow <= 0) throw new ArgumentException($"{nameof(InPositionWindow)} must be > 0", nameof(InPositionWindow));
        if (ScanInterval <= TimeSpan.Zero) throw new ArgumentException($"{nameof(ScanInterval)} must be > 0", nameof(ScanInterval));
        if (EnableDelay < TimeSpan.Zero) throw new ArgumentException($"{nameof(EnableDelay)} must be >= 0", nameof(EnableDelay));
        if (WatchdogTimeout < TimeSpan.FromSeconds(1) || WatchdogTimeout > TimeSpan.FromSeconds(1.5))
            throw new ArgumentException($"{nameof(WatchdogTimeout)} must be 1.0–1.5 s (FR-11), got {WatchdogTimeout.TotalSeconds} s", nameof(WatchdogTimeout));

        // Every value the PLC publishes must fit its int32 register at 0.001 per count: refuse, never clamp.
        var reach = 2 * LimitSwitchMargin + Math.Abs(UnhomedOffset);
        foreach (var (name, value) in new[]
                 {
                     (nameof(TravelMin), TravelMin - reach), (nameof(TravelMax), TravelMax + reach),
                     (nameof(MaxVelocity), MaxVelocity), (nameof(HomingVelocity), HomingVelocity),
                 })
        {
            if (Math.Abs(value * SimRegisters.Scale) > int.MaxValue)
                throw new ArgumentException(
                    $"{name} reaches {value}, which does not fit an int32 register at scale {SimRegisters.Scale}", name);
        }

        return this;
    }
}
