using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// Per-axis configuration (design § <c>GenericAxisOptions</c>). Every default is named after its source: the
/// protocol, the design's supervision budgets (ADR-20), or "absent" — a machine limit is never defaulted.
/// </summary>
public sealed record GenericAxisOptions
{
    /// <summary>Protocol § Transport: port 502.</summary>
    public const int DefaultPort = 502;

    /// <summary>Protocol § Transport: unit id 1.</summary>
    public const byte DefaultUnitId = 1;

    /// <summary>Design: 10 Hz beat, one beat of headroom under a full 200 ms deferral (ADR-15).</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Shortest accepted beat interval (design: 20 ms).</summary>
    public static readonly TimeSpan MinHeartbeatInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>Longest accepted beat interval: the protocol's ≥ 5 Hz.</summary>
    public static readonly TimeSpan MaxHeartbeatInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>ADR-18: a move with no speed runs at 100 % of <c>MaxVelocity</c>.</summary>
    public static readonly Percentage DefaultDefaultSpeed = new(100f);

    /// <summary>ADR-19: one register quantum.</summary>
    public const double DefaultTolerance = Words.Quantum;

    /// <summary>ADR-20: enable/disable and reset confirmation budget.</summary>
    public static readonly TimeSpan DefaultEnableTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ADR-20: stop confirmation budget.</summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>ADR-20: homing budget.</summary>
    public static readonly TimeSpan DefaultHomingTimeout = TimeSpan.FromSeconds(120);

    /// <summary>ADR-20: margin added to distance ÷ speed for a move's budget.</summary>
    public static readonly TimeSpan DefaultMoveTimeoutMargin = TimeSpan.FromSeconds(10);

    /// <summary>Axis name, frozen (<c>carriage</c> / <c>turntable</c> from the plugin).</summary>
    public string Name { get; init; } = "";

    /// <summary>Operator-facing label; <see cref="Name"/> when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary><c>Linear</c> or <c>Rotary</c>; set by the device class.</summary>
    public AxisKind Kind { get; init; } = AxisKind.Linear;

    /// <summary>PLC host. Required.</summary>
    public string Host { get; init; } = "";

    /// <summary>Modbus TCP port, 1–65535.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Modbus unit id, 0–255.</summary>
    public int UnitId { get; init; } = DefaultUnitId;

    /// <summary>Block bases; <see cref="RegisterMap.Default"/> is 0 / 100.</summary>
    public RegisterMap Map { get; init; } = RegisterMap.Default;

    /// <summary>Heartbeat tick period, 20–200 ms.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;

    /// <summary>How long to wait for a live foreign lease; <see langword="null"/> = until the caller cancels.</summary>
    public TimeSpan? LeaseTimeout { get; init; }

    /// <summary>Travel minimum, used only when the PLC publishes no limits. Record where the number came from.</summary>
    public double? ConfiguredTravelMin { get; init; }

    /// <summary>Travel maximum, used only when the PLC publishes no limits.</summary>
    public double? ConfiguredTravelMax { get; init; }

    /// <summary>Maximum velocity, used only when the PLC publishes no limits.</summary>
    public double? ConfiguredMaxVelocity { get; init; }

    /// <summary>Accepted reading range minimum (feature-001 FR-10); <see langword="null"/> = the effective travel.</summary>
    public double? ReadMin { get; init; }

    /// <summary>Accepted reading range maximum; <see langword="null"/> = the effective travel.</summary>
    public double? ReadMax { get; init; }

    /// <summary>Speed of a move that names none, as a share of <c>MaxVelocity</c>; 0 &lt; p ≤ 100.</summary>
    public Percentage DefaultSpeed { get; init; } = DefaultDefaultSpeed;

    /// <summary>Acceleration for every move; <see langword="null"/> writes 0 = the PLC's default ramp.</summary>
    public double? Acceleration { get; init; }

    /// <summary>Reported in-position tolerance. The PLC's <c>InPosition</c> decides arrival.</summary>
    public double Tolerance { get; init; } = DefaultTolerance;

    /// <summary>Budget for Enable/Disable and Reset to show their state.</summary>
    public TimeSpan EnableTimeout { get; init; } = DefaultEnableTimeout;

    /// <summary>Budget for Stop to show Standstill.</summary>
    public TimeSpan StopTimeout { get; init; } = DefaultStopTimeout;

    /// <summary>Budget for the PLC's homing sequence.</summary>
    public TimeSpan HomingTimeout { get; init; } = DefaultHomingTimeout;

    /// <summary>Margin added to distance ÷ speed for a discrete move's budget.</summary>
    public TimeSpan MoveTimeoutMargin { get; init; } = DefaultMoveTimeoutMargin;

    /// <summary>The label to show: <see cref="DisplayName"/> or <see cref="Name"/>.</summary>
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>Checks every rule of the design's options table.</summary>
    /// <exception cref="ArgumentException">A value breaks its rule; <see cref="ArgumentException.ParamName"/> is the field.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name is required", nameof(Name));
        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException($"{Name}: Host is required", nameof(Host));
        if (Port is < 1 or > 65535)
            throw new ArgumentException($"{Name}: Port {Port} is outside 1–65535", nameof(Port));
        if (UnitId is < 0 or > 255)
            throw new ArgumentException($"{Name}: UnitId {UnitId} is outside 0–255", nameof(UnitId));

        ArgumentNullException.ThrowIfNull(Map, nameof(Map));
        try
        {
            Map.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{Name}: {ex.Message}", nameof(Map), ex);
        }

        if (HeartbeatInterval < MinHeartbeatInterval || HeartbeatInterval > MaxHeartbeatInterval)
            throw new ArgumentException(
                $"{Name}: HeartbeatInterval {HeartbeatInterval.TotalMilliseconds} ms is outside "
                + $"{MinHeartbeatInterval.TotalMilliseconds}–{MaxHeartbeatInterval.TotalMilliseconds} ms (protocol: ≥ 5 Hz)",
                nameof(HeartbeatInterval));
        if (LeaseTimeout is { } lease && lease < TimeSpan.Zero)
            throw new ArgumentException($"{Name}: LeaseTimeout must be ≥ 0", nameof(LeaseTimeout));

        if (ConfiguredTravelMin is { } min && ConfiguredTravelMax is { } max && min >= max)
            throw new ArgumentException(
                $"{Name}: ConfiguredTravelMin {min} must be below ConfiguredTravelMax {max}", nameof(ConfiguredTravelMin));
        if (ConfiguredMaxVelocity is { } v && v <= 0)
            throw new ArgumentException($"{Name}: ConfiguredMaxVelocity {v} must be > 0", nameof(ConfiguredMaxVelocity));

        if (ReadMin is { } rmin && ReadMax is { } rmax && rmin >= rmax)
            throw new ArgumentException($"{Name}: ReadMin {rmin} must be below ReadMax {rmax}", nameof(ReadMin));
        if (ReadMin is { } rm && ConfiguredTravelMin is { } tmin && rm > tmin)
            throw new ArgumentException(
                $"{Name}: ReadMin {rm} is above ConfiguredTravelMin {tmin}; the reading range must contain the travel",
                nameof(ReadMin));
        if (ReadMax is { } rx && ConfiguredTravelMax is { } tmax && rx < tmax)
            throw new ArgumentException(
                $"{Name}: ReadMax {rx} is below ConfiguredTravelMax {tmax}; the reading range must contain the travel",
                nameof(ReadMax));

        if (DefaultSpeed.Value <= 0f)
            throw new ArgumentException($"{Name}: DefaultSpeed must be above 0 %", nameof(DefaultSpeed));
        if (Acceleration is { } a && a <= 0)
            throw new ArgumentException($"{Name}: Acceleration {a} must be > 0 when set", nameof(Acceleration));
        if (Tolerance <= 0)
            throw new ArgumentException($"{Name}: Tolerance {Tolerance} must be > 0", nameof(Tolerance));
        if (EnableTimeout <= TimeSpan.Zero)
            throw new ArgumentException($"{Name}: EnableTimeout must be > 0", nameof(EnableTimeout));
        if (StopTimeout <= TimeSpan.Zero)
            throw new ArgumentException($"{Name}: StopTimeout must be > 0", nameof(StopTimeout));
        if (HomingTimeout <= TimeSpan.Zero)
            throw new ArgumentException($"{Name}: HomingTimeout must be > 0", nameof(HomingTimeout));
        if (MoveTimeoutMargin <= TimeSpan.Zero)
            throw new ArgumentException($"{Name}: MoveTimeoutMargin must be > 0", nameof(MoveTimeoutMargin));
    }
}
