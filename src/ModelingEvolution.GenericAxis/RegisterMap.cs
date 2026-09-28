namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The register map of <c>docs/protocol.md</c>, map version 1: a <b>command block</b> of 12 holding registers at
/// <see cref="CommandBase"/> (written by the driver) and a <b>status block</b> of 15 holding registers at
/// <see cref="StatusBase"/> (written by the PLC). Offsets inside a block are fixed; only the two bases move, on
/// both sides, for a PLC whose register file cannot start at 0/100 or that serves a second axis.
/// </summary>
/// <param name="CommandBase">Base <c>C</c> of the command block. Protocol default 0.</param>
/// <param name="StatusBase">Base <c>S</c> of the status block. Protocol default 100.</param>
public sealed record RegisterMap(int CommandBase = RegisterMap.DefaultCommandBase,
    int StatusBase = RegisterMap.DefaultStatusBase)
{
    /// <summary>The map version this driver speaks (register S+14). Protocol: "register 114 = 1 for this document".</summary>
    public const ushort Version = 1;

    /// <summary>Protocol default of the command block base.</summary>
    public const int DefaultCommandBase = 0;

    /// <summary>Protocol default of the status block base.</summary>
    public const int DefaultStatusBase = 100;

    /// <summary>Registers in the command block, C+0 … C+11.</summary>
    public const int CommandLength = 12;

    /// <summary>Registers in the status block, S+0 … S+14. The PLC serves them from one scan's image.</summary>
    public const int StatusLength = 15;

    /// <summary>Registers C+2 … C+7: <c>TargetPosition</c>, <c>Velocity</c>, <c>Acceleration</c>, written in one FC16.</summary>
    public const int ParametersLength = 6;

    /// <summary>Registers C+9 … C+11 read by every heartbeat tick: <c>LeaseOwner</c>, <c>WatchdogFault</c>, <c>WatchdogTrips</c>.</summary>
    public const int WatchdogBlockLength = 3;

    /// <summary>Protocol "Acknowledge": the driver waits at most 500 ms for <c>CommandAck == CommandSeq</c>.</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Protocol FR-11: the PLC trips when <c>Heartbeat</c> has not changed for 1 s. Also the lease expiry.</summary>
    public static readonly TimeSpan WatchdogWindow = TimeSpan.FromSeconds(1);

    /// <summary>Protocol FR-11: heartbeat deferral behind move traffic is bounded at 200 ms.</summary>
    public static readonly TimeSpan HeartbeatDeferralBound = TimeSpan.FromMilliseconds(200);

    /// <summary>The protocol defaults, 0 / 100.</summary>
    public static RegisterMap Default { get; } = new();

    // ── command block ───────────────────────────────────────────

    /// <summary>C+0 <c>Command</c> bitfield (<see cref="CommandBits"/>).</summary>
    public ushort Command => C(0);

    /// <summary>C+1 <c>CommandSeq</c>.</summary>
    public ushort CommandSeq => C(1);

    /// <summary>C+2 — first of the parameter registers (<c>TargetPosition</c>, int32 low word first).</summary>
    public ushort Parameters => C(2);

    /// <summary>C+2…C+3 <c>TargetPosition</c>.</summary>
    public ushort TargetPosition => C(2);

    /// <summary>C+4…C+5 <c>Velocity</c>.</summary>
    public ushort Velocity => C(4);

    /// <summary>C+6…C+7 <c>Acceleration</c>.</summary>
    public ushort Acceleration => C(6);

    /// <summary>C+8 <c>Heartbeat</c>.</summary>
    public ushort Heartbeat => C(8);

    /// <summary>C+9 <c>LeaseOwner</c>.</summary>
    public ushort LeaseOwner => C(9);

    /// <summary>C+10 <c>WatchdogFault</c>.</summary>
    public ushort WatchdogFault => C(10);

    /// <summary>C+11 <c>WatchdogTrips</c>.</summary>
    public ushort WatchdogTrips => C(11);

    // ── status block ────────────────────────────────────────────

    /// <summary>S+0 — the first register of the status block (<c>State</c>).</summary>
    public ushort Status => S(0);

    /// <summary>S+0 <c>State</c>.</summary>
    public ushort State => S(0);

    /// <summary>S+1 <c>Flags</c>.</summary>
    public ushort Flags => S(1);

    /// <summary>S+2…S+3 <c>ActualPosition</c>.</summary>
    public ushort ActualPosition => S(2);

    /// <summary>S+4…S+5 <c>ActualVelocity</c>.</summary>
    public ushort ActualVelocity => S(4);

    /// <summary>S+6 <c>FaultCode</c>.</summary>
    public ushort FaultCode => S(6);

    /// <summary>S+7 <c>CommandAck</c>.</summary>
    public ushort CommandAck => S(7);

    /// <summary>S+8…S+9 <c>TravelMin</c>.</summary>
    public ushort TravelMin => S(8);

    /// <summary>S+10…S+11 <c>TravelMax</c>.</summary>
    public ushort TravelMax => S(10);

    /// <summary>S+12…S+13 <c>MaxVelocity</c>.</summary>
    public ushort MaxVelocity => S(12);

    /// <summary>S+14 <c>MapVersion</c> — the register's address; the version this driver speaks is <see cref="Version"/>.</summary>
    public ushort MapVersion => S(14);

    /// <summary>
    /// Refuses a map the protocol cannot serve: a negative base, a block that runs past register 65535, or two
    /// blocks that overlap.
    /// </summary>
    /// <exception cref="ArgumentException">The map is invalid; the message names the bases.</exception>
    public void Validate()
    {
        if (CommandBase < 0 || StatusBase < 0)
            throw new ArgumentException(
                $"Register bases must be non-negative (CommandBase {CommandBase}, StatusBase {StatusBase})",
                nameof(CommandBase));

        if (CommandBase + CommandLength - 1 > ushort.MaxValue)
            throw new ArgumentException(
                $"The command block C+0…C+{CommandLength - 1} at CommandBase {CommandBase} runs past register 65535",
                nameof(CommandBase));

        if (StatusBase + StatusLength - 1 > ushort.MaxValue)
            throw new ArgumentException(
                $"The status block S+0…S+{StatusLength - 1} at StatusBase {StatusBase} runs past register 65535",
                nameof(StatusBase));

        var commandEnd = CommandBase + CommandLength;
        var statusEnd = StatusBase + StatusLength;
        if (CommandBase < statusEnd && StatusBase < commandEnd)
            throw new ArgumentException(
                $"The command block {CommandBase}…{commandEnd - 1} overlaps the status block {StatusBase}…{statusEnd - 1}",
                nameof(StatusBase));
    }

    /// <summary>"C+n = address" / "S+n = address" — the protocol's form in messages and logs, e.g. <c>S+14 = 114</c>.</summary>
    public string Describe(ushort address) =>
        address >= CommandBase && address < CommandBase + CommandLength
            ? $"C+{address - CommandBase} = {address}"
            : address >= StatusBase && address < StatusBase + StatusLength
                ? $"S+{address - StatusBase} = {address}"
                : address.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A register range in the protocol's form, e.g. <c>S+0…S+14 (100…114)</c>.</summary>
    public string DescribeRange(ushort address, int count)
    {
        var last = address + count - 1;
        string Offset(int a) =>
            a >= CommandBase && a < CommandBase + CommandLength ? $"C+{a - CommandBase}"
            : a >= StatusBase && a < StatusBase + StatusLength ? $"S+{a - StatusBase}"
            : a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return count <= 1 ? $"{Offset(address)} ({address})" : $"{Offset(address)}…{Offset(last)} ({address}…{last})";
    }

    private ushort C(int offset) => checked((ushort)(CommandBase + offset));

    private ushort S(int offset) => checked((ushort)(StatusBase + offset));
}
