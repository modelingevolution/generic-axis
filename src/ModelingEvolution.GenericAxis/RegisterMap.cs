namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The register map of <c>docs/protocol.md</c>, map version 1: a <b>command block</b> of 12 holding registers at
/// <see cref="CommandBase"/> (written by the driver, FC03/FC06/FC16) and a <b>status block</b> of 15 input registers at
/// <see cref="StatusBase"/> (written by the PLC, FC04) — ADR-36. Each block lives in its own address space, so both
/// bases default to 0. Offsets inside a block are fixed; only the two bases move, on both sides, for a PLC whose
/// arrays cannot start at 0 or that serves a second axis.
/// </summary>
/// <param name="CommandBase">Base <c>C</c> of the command block (holding registers). Protocol default 0.</param>
/// <param name="StatusBase">Base <c>S</c> of the status block (input registers). Protocol default 0.</param>
public sealed record RegisterMap(int CommandBase = RegisterMap.DefaultCommandBase,
    int StatusBase = RegisterMap.DefaultStatusBase)
{
    /// <summary>The map version this driver speaks (register S+14). Protocol: "MapVersion (S+14) = 1 for this document".</summary>
    public const ushort Version = 1;

    /// <summary>Protocol default of the command block base.</summary>
    public const int DefaultCommandBase = 0;

    /// <summary>Protocol default of the status block base (input registers, ADR-36).</summary>
    public const int DefaultStatusBase = 0;

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

    /// <summary>The protocol defaults: holding base 0, input base 0 (ADR-36).</summary>
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
    /// Refuses a map the protocol cannot serve: a negative base, or a block that runs past register 65535 of its own
    /// address space. The blocks live in separate spaces (holding and input, ADR-36), so equal bases are valid.
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
                $"The command block C+0…C+{CommandLength - 1} at holding CommandBase {CommandBase} runs past register 65535",
                nameof(CommandBase));

        if (StatusBase + StatusLength - 1 > ushort.MaxValue)
            throw new ArgumentException(
                $"The status block S+0…S+{StatusLength - 1} at input StatusBase {StatusBase} runs past register 65535",
                nameof(StatusBase));
    }

    /// <summary>The absolute address of a field under this map's bases.</summary>
    public ushort Address(RegisterField field) =>
        field.Space == RegisterSpace.Holding ? C(field.Offset) : S(field.Offset);

    /// <summary>
    /// A field's address in the protocol's form (§ Errors and debugging, rule 1): <c>C+n = holding a</c> or
    /// <c>S+n = input a</c>, a = base + n.
    /// </summary>
    public string Describe(RegisterField field) => DescribeRange(field.Space, Address(field), 1);

    /// <summary>
    /// A register range in the protocol's form: <c>S+0…S+14 = input 0…14</c>, <c>C+9 = holding 9</c>. A range outside
    /// the space's block names only the space and the absolute addresses (<c>holding 500…501</c>). The space must be
    /// given: with both bases 0 an address alone does not say which block it is in.
    /// </summary>
    public string DescribeRange(RegisterSpace space, ushort address, int count)
    {
        var (letter, @base, length, kind) = space == RegisterSpace.Holding
            ? ("C", CommandBase, CommandLength, "holding")
            : ("S", StatusBase, StatusLength, "input");
        var last = address + Math.Max(count, 1) - 1;
        var inBlock = address >= @base && last < @base + length;
        string Abs(int a) => a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!inBlock)
            return last == address ? $"{kind} {Abs(address)}" : $"{kind} {Abs(address)}…{Abs(last)}";
        return last == address
            ? $"{letter}+{address - @base} = {kind} {Abs(address)}"
            : $"{letter}+{address - @base}…{letter}+{last - @base} = {kind} {Abs(address)}…{Abs(last)}";
    }

    private ushort C(int offset) => checked((ushort)(CommandBase + offset));

    private ushort S(int offset) => checked((ushort)(StatusBase + offset));
}

/// <summary>The Modbus address space a block lives in (protocol § Transport, ADR-36).</summary>
public enum RegisterSpace
{
    /// <summary>Holding registers (FC03 read, FC06/FC16 write): the command block.</summary>
    Holding,

    /// <summary>Input registers (FC04 read): the status block.</summary>
    Input,
}

/// <summary>
/// One named register of map version 1: its protocol name, its address space and its offset in the block. Name, space
/// and offset are declared once here, so a message can never pair a name with another register's address.
/// </summary>
/// <param name="Name">The protocol's register name.</param>
/// <param name="Space">Holding (command block) or input (status block).</param>
/// <param name="Offset">The offset n in <c>C+n</c> / <c>S+n</c>.</param>
public readonly record struct RegisterField(string Name, RegisterSpace Space, int Offset)
{
    /// <summary>C+0.</summary>
    public static readonly RegisterField Command = new("Command", RegisterSpace.Holding, 0);
    /// <summary>C+1.</summary>
    public static readonly RegisterField CommandSeq = new("CommandSeq", RegisterSpace.Holding, 1);
    /// <summary>C+2…C+3.</summary>
    public static readonly RegisterField TargetPosition = new("TargetPosition", RegisterSpace.Holding, 2);
    /// <summary>C+4…C+5.</summary>
    public static readonly RegisterField Velocity = new("Velocity", RegisterSpace.Holding, 4);
    /// <summary>C+6…C+7.</summary>
    public static readonly RegisterField Acceleration = new("Acceleration", RegisterSpace.Holding, 6);
    /// <summary>C+8.</summary>
    public static readonly RegisterField Heartbeat = new("Heartbeat", RegisterSpace.Holding, 8);
    /// <summary>C+9.</summary>
    public static readonly RegisterField LeaseOwner = new("LeaseOwner", RegisterSpace.Holding, 9);
    /// <summary>C+10 (PLC-written holding register).</summary>
    public static readonly RegisterField WatchdogFault = new("WatchdogFault", RegisterSpace.Holding, 10);
    /// <summary>C+11 (PLC-written holding register).</summary>
    public static readonly RegisterField WatchdogTrips = new("WatchdogTrips", RegisterSpace.Holding, 11);

    /// <summary>S+0.</summary>
    public static readonly RegisterField State = new("State", RegisterSpace.Input, 0);
    /// <summary>S+1.</summary>
    public static readonly RegisterField Flags = new("Flags", RegisterSpace.Input, 1);
    /// <summary>S+2…S+3.</summary>
    public static readonly RegisterField ActualPosition = new("ActualPosition", RegisterSpace.Input, 2);
    /// <summary>S+4…S+5.</summary>
    public static readonly RegisterField ActualVelocity = new("ActualVelocity", RegisterSpace.Input, 4);
    /// <summary>S+6.</summary>
    public static readonly RegisterField FaultCode = new("FaultCode", RegisterSpace.Input, 6);
    /// <summary>S+7.</summary>
    public static readonly RegisterField CommandAck = new("CommandAck", RegisterSpace.Input, 7);
    /// <summary>S+8…S+9.</summary>
    public static readonly RegisterField TravelMin = new("TravelMin", RegisterSpace.Input, 8);
    /// <summary>S+10…S+11.</summary>
    public static readonly RegisterField TravelMax = new("TravelMax", RegisterSpace.Input, 10);
    /// <summary>S+12…S+13.</summary>
    public static readonly RegisterField MaxVelocity = new("MaxVelocity", RegisterSpace.Input, 12);
    /// <summary>S+14.</summary>
    public static readonly RegisterField MapVersion = new("MapVersion", RegisterSpace.Input, 14);
}
