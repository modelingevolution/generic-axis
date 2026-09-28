using System.Globalization;
using System.Text;

namespace ModelingEvolution.GenericAxis;

/// <summary>One decoded field of a register dump.</summary>
/// <param name="Address">The protocol's address text: <c>C+0 = 0</c>, or <c>S+8…S+9 = 108…109</c> for an int32.</param>
/// <param name="Name">The protocol's register name.</param>
/// <param name="RawHex">The raw registers in hex, in wire order (an int32 low word first).</param>
/// <param name="Value">The decoded value: engineering units with three decimals, or names for <c>State</c>,
/// <c>FaultCode</c>, <c>Flags</c> and <c>Command</c>.</param>
public sealed record RegisterRow(string Address, string Name, string RawHex, string Value);

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

        var c = map.CommandBase;
        var s = map.StatusBase;
        return
        [
            Word("C", 0, c, "Command", command[0], Bits((CommandBits)command[0])),
            Word("C", 1, c, "CommandSeq", command[1], Num(command[1])),
            Pair("C", 2, c, "TargetPosition", command[2], command[3]),
            Pair("C", 4, c, "Velocity", command[4], command[5]),
            Pair("C", 6, c, "Acceleration", command[6], command[7]),
            Word("C", 8, c, "Heartbeat", command[8], Num(command[8])),
            Word("C", 9, c, "LeaseOwner", command[9], command[9] == 0 ? "0 (unowned)" : Num(command[9])),
            Word("C", 10, c, "WatchdogFault", command[10], command[10] switch
            {
                0 => "0 (healthy)",
                1 => "1 (tripped)",
                var v => $"{v} (undefined)",
            }),
            Word("C", 11, c, "WatchdogTrips", command[11], Num(command[11])),

            Word("S", 0, s, "State", status[0], StateName(status[0])),
            Word("S", 1, s, "Flags", status[1], Bits(block.Flags)),
            Pair("S", 2, s, "ActualPosition", status[2], status[3]),
            Pair("S", 4, s, "ActualVelocity", status[4], status[5]),
            Word("S", 6, s, "FaultCode", status[6], FaultName(status[6])),
            Word("S", 7, s, "CommandAck", status[7], Num(status[7])),
            Pair("S", 8, s, "TravelMin", status[8], status[9]),
            Pair("S", 10, s, "TravelMax", status[10], status[11]),
            Pair("S", 12, s, "MaxVelocity", status[12], status[13]),
            Word("S", 14, s, "MapVersion", status[14], Num(status[14])),
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

    private static RegisterRow Word(string block, int offset, int b, string name, ushort raw, string value) =>
        new($"{block}+{offset} = {b + offset}", name, $"0x{raw:X4}", value);

    private static RegisterRow Pair(string block, int offset, int b, string name, ushort low, ushort high) =>
        new($"{block}+{offset}…{block}+{offset + 1} = {b + offset}…{b + offset + 1}", name,
            $"0x{low:X4} 0x{high:X4}",
            Words.FromRaw(Words.Join(low, high)).ToString("0.000", Inv));
}
