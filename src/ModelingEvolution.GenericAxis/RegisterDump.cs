using System.Globalization;
using System.Text;

namespace ModelingEvolution.GenericAxis;

/// <summary>One decoded field of a register dump.</summary>
/// <param name="Address">The protocol's address text: <c>C+0 = holding 0</c>, or <c>S+8…S+9 = input 8…9</c> for an
/// int32 (§ Errors and debugging, rule 1).</param>
/// <param name="Name">The protocol's register name.</param>
/// <param name="RawHex">The raw registers in hex, in wire order (an int32 low word first).</param>
/// <param name="Value">The decoded value: engineering units with three decimals, or names for <c>State</c>,
/// <c>FaultCode</c>, <c>Flags</c> and <c>Command</c>.</param>
/// <param name="Space">The block's address space: holding (command block) or input (status block), ADR-36.</param>
/// <param name="AbsoluteAddress">The absolute register address of the field's first register (base + offset).</param>
public sealed record RegisterRow(string Address, string Name, string RawHex, string Value,
    RegisterSpace Space = RegisterSpace.Holding, int AbsoluteAddress = 0);

/// <summary>
/// Decodes a command block and a status block into rows (protocol § Errors and debugging, rule 4: "A register dump
/// comes first"). The one decoder behind the test app's <c>/registers</c>, the C# <c>--dump</c> and a report's
/// <c>lastRead</c>. It decodes; it never reads or writes the PLC.
/// </summary>
public static class RegisterDump
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Decodes both blocks, command block first.</summary>
    /// <param name="map">The block bases, for the address column.</param>
    /// <param name="command">C+0 … C+11, exactly <see cref="RegisterMap.CommandLength"/> registers.</param>
    /// <param name="status">S+0 … S+14, exactly <see cref="RegisterMap.StatusLength"/> registers.</param>
    /// <exception cref="ArgumentException">A block has the wrong length.</exception>
    public static IReadOnlyList<RegisterRow> Decode(RegisterMap map, ReadOnlySpan<ushort> command,
        ReadOnlySpan<ushort> status)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (command.Length != RegisterMap.CommandLength)
            throw new ArgumentException(
                $"A command block is {RegisterMap.CommandLength} registers (C+0…C+11); got {command.Length}", nameof(command));
        var block = StatusBlock.Parse(status);

        return
        [
            Word(map, RegisterSpace.Holding, 0, "Command", command[0], Bits((CommandBits)command[0])),
            Word(map, RegisterSpace.Holding, 1, "CommandSeq", command[1], Num(command[1])),
            Pair(map, RegisterSpace.Holding, 2, "TargetPosition", command[2], command[3]),
            Pair(map, RegisterSpace.Holding, 4, "Velocity", command[4], command[5]),
            Pair(map, RegisterSpace.Holding, 6, "Acceleration", command[6], command[7]),
            Word(map, RegisterSpace.Holding, 8, "Heartbeat", command[8], Num(command[8])),
            Word(map, RegisterSpace.Holding, 9, "LeaseOwner", command[9], command[9] == 0 ? "0 (unowned)" : Num(command[9])),
            Word(map, RegisterSpace.Holding, 10, "WatchdogFault", command[10], command[10] switch
            {
                0 => "0 (healthy)",
                1 => "1 (tripped)",
                var v => $"{v} (undefined)",
            }),
            Word(map, RegisterSpace.Holding, 11, "WatchdogTrips", command[11], Num(command[11])),

            Word(map, RegisterSpace.Input, 0, "State", status[0], StateName(status[0])),
            Word(map, RegisterSpace.Input, 1, "Flags", status[1], Bits(block.Flags)),
            Pair(map, RegisterSpace.Input, 2, "ActualPosition", status[2], status[3]),
            Pair(map, RegisterSpace.Input, 4, "ActualVelocity", status[4], status[5]),
            Word(map, RegisterSpace.Input, 6, "FaultCode", status[6], FaultName(status[6])),
            Word(map, RegisterSpace.Input, 7, "CommandAck", status[7], Num(status[7])),
            Pair(map, RegisterSpace.Input, 8, "TravelMin", status[8], status[9]),
            Pair(map, RegisterSpace.Input, 10, "TravelMax", status[10], status[11]),
            Pair(map, RegisterSpace.Input, 12, "MaxVelocity", status[12], status[13]),
            Word(map, RegisterSpace.Input, 14, "MapVersion", status[14], Num(status[14])),
        ];
    }

    /// <summary>A fixed-width text table: address, name, raw hex, value.</summary>
    public static string Format(IReadOnlyList<RegisterRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int wa = "Address".Length, wn = "Name".Length, wr = "Raw".Length;
        foreach (var r in rows)
        {
            wa = Math.Max(wa, r.Address.Length);
            wn = Math.Max(wn, r.Name.Length);
            wr = Math.Max(wr, r.RawHex.Length);
        }

        var sb = new StringBuilder();
        sb.Append("Address".PadRight(wa)).Append("  ").Append("Name".PadRight(wn)).Append("  ")
            .Append("Raw".PadRight(wr)).Append("  Value").AppendLine();
        foreach (var r in rows)
            sb.Append(r.Address.PadRight(wa)).Append("  ").Append(r.Name.PadRight(wn)).Append("  ")
                .Append(r.RawHex.PadRight(wr)).Append("  ").Append(r.Value).AppendLine();
        return sb.ToString();
    }

    /// <summary>The protocol's name of a <c>State</c> value.</summary>
    public static string StateName(ushort state) => state switch
    {
        0 => "Disabled",
        1 => "Standstill",
        2 => "Homing",
        3 => "DiscreteMotion",
        4 => "ContinuousMotion",
        5 => "5 (reserved)",
        6 => "Stopping",
        7 => "ErrorStop",
        _ => $"{state} (undefined)",
    };

    /// <summary>The protocol's name of a <c>FaultCode</c> value.</summary>
    public static string FaultName(ushort code) => code switch
    {
        0 => "None",
        1 => "DriveFault",
        2 => "LimitSwitch",
        3 => "FollowingError",
        4 => "Watchdog",
        5 => "HomingFailed",
        6 => "DriveLinkLost",
        7 => "SafetyStop",
        >= (ushort)PlcFaultCode.VendorFirst => $"{code} (vendor)",
        _ => $"{code} (undefined)",
    };

    private static string Bits<T>(T value) where T : struct, Enum =>
        Convert.ToUInt16(value, Inv) == 0 ? "none" : value.ToString().Replace(", ", " | ", StringComparison.Ordinal);

    private static string Num(ushort value) => value.ToString(Inv);

    private static int Base(RegisterMap map, RegisterSpace space) =>
        space == RegisterSpace.Holding ? map.CommandBase : map.StatusBase;

    private static RegisterRow Word(RegisterMap map, RegisterSpace space, int offset, string name, ushort raw,
        string value)
    {
        var address = Base(map, space) + offset;
        return new(map.DescribeRange(space, (ushort)address, 1), name, $"0x{raw:X4}", value, space, address);
    }

    private static RegisterRow Pair(RegisterMap map, RegisterSpace space, int offset, string name, ushort low,
        ushort high)
    {
        var address = Base(map, space) + offset;
        return new(map.DescribeRange(space, (ushort)address, 2), name, $"0x{low:X4} 0x{high:X4}",
            Words.FromRaw(Words.Join(low, high)).ToString("0.000", Inv), space, address);
    }
}
