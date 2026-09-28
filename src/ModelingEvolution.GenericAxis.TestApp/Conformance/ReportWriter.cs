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
                ["errorClass"] = c.ErrorClass?.ToString(),
                ["observed"] = observed,
            });
            if (c.Result == CheckResultKind.Fail && c.LastRead is { } read)
            {
                ((JsonObject)checks[^1]!)["lastRead"] = new JsonObject
                {
                    ["command"] = new JsonArray([.. read.Command.Select(w => (JsonNode?)(w is { } v ? JsonValue.Create(v) : null))]),
                    ["status"] = new JsonArray([.. read.Status.Select(w => (JsonNode?)(w is { } v ? JsonValue.Create(v) : null))]),
                };
            }
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
                ["result"] = report.SummaryResult,
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
            sb.Append("Refused to start: another commander is beating (rw2, a station, or a second tool). Stop it first.\n\n");

        sb.Append("| Id | Title | Result | Observed | Protocol section |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var c in report.Checks)
        {
            var observed = string.Join(", ", c.Observed.Select(kv => $"{kv.Key}={(kv.Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null")}"));
            var cell = observed.Length == 0 ? c.Message : c.Message.Length == 0 ? observed : $"{c.Message} ({observed})";
            sb.Append(CultureInfo.InvariantCulture,
                $"| {c.Id} | {Escape(c.Title)} | {Result(c.Result)} | {Escape(cell)} | {Escape(c.Section)} |\n");
        }

        var failures = report.Checks.Where(c => c.Result == CheckResultKind.Fail).ToList();
        sb.Append("\n## Failures\n\n");
        if (failures.Count == 0) sb.Append("None.\n");
        foreach (var f in failures)
        {
            sb.Append(CultureInfo.InvariantCulture, $"### {f.Id} {f.Title}\n\n{f.Id}: {f.Message}\n\n");
            if (f.LastRead is { } read) sb.Append("Last read of both blocks:\n\n").Append(RegisterDump.Render(read, o.Map)).Append('\n');
        }

        sb.Append("\nCleanup:\n");
        if (report.Cleanup.IsEmpty) sb.Append("- nothing to undo\n");
        foreach (var line in report.Cleanup) sb.Append("- ").Append(line).Append('\n');

        sb.Append(CultureInfo.InvariantCulture, $"\nRESULT: {report.SummaryResult}\n");
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
