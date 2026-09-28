using System.Text.RegularExpressions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-60.cs — the catalog is the protocol's table: same ids, titles, sections and needs, in order.</summary>
public sealed partial class ProtocolCatalogTests
{
    internal sealed record Row(string Id, string Title, string Section, IReadOnlyList<string> Needs, bool Motion);

    [GeneratedRegex(@"^\|\s*(CHK-\d{2})\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|\s*([^|]+?)\s*\|")]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"\b(\d{2})\b")]
    private static partial Regex NeedPattern();

    internal static IReadOnlyList<Row> ProtocolRows()
    {
        var rows = new List<Row>();
        foreach (var line in File.ReadLines(ProtocolPath()))
        {
            var m = RowPattern().Match(line);
            if (!m.Success) continue;
            var needs = m.Groups[4].Value;
            rows.Add(new Row(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value,
                [.. NeedPattern().Matches(needs).Select(n => $"CHK-{n.Groups[1].Value}")],
                needs.Contains("--allow-motion", StringComparison.Ordinal)));
        }

        return rows;
    }

    private static string ProtocolPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "protocol.md");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("docs/protocol.md not found above the test assembly");
    }

    [Fact]
    public void GA_U_60_TheCatalogMatchesTheProtocolTable()
    {
        var rows = ProtocolRows();
        rows.Should().HaveCount(16, "protocol.md lists CHK-01…CHK-16");

        var catalog = CheckCatalog.All.Select(d => new Row(d.Id, d.Title, d.Section, d.Needs, false)).ToList();
        catalog.Select(r => r.Id).Should().Equal(rows.Select(r => r.Id));
        catalog.Select(r => r.Title).Should().Equal(rows.Select(r => r.Title));
        catalog.Select(r => r.Section).Should().Equal(rows.Select(r => r.Section));
        foreach (var (row, def) in rows.Zip(CheckCatalog.All))
        {
            def.Needs.Should().Equal(row.Needs, $"{row.Id}'s needs are '{string.Join(", ", row.Needs)}' in protocol.md");
            if (row.Motion) def.RequiresMotion.Should().BeTrue($"{row.Id} needs --allow-motion in protocol.md");
        }

        // "--allow-motion runs CHK-12…CHK-16": exactly those need motion.
        CheckCatalog.All.Where(d => d.RequiresMotion).Select(d => d.Id)
            .Should().Equal("CHK-12", "CHK-13", "CHK-14", "CHK-15", "CHK-16");
    }
}
