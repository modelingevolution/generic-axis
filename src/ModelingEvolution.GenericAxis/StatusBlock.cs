namespace ModelingEvolution.GenericAxis;

/// <summary>
/// One read of the status block S+0 … S+14, served by the PLC from one scan's image (protocol § Status block).
/// Values are raw register values; <see cref="Words.FromRaw"/> turns them into axis units.
/// </summary>
/// <param name="State">S+0, raw — the SDK <c>AxisState</c> number; unknown values are read as ErrorStop.</param>
/// <param name="Flags">S+1.</param>
/// <param name="ActualPosition">S+2…S+3, 0.001 unit. Valid only while <see cref="StatusFlags.Homed"/>.</param>
/// <param name="ActualVelocity">S+4…S+5, signed 0.001 unit/s.</param>
/// <param name="FaultCode">S+6, raw (see <see cref="PlcFaultCode"/>).</param>
/// <param name="CommandAck">S+7 — the last accepted <c>CommandSeq</c>.</param>
/// <param name="TravelMin">S+8…S+9, machine limit, 0.001 unit.</param>
/// <param name="TravelMax">S+10…S+11, machine limit, 0.001 unit.</param>
/// <param name="MaxVelocity">S+12…S+13, machine limit, 0.001 unit/s.</param>
/// <param name="MapVersion">S+14.</param>
public readonly record struct StatusBlock(
    ushort State,
    StatusFlags Flags,
    int ActualPosition,
    int ActualVelocity,
    ushort FaultCode,
    ushort CommandAck,
    int TravelMin,
    int TravelMax,
    int MaxVelocity,
    ushort MapVersion)
{
    /// <summary>Whether <c>Homed</c> is set.</summary>
    public bool Homed => (Flags & StatusFlags.Homed) != 0;

    /// <summary>Whether <c>InPosition</c> is set.</summary>
    public bool InPosition => (Flags & StatusFlags.InPosition) != 0;

    /// <summary>
    /// Whether the PLC publishes limits at all. All three zero means "not published"; anything else is a
    /// publication that must satisfy <see cref="LimitsValid"/>.
    /// </summary>
    public bool LimitsPublished => TravelMin != 0 || TravelMax != 0 || MaxVelocity != 0;

    /// <summary>A publication is valid only as a whole: <c>TravelMin &lt; TravelMax</c> and <c>MaxVelocity &gt; 0</c>.</summary>
    public bool LimitsValid => TravelMin < TravelMax && MaxVelocity > 0;

    /// <summary>Parses exactly <see cref="RegisterMap.StatusLength"/> registers.</summary>
    /// <exception cref="ArgumentException">The span does not hold exactly 15 registers.</exception>
    public static StatusBlock Parse(ReadOnlySpan<ushort> words)
    {
        if (words.Length != RegisterMap.StatusLength)
            throw new ArgumentException(
                $"A status block is {RegisterMap.StatusLength} registers (S+0…S+14); got {words.Length}",
                nameof(words));

        return new StatusBlock(
            State: words[0],
            Flags: (StatusFlags)words[1],
            ActualPosition: Words.Read(words[2..]),
            ActualVelocity: Words.Read(words[4..]),
            FaultCode: words[6],
            CommandAck: words[7],
            TravelMin: Words.Read(words[8..]),
            TravelMax: Words.Read(words[10..]),
            MaxVelocity: Words.Read(words[12..]),
            MapVersion: words[14]);
    }
}

/// <summary>
/// One heartbeat tick's view of the PLC: the status block plus the watchdog registers C+9 … C+11, read in the
/// same tick. The tick is the axis' status cadence (ADR-15).
/// </summary>
/// <param name="Status">S+0 … S+14.</param>
/// <param name="LeaseOwner">C+9.</param>
/// <param name="WatchdogFault">C+10: 0 healthy, 1 tripped.</param>
/// <param name="WatchdogTrips">C+11: trips since PLC power-up.</param>
/// <param name="ReadAt"><see cref="TimeProvider.GetTimestamp"/> when the status read completed.</param>
public readonly record struct PlcSnapshot(
    StatusBlock Status,
    ushort LeaseOwner,
    ushort WatchdogFault,
    ushort WatchdogTrips,
    long ReadAt);
