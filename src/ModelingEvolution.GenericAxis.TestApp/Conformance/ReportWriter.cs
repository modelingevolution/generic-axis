using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>JSON and Markdown per protocol § Report schema.</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string ToJson(ConformanceReport report)
    {
        var o = report.Options;
        var checks = new JsonArray();
        foreach (var c in report.Checks)
        {
            var observed = new JsonObject();
            foreach (var (key, value) in c.Observed) observed[key] = value;
            checks.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["title"] = c.Title,
                ["section"] = c.Section,
                ["result"] = Result(c.Result),
                ["durationMs"] = c.DurationMs,
                ["message"] = c.Message,
                ["observed"] = observed,
            });
        }

        var root = new JsonObject
        {
            ["schema"] = ConformanceReport.Schema,
            ["mapVersion"] = RegisterMap.Version,
            ["tool"] = new JsonObject
            {
                ["name"] = ConformanceReport.ToolName,
                ["language"] = "csharp",
                ["version"] = report.ToolVersion,
            },
            ["target"] = new JsonObject
            {
                ["host"] = o.Host,
                ["port"] = o.Port,
                ["unit"] = o.Unit,
                ["commandBase"] = o.CommandBase,
                ["statusBase"] = o.StatusBase,
            },
            ["allowMotion"] = o.AllowMotion,
            ["startedAt"] = Utc(report.StartedAt),
            ["finishedAt"] = Utc(report.FinishedAt ?? report.StartedAt),
            ["summary"] = new JsonObject
            {
                ["result"] = report.Passed ? "PASS" : "FAIL",
                ["pass"] = report.PassCount,
                ["fail"] = report.FailCount,
                ["skipped"] = report.SkippedCount,
            },
            ["checks"] = checks,
            ["cleanup"] = new JsonArray([.. report.Cleanup.Select(s => (JsonNode?)JsonValue.Create(s))]),
        };
        return root.ToJsonString(Indented);
    }

    public static string ToMarkdown(ConformanceReport report)
    {
        var o = report.Options;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"# PLC conformance — {ConformanceReport.ToolName} (csharp {report.ToolVersion}) against {o.Host}:{o.Port} unit {o.Unit}, ");
        sb.Append(CultureInfo.InvariantCulture,
            $"bases C={o.CommandBase} S={o.StatusBase}, motion {(o.AllowMotion ? "allowed" : "not allowed")}, {Utc(report.StartedAt)}\n\n");
        if (report.Refused)
            sb.Append("Refused to start: the axis is held by a live foreign lease (another commander such as rw2 is attached). Stop it first.\n\n");

        sb.Append("| Id | Title | Result | Observed | Protocol section |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var c in report.Checks)
        {
            var observed = string.Join(", ", c.Observed.Select(kv => $"{kv.Key}={kv.Value.ToString(CultureInfo.InvariantCulture)}"));
            var cell = observed.Length == 0 ? c.Message : c.Message.Length == 0 ? observed : $"{c.Message} ({observed})";
            sb.Append(CultureInfo.InvariantCulture,
                $"| {c.Id} | {Escape(c.Title)} | {Result(c.Result)} | {Escape(cell)} | {Escape(c.Section)} |\n");
        }

        sb.Append("\nCleanup:\n");
        if (report.Cleanup.IsEmpty) sb.Append("- nothing to undo\n");
        foreach (var line in report.Cleanup) sb.Append("- ").Append(line).Append('\n');

        sb.Append(CultureInfo.InvariantCulture, $"\nRESULT: {(report.Passed ? "PASS" : "FAIL")}\n");
        return sb.ToString();
    }

    public static string Result(CheckResultKind kind) => kind switch
    {
        CheckResultKind.Pass => "PASS",
        CheckResultKind.Fail => "FAIL",
        _ => "SKIPPED",
    };

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
