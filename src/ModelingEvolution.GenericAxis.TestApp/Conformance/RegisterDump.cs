using System.Globalization;
using System.Text;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// The decoded register dump of protocol § Errors and debugging, rule 4: one row per register (a 32-bit pair is one
/// row), with address, name, raw hex and the value in engineering units; <c>State</c> and <c>FaultCode</c> by name,
/// <c>Flags</c> and <c>Command</c> bits by name. Decoding goes through the driver's <see cref="Words"/>.
/// </summary>
public static class RegisterDump
{
    private static readonly string[] StateNames =
        ["Disabled", "Standstill", "Homing", "DiscreteMotion", "ContinuousMotion", "(reserved)", "Stopping", "ErrorStop"];

    private static readonly string[] FaultNames =
        ["none", "drive fault", "limit switch tripped", "following error", "watchdog", "homing failed", "communication to drive lost", "safety stop"];

    /// <summary>A Markdown table of both blocks; <c>—</c> for a register never read.</summary>
    public static string Render(LastRead read, RegisterMap map)
    {
        var sb = new StringBuilder();
        sb.Append("| Address | Name | Raw | Decoded |\n|---|---|---|---|\n");
        var c = read.Command;
        var s = read.Status;
        Row(sb, map.CommandBase, 0, "C", "Command", c, v => Bits<CommandBits>(v));
        Row(sb, map.CommandBase, 1, "C", "CommandSeq", c, v => v.ToString(CultureInfo.InvariantCulture));
        Pair(sb, map.CommandBase, 2, "C", "TargetPosition", c, "unit");
        Pair(sb, map.CommandBase, 4, "C", "Velocity", c, "unit/s");
        Pair(sb, map.CommandBase, 6, "C", "Acceleration", c, "unit/s²");
        Row(sb, map.CommandBase, 8, "C", "Heartbeat", c, v => v.ToString(CultureInfo.InvariantCulture));
        Row(sb, map.CommandBase, 9, "C", "LeaseOwner", c, v => v == 0 ? "0 (unowned)" : v.ToString(CultureInfo.InvariantCulture));
        Row(sb, map.CommandBase, 10, "C", "WatchdogFault", c, v => v == 0 ? "0 (healthy)" : $"{v} (tripped)");
        Row(sb, map.CommandBase, 11, "C", "WatchdogTrips", c, v => v.ToString(CultureInfo.InvariantCulture));
        Row(sb, map.StatusBase, 0, "S", "State", s, v => $"{v} {(v < StateNames.Length ? StateNames[v] : "(not a protocol state)")}");
        Row(sb, map.StatusBase, 1, "S", "Flags", s, v => Bits<StatusFlags>(v));
        Pair(sb, map.StatusBase, 2, "S", "ActualPosition", s, "unit");
        Pair(sb, map.StatusBase, 4, "S", "ActualVelocity", s, "unit/s");
        Row(sb, map.StatusBase, 6, "S", "FaultCode", s, v => $"{v} {(v < FaultNames.Length ? FaultNames[v] : v >= 100 ? "vendor-specific" : "(undefined)")}");
        Row(sb, map.StatusBase, 7, "S", "CommandAck", s, v => v.ToString(CultureInfo.InvariantCulture));
        Pair(sb, map.StatusBase, 8, "S", "TravelMin", s, "unit");
        Pair(sb, map.StatusBase, 10, "S", "TravelMax", s, "unit");
        Pair(sb, map.StatusBase, 12, "S", "MaxVelocity", s, "unit/s");
        Row(sb, map.StatusBase, 14, "S", "MapVersion", s, v => v.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, int baseAddress, int offset, string block, string name, IReadOnlyList<int?> words, Func<ushort, string> decode)
    {
        var w = words[offset];
        sb.Append(CultureInfo.InvariantCulture,
            $"| {block}+{offset} ({baseAddress + offset}) | {name} | {(w is { } r ? $"0x{r:X4}" : "—")} | {(w is { } v ? decode((ushort)v) : "never read")} |\n");
    }

    private static void Pair(StringBuilder sb, int baseAddress, int offset, string block, string name, IReadOnlyList<int?> words, string unit)
    {
        var (lo, hi) = (words[offset], words[offset + 1]);
        var raw = lo is { } l && hi is { } h ? $"0x{l:X4} 0x{h:X4}" : "—";
        var decoded = lo is { } a && hi is { } b
            ? $"{Words.Join((ushort)a, (ushort)b)} = {Words.FromRaw(Words.Join((ushort)a, (ushort)b)).ToString("0.000", CultureInfo.InvariantCulture)} {unit}"
            : "never read";
        sb.Append(CultureInfo.InvariantCulture, $"| {block}+{offset}…{block}+{offset + 1} ({baseAddress + offset}…{baseAddress + offset + 1}) | {name} | {raw} | {decoded} |\n");
    }

    private static string Bits<T>(ushort value) where T : struct, Enum
    {
        var names = Enum.GetValues<T>().Select(f => (Name: f.ToString(), Bit: Convert.ToUInt16(f)))
            .Where(f => f.Bit != 0 && (value & f.Bit) == f.Bit).Select(f => f.Name).ToList();
        var known = Enum.GetValues<T>().Aggregate(0, (acc, f) => acc | Convert.ToUInt16(f));
        var unknown = value & ~known;
        if (unknown != 0) names.Add($"unknown 0x{unknown:X4}");
        return names.Count == 0 ? "none" : string.Join(" | ", names);
    }
}
