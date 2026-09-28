using System.Collections.Immutable;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>PASS, FAIL or SKIPPED (protocol § Conformance checks, Report schema).</summary>
public enum CheckResultKind
{
    Pass,
    Fail,
    Skipped,
}

/// <summary>What one check returns: a result, a message and the observed values, in the order they were observed.</summary>
public sealed record CheckOutcome(CheckResultKind Result, string Message, ImmutableArray<KeyValuePair<string, long>> Observed)
{
    public static CheckOutcome Pass(string message, params (string Key, long Value)[] observed) =>
        new(CheckResultKind.Pass, message, ToObserved(observed));

    public static CheckOutcome Fail(string message, params (string Key, long Value)[] observed) =>
        new(CheckResultKind.Fail, message, ToObserved(observed));

    public static CheckOutcome Skipped(string message) => new(CheckResultKind.Skipped, message, []);

    /// <summary>PASS when <paramref name="failures"/> is empty, else FAIL naming every failure.</summary>
    public static CheckOutcome Judge(IReadOnlyCollection<string> failures, string passMessage, params (string Key, long Value)[] observed) =>
        failures.Count == 0 ? Pass(passMessage, observed) : Fail(string.Join("; ", failures), observed);

    private static ImmutableArray<KeyValuePair<string, long>> ToObserved((string Key, long Value)[] observed) =>
        [.. observed.Select(o => KeyValuePair.Create(o.Key, o.Value))];
}

/// <summary>One line of the report.</summary>
public sealed record CheckResult(
    string Id,
    string Title,
    string Section,
    CheckResultKind Result,
    long DurationMs,
    string Message,
    ImmutableArray<KeyValuePair<string, long>> Observed);

/// <summary>The whole run, as the report schema <c>generic-axis-conformance/1</c> describes it.</summary>
public sealed record ConformanceReport
{
    public const string Schema = "generic-axis-conformance/1";
    public const string ToolName = "generic-axis-check";

    public required CheckerOptions Options { get; init; }
    public required string ToolVersion { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public ImmutableArray<CheckResult> Checks { get; init; } = [];
    public ImmutableArray<string> Cleanup { get; init; } = [];

    /// <summary>The run refused to start: a live foreign commander holds the axis (exit 3).</summary>
    public bool Refused { get; init; }

    /// <summary>The check running now (for the page), null when idle or done.</summary>
    public string? Running { get; init; }

    public int PassCount => Checks.Count(c => c.Result == CheckResultKind.Pass);
    public int FailCount => Checks.Count(c => c.Result == CheckResultKind.Fail);
    public int SkippedCount => Checks.Count(c => c.Result == CheckResultKind.Skipped);
    public bool Passed => FailCount == 0;

    /// <summary>0 = no FAIL · 1 = at least one FAIL · 3 = refused to start.</summary>
    public int ExitCode => Refused ? ConformanceExitCodes.Refused : Passed ? ConformanceExitCodes.Pass : ConformanceExitCodes.Fail;
}

/// <summary>Exit codes of <c>--check</c> (protocol § Command line).</summary>
public static class ConformanceExitCodes
{
    public const int Pass = 0;
    public const int Fail = 1;
    public const int Usage = 2;
    public const int Refused = 3;
}
