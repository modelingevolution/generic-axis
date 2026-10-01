namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// Renders a <see cref="LastRead"/> as two tables with the driver's <see cref="RegisterDump"/> (protocol § Errors and debugging,
/// rule 4) — one decoder for the driver, <c>--dump</c> and the report. A register never read shows as <c>—</c>.
/// </summary>
public static class LastReadDump
{
    /// <summary>The registers each row of <see cref="RegisterDump.Decode"/> covers, in its order (C = command, S = status).</summary>
    private static readonly (bool Status, int Offset, int Count)[] Rows =
    [
        (false, 0, 1), (false, 1, 1), (false, 2, 2), (false, 4, 2), (false, 6, 2), (false, 8, 1), (false, 9, 1), (false, 10, 1), (false, 11, 1),
        (true, 0, 1), (true, 1, 1), (true, 2, 2), (true, 4, 2), (true, 6, 1), (true, 7, 1), (true, 8, 2), (true, 10, 2), (true, 12, 2), (true, 14, 1),
    ];

    public static string Render(LastRead read, RegisterMap map)
    {
        ushort[] Words(IReadOnlyList<int?> w) => [.. w.Select(v => (ushort)(v ?? 0))];
        var decoded = RegisterDump.Decode(map, Words(read.Command), Words(read.Status));
        var rows = decoded.Select((row, i) =>
        {
            var (status, offset, count) = Rows[i];
            var source = status ? read.Status : read.Command;
            var never = Enumerable.Range(offset, count).Any(o => source[o] is null);
            return never ? row with { RawHex = "—", Value = "never read" } : row;
        }).ToList();
        return Tables(rows);
    }

    /// <summary>The dump as two tables (rule 4, ADR-36): the command block in holding registers, then the status block in
    /// input registers. Rows are split by the decoder's <see cref="RegisterRow.Space"/>.</summary>
    public static string Tables(IReadOnlyList<RegisterRow> rows) =>
        "Command block — holding registers (FC03)\n"
        + RegisterDump.Format([.. rows.Where(r => r.Space == RegisterSpace.Holding)])
        + "\nStatus block — input registers (FC04)\n"
        + RegisterDump.Format([.. rows.Where(r => r.Space == RegisterSpace.Input)]);
}
