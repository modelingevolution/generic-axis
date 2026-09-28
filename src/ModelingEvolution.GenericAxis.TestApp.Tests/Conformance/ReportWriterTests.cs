using System.Collections.Immutable;
using System.Text.Json;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-62.cs — the report follows protocol.md § Report schema.</summary>
public sealed class ReportWriterTests
{
    private static ConformanceReport Report() => new()
    {
        Options = new CheckerOptions { Host = "192.168.58.20", Port = 502 },
        ToolVersion = "1.0.0",
        StartedAt = new DateTimeOffset(2026, 9, 29, 10, 15, 2, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 9, 29, 10, 15, 31, TimeSpan.Zero),
        Checks =
        [
            new("CHK-01", "Transport and unit", "Transport", CheckResultKind.Pass, 7, "connected", [KeyValuePair.Create("connectMs", 3L)]),
            new("CHK-02", "Map version", "Status block", CheckResultKind.Fail, 2, "MapVersion 2, expected 1", [KeyValuePair.Create("mapVersion", 2L)]),
            new("CHK-12", "Home", "Command semantics: Home", CheckResultKind.Skipped, 0, "needs --allow-motion", []),
        ],
        Cleanup = ["C+0 = 0x0000 (Enable 0)", "C+9 = 0 (release lease)"],
    };

    [Fact]
    public void GA_U_62_TheJsonHasExactlyTheSchemasFields()
    {
        using var doc = JsonDocument.Parse(ReportWriter.ToJson(Report()));
        var root = doc.RootElement;

        Names(root).Should().Equal("schema", "mapVersion", "tool", "target", "allowMotion", "startedAt", "finishedAt", "summary", "checks", "cleanup");
        root.GetProperty("schema").GetString().Should().Be("generic-axis-conformance/1");
        root.GetProperty("mapVersion").GetInt32().Should().Be(1);
        Names(root.GetProperty("tool")).Should().Equal("name", "language", "version");
        root.GetProperty("tool").GetProperty("name").GetString().Should().Be("generic-axis-check");
        root.GetProperty("tool").GetProperty("language").GetString().Should().Be("csharp");
        Names(root.GetProperty("target")).Should().Equal("host", "port", "unit", "commandBase", "statusBase");
        root.GetProperty("startedAt").GetString().Should().Be("2026-09-29T10:15:02Z");
        root.GetProperty("finishedAt").GetString().Should().Be("2026-09-29T10:15:31Z");

        var summary = root.GetProperty("summary");
        Names(summary).Should().Equal("result", "pass", "fail", "skipped");
        summary.GetProperty("result").GetString().Should().Be("FAIL");
        summary.GetProperty("pass").GetInt32().Should().Be(1);
        summary.GetProperty("fail").GetInt32().Should().Be(1);
        summary.GetProperty("skipped").GetInt32().Should().Be(1);

        var checks = root.GetProperty("checks").EnumerateArray().ToList();
        checks.Should().HaveCount(3);
        foreach (var c in checks) Names(c).Should().Equal("id", "title", "section", "result", "durationMs", "message", "observed");
        checks.Select(c => c.GetProperty("result").GetString()).Should().Equal("PASS", "FAIL", "SKIPPED");
        checks[1].GetProperty("observed").GetProperty("mapVersion").GetInt64().Should().Be(2);
        checks[2].GetProperty("observed").EnumerateObject().Should().BeEmpty();
        root.GetProperty("cleanup").EnumerateArray().Select(e => e.GetString()).Should().Equal("C+0 = 0x0000 (Enable 0)", "C+9 = 0 (release lease)");
    }

    [Fact]
    public void GA_U_62_TheMarkdownHasTheTableAndEndsWithTheResult()
    {
        var md = ReportWriter.ToMarkdown(Report());
        var lines = md.TrimEnd('\n').Split('\n');

        lines[0].Should().StartWith("# ").And.Contain("generic-axis-check").And.Contain("192.168.58.20:502").And.Contain("2026-09-29T10:15:02Z");
        lines.Should().Contain("| Id | Title | Result | Observed | Protocol section |");
        lines.Should().Contain(l => l.StartsWith("| CHK-02 | Map version | FAIL | MapVersion 2, expected 1 (mapVersion=2) | Status block |"));
        lines.Should().Contain("- C+9 = 0 (release lease)");
        lines[^1].Should().Be("RESULT: FAIL");
    }

    [Fact]
    public void AllPassOrSkippedIsAPass()
    {
        var report = Report() with { Checks = Report().Checks.RemoveAt(1) };
        report.ExitCode.Should().Be(0);
        ReportWriter.ToMarkdown(report).TrimEnd().Should().EndWith("RESULT: PASS");
        (report with { Refused = true }).ExitCode.Should().Be(3);
    }

    private static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);
}
