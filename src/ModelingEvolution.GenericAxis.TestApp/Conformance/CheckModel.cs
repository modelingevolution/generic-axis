using System.Collections.Immutable;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>PASS, FAIL or SKIPPED (protocol § Report schema).</summary>
public enum CheckResultKind
{
    Pass,
    Fail,
    Skipped,
}

/// <summary>
/// One thing a check saw go wrong, with its class, its SDK <c>MotionError</c> name and its precedence from protocol
/// § "Error class of a FAIL" (1 = decided first).
/// </summary>
public sealed record Failure(ErrorClass? Class, string Name, int Rank, string Text)
{
    /// <summary>
    /// Rank 0: the checker itself failed (protocol § Report schema). No class: it is not a verdict on the PLC. Rendered
    /// <c>checker error: &lt;ExceptionType&gt;: &lt;text&gt;</c>.
    /// </summary>
    public static Failure CheckerError(Exception ex) => new(null, "checker error", 0, $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>The message form: <c>&lt;Class&gt;/&lt;MotionError&gt;: &lt;text&gt;</c>, or <c>checker error: …</c>.</summary>
    public string Render() => Class is { } cls ? $"{cls}/{Name}: {Text}" : $"checker error: {Text}";

    /// <summary>Rank 1: no answer, connect failed, socket closed, Modbus exception (after the one retry).</summary>
    public static Failure Transport(string text) => new(ErrorClass.Transport, "CommunicationLost", 1, text);

    /// <summary>Rank 2: a command was written and <c>CommandAck</c> did not echo <c>CommandSeq</c> within 500 ms.</summary>
    public static Failure NotAcknowledged(string what, ushort seq, ushort ack, ushort state) =>
        new(ErrorClass.Protocol, "NotAcknowledged", 2,
            $"{what} not accepted. CommandSeq {seq} written, CommandAck {ack} read after 500 ms, State {state} read.");

    /// <summary>Rank 3: <c>State</c> is 5 or above 7, or 7 with <c>FaultCode = 0</c>.</summary>
    public static Failure InvalidState(ushort state, ushort faultCode, string where) => state == 7
        ? new(ErrorClass.Protocol, "ProtocolMismatch", 3, $"ErrorStop without a fault. Read State ({where}) = 7, FaultCode = 0, expected a FaultCode.")
        : new(ErrorClass.Protocol, "ProtocolMismatch", 3, $"State {state} is not a protocol state. Read State ({where}) = {state}, expected 0, 1, 2, 3, 4, 6 or 7.");

    /// <summary>Rank 4: the PLC reports a fault the check did not expect; the name follows the FaultCode map.</summary>
    public static Failure Fault(ushort faultCode, string where) =>
        new(ErrorClass.Machine, FaultName(faultCode), 4,
            $"the PLC reports ErrorStop. Read FaultCode ({where}) = {faultCode}{(faultCode >= 100 ? " (vendor code)" : "")}.");

    /// <summary>Rank 5: the PLC answered, but against the protocol.</summary>
    public static Failure Protocol(string text) => new(ErrorClass.Protocol, "ProtocolMismatch", 5, text);

    /// <summary>
    /// A driver exception under its own MotionError name and the protocol's class for it (review #33): never
    /// relabelled. Rank follows the class: Transport 1, NotAcknowledged 2, other Protocol 5, Machine 6, Commander 7.
    /// </summary>
    public static Failure FromMotion(RocketWelder.SDK.Devices.Motion.MotionException ex)
    {
        var cls = MotionErrorClasses.Of(ex.Error);
        var rank = cls switch
        {
            ErrorClass.Transport => 1,
            ErrorClass.Protocol => ex.Error == RocketWelder.SDK.Devices.Motion.MotionError.NotAcknowledged ? 2 : 5,
            ErrorClass.Machine => 6,
            _ => 7,
        };
        return new(cls, ex.Error.ToString(), rank, CheckerText.Facts(ex));
    }

    /// <summary>Rank 6: an accepted command whose effect never came.</summary>
    public static Failure Machine(string name, string text) => new(ErrorClass.Machine, name, 6, text);

    /// <summary>FaultCode → SDK MotionError name (protocol § Errors and debugging, Machine row).</summary>
    public static string FaultName(ushort faultCode) => faultCode switch
    {
        2 => "LimitTripped",
        3 => "MotionFailed",
        4 => "WatchdogTripped",
        5 => "HomeLatchFailed",
        7 => "SafetyStop",
        _ => "DriveFault", // 1, 6 (drive link), ≥ 100 (vendor)
    };
}

/// <summary>Raw C+0…C+11 and S+0…S+14; <c>null</c> for a register never read.</summary>
public sealed record LastRead(ImmutableArray<int?> Command, ImmutableArray<int?> Status);

/// <summary>What one check returns.</summary>
public sealed record CheckOutcome(
    CheckResultKind Result,
    string Message,
    ImmutableArray<KeyValuePair<string, long?>> Observed,
    ImmutableArray<Failure> Failures)
{
    public static CheckOutcome Pass(string message, params (string Key, long? Value)[] observed) =>
        new(CheckResultKind.Pass, message, ToObserved(observed), []);

    public static CheckOutcome Skipped(string message) => new(CheckResultKind.Skipped, message, [], []);

    public static CheckOutcome Fail(Failure failure, params (string Key, long? Value)[] observed) =>
        Judge([failure], "", observed);

    /// <summary>PASS when <paramref name="failures"/> is empty, else FAIL.</summary>
    public static CheckOutcome Judge(IEnumerable<Failure> failures, string passMessage, params (string Key, long? Value)[] observed)
    {
        var list = failures.ToImmutableArray();
        return list.IsEmpty
            ? Pass(passMessage, observed)
            : new CheckOutcome(CheckResultKind.Fail, "", ToObserved(observed), list).WithMessage();
    }

    /// <summary>The class that decides: the lowest rank among the failures seen.</summary>
    public Failure? Deciding => Failures.IsEmpty ? null : Failures.MinBy(f => f.Rank);

    /// <summary>Adds failures seen outside the check (the runner's lastRead, a failed restore) and re-decides.</summary>
    public CheckOutcome With(IEnumerable<Failure> more)
    {
        // A failure already reported (same class, name and text) is not repeated.
        var added = more.Where(m => !Failures.Any(f => f.Class == m.Class && f.Name == m.Name && f.Text == m.Text)).ToImmutableArray();
        return added.IsEmpty ? this : (this with { Result = CheckResultKind.Fail, Failures = Failures.AddRange(added) }).WithMessage();
    }

    public CheckOutcome WithObserved(string key, long? value) => this with { Observed = Observed.Add(KeyValuePair.Create(key, value)) };

    /// <summary><c>&lt;Class&gt;/&lt;MotionError&gt;: &lt;what happened&gt;</c>, the deciding failure first.</summary>
    private CheckOutcome WithMessage()
    {
        var deciding = Deciding!;
        var rest = Failures.Where(f => !ReferenceEquals(f, deciding)).Select(f => $"also {f.Render()}");
        return this with { Message = string.Join(" ", new[] { deciding.Render() }.Concat(rest)) };
    }

    private static ImmutableArray<KeyValuePair<string, long?>> ToObserved((string Key, long? Value)[] observed) =>
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
    ImmutableArray<KeyValuePair<string, long?>> Observed,
    ErrorClass? ErrorClass = null,
    LastRead? LastRead = null);

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

    /// <summary>
    /// What pre-flight found when it matters to the reader: the refusal reason, or why the run proceeded past a held
    /// lease (its holder is dead, review #35). The Markdown states it on the line after the heading.
    /// </summary>
    public string? Preflight { get; init; }

    /// <summary>The run refused to start: a live foreign commander holds the axis (exit 3).</summary>
    public bool Refused { get; init; }

    /// <summary>The operator interrupted the run (exit 4). Not a FAIL.</summary>
    public bool Interrupted { get; init; }

    /// <summary>The check running now (for the page), null when idle or done.</summary>
    public string? Running { get; init; }

    public int PassCount => Checks.Count(c => c.Result == CheckResultKind.Pass);
    public int FailCount => Checks.Count(c => c.Result == CheckResultKind.Fail);
    public int SkippedCount => Checks.Count(c => c.Result == CheckResultKind.Skipped);
    public bool Passed => FailCount == 0;

    /// <summary><c>REFUSED</c> (exit 3), else <c>INTERRUPTED</c>, else <c>FAIL</c> if any check failed, else <c>PASS</c>.</summary>
    public string SummaryResult => Refused ? "REFUSED" : Interrupted ? "INTERRUPTED" : Passed ? "PASS" : "FAIL";

    /// <summary>0 = no FAIL · 1 = at least one FAIL · 3 = refused to start · 4 = interrupted.</summary>
    public int ExitCode => Refused ? ConformanceExitCodes.Refused
        : Interrupted ? ConformanceExitCodes.Interrupted
        : Passed ? ConformanceExitCodes.Pass : ConformanceExitCodes.Fail;
}

/// <summary>Exit codes of <c>--check</c> (protocol § Command line).</summary>
public static class ConformanceExitCodes
{
    public const int Pass = 0;
    public const int Fail = 1;
    public const int Usage = 2;
    public const int Refused = 3;
    public const int Interrupted = 4;
}
