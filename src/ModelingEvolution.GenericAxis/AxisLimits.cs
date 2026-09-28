namespace ModelingEvolution.GenericAxis;

/// <summary>Where the effective machine limits came from (design § Limits).</summary>
public enum LimitSource
{
    /// <summary>No limits: the PLC publishes none and none are configured. Every move is refused.</summary>
    None,

    /// <summary>Published by the PLC in S+8 … S+13.</summary>
    Plc,

    /// <summary>The PLC publishes none; the configured values stand in.</summary>
    Configuration,
}

/// <summary>The effective machine limits in axis units, with their source. Never clamped to, only refused against.</summary>
/// <param name="TravelMin">Minimum target, mm or °.</param>
/// <param name="TravelMax">Maximum target, mm or °.</param>
/// <param name="MaxVelocity">Fastest accepted speed, unit/s.</param>
/// <param name="Source">Where they came from; 0/0/0 with <see cref="LimitSource.None"/>.</param>
public readonly record struct AxisLimits(double TravelMin, double TravelMax, double MaxVelocity, LimitSource Source)
{
    /// <summary>No limit source: every move is refused.</summary>
    public static AxisLimits None { get; } = new(0, 0, 0, LimitSource.None);

    /// <summary>"published by the PLC" / "from configuration" / "no limit source", for refusal messages.</summary>
    public string SourceText => Source switch
    {
        LimitSource.Plc => "published by the PLC",
        LimitSource.Configuration => "from configuration",
        _ => "no limit source",
    };
}
