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
            new("CHK-01", "Transport and unit", "Transport", CheckResultKind.Pass, 7, "connected", [KeyValuePair.Create("connectMs", (long?)3)]),
            new("CHK-02", "Map version", "Status block", CheckResultKind.Fail, 2,
                "Protocol/ProtocolMismatch: wrong map version. Read MapVersion (S+14 = input 14) = 2, expected 1.", [KeyValuePair.Create("mapVersion", (long?)2), KeyValuePair.Create("retries", (long?)0)],
                ErrorClass.Protocol,
                new LastRead([1, 7, null, null, 0, 0, 0, 0, 12, 65535, 0, 2], [0, 32, 0, 0, 0, 0, 0, 6, 0, 0, 38528, 152, 41248, 7, 2])),
            new("CHK-12", "Home", "Command semantics: Home", CheckResultKind.Skipped, 0, "needs --allow-motion", []),
        ],
        Cleanup = ["C+0 = 0x0000 (Enable 0)", "C+9 = 0 (release lease)"],
    };

    [Fact]
    public void GA_U_62_TheJsonHasExactlyTheSchemasFields()
    {
        using var doc = JsonDocument.Parse(ReportWriter.ToJson(Report()));
        var root = doc.RootElement;

        Names(root).Should().Equal("schema", "mapVersion", "tool", "target", "allowMotion", "startedAt", "finishedAt", "preflight", "summary", "checks", "cleanup");
        root.GetProperty("preflight").ValueKind.Should().Be(JsonValueKind.Null, "null unless pre-flight had something to say");
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
        Names(checks[0]).Should().Equal("id", "title", "section", "result", "durationMs", "message", "errorClass", "observed");
        Names(checks[1]).Should().Equal("id", "title", "section", "result", "durationMs", "message", "errorClass", "observed", "lastRead");
        Names(checks[2]).Should().Equal("id", "title", "section", "result", "durationMs", "message", "errorClass", "observed");
        checks[0].GetProperty("errorClass").ValueKind.Should().Be(JsonValueKind.Null);
        checks[1].GetProperty("errorClass").GetString().Should().Be("Protocol");
        var lastRead = checks[1].GetProperty("lastRead");
        Names(lastRead).Should().Equal("command", "status");
        lastRead.GetProperty("command").GetArrayLength().Should().Be(12);
        lastRead.GetProperty("status").GetArrayLength().Should().Be(15);
        lastRead.GetProperty("command")[2].ValueKind.Should().Be(JsonValueKind.Null, "a register never read is null");
        lastRead.GetProperty("status")[14].GetInt32().Should().Be(2);
        checks.Select(c => c.GetProperty("result").GetString()).Should().Equal("PASS", "FAIL", "SKIPPED");
        checks[1].GetProperty("observed").GetProperty("mapVersion").GetInt64().Should().Be(2);
        checks[1].GetProperty("observed").GetProperty("retries").GetInt64().Should().Be(0);
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
        lines.Should().Contain(l => l.StartsWith("| CHK-02 | Map version | FAIL | Protocol/ProtocolMismatch: wrong map version."));
        var failures = Array.IndexOf(lines, "## Failures");
        var cleanup = Array.IndexOf(lines, "Cleanup:");
        failures.Should().BeGreaterThan(Array.IndexOf(lines, "| Id | Title | Result | Observed | Protocol section |"), "the Failures part follows the table");
        cleanup.Should().BeGreaterThan(failures, "the cleanup list comes last");
        var part = lines[failures..cleanup];
        part.Should().Contain("CHK-02: Protocol/ProtocolMismatch: wrong map version. Read MapVersion (S+14 = input 14) = 2, expected 1.");
        // The driver's RegisterDump renders the dump; a register never read shows as "—".
        // Two tables (ADR-36): the command block from holding, then the status block from input.
        Array.IndexOf(part, "Command block — holding registers (FC03)").Should().BeGreaterThan(-1).And.BeLessThan(Array.IndexOf(part, "Status block — input registers (FC04)"));
        part.Should().Contain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^S\+14 = input 14\s+MapVersion\s+0x0002\s+2$"));
        part.Should().Contain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^S\+0 = input 0\s+State\s+0x0000\s+Disabled"));
        part.Should().Contain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^C\+0 = holding 0\s+Command\s+0x0001\s+.*Enable"));
        part.Should().Contain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^C\+2…C\+3 = holding 2…3\s+TargetPosition\s+—\s+never read$"));
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

    [Fact]
    public void AnInterruptedRunIsInterruptedNotFail()
    {
        var report = Report() with { Interrupted = true };

        report.ExitCode.Should().Be(4);
        report.SummaryResult.Should().Be("INTERRUPTED");
        using var doc = JsonDocument.Parse(ReportWriter.ToJson(report));
        doc.RootElement.GetProperty("summary").GetProperty("result").GetString().Should().Be("INTERRUPTED");
        ReportWriter.ToMarkdown(report).TrimEnd().Should().EndWith("RESULT: INTERRUPTED");
    }

    /// <summary>GA-U-93 (python review #22 mirror): a refused run is REFUSED, never PASS.</summary>
    [Fact]
    public void GA_U_93_ARefusedRunIsRefusedNotPass()
    {
        var all = Report().Checks.Select(c => c with { Result = CheckResultKind.Skipped, Message = "refused to start: …" });
        var report = Report() with { Refused = true, Checks = [.. all] };

        report.ExitCode.Should().Be(3);
        report.SummaryResult.Should().Be("REFUSED");
        using var doc = JsonDocument.Parse(ReportWriter.ToJson(report));
        doc.RootElement.GetProperty("summary").GetProperty("result").GetString().Should().Be("REFUSED");
        ReportWriter.ToMarkdown(report).TrimEnd().Should().EndWith("RESULT: REFUSED");
    }

    private static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);
}
