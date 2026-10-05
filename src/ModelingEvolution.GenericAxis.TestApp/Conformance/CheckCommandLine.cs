using System.Globalization;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>Report files: <c>*.md</c> writes Markdown plus a sibling <c>*.json</c>; <c>*.json</c> writes JSON only.</summary>
public sealed record ReportTarget(string? MarkdownPath, string JsonPath);

/// <summary>The checker's arguments, identical in both tools (protocol § Command line).</summary>
public sealed record CheckerOptions
{
    public const ushort DefaultOwnerId = 65535;
    public const ushort ForeignOwnerId = 65534;

    public required string Host { get; init; }
    public int Port { get; init; } = 502;
    public byte Unit { get; init; } = 1;
    public int CommandBase { get; init; } = RegisterMap.DefaultCommandBase;
    public int StatusBase { get; init; } = RegisterMap.DefaultStatusBase;
    public ushort OwnerId { get; init; } = DefaultOwnerId;
    public bool AllowMotion { get; init; }

    /// <summary>CHK-13 position threshold in axis units — a checker threshold, not a machine number.</summary>
    public double Tolerance { get; init; } = 0.1;

    public ReportTarget? Report { get; init; }

    /// <summary><c>--dump</c>: print the decoded register dump and run no checks (rule 4). Writes nothing.</summary>
    public bool Dump { get; init; }

    /// <summary><c>--watch</c> (with <c>--dump</c>): repeat the dump at 5 Hz until Ctrl-C.</summary>
    public bool Watch { get; init; }

    /// <summary><c>--command &lt;verb&gt; [args]</c>: one-verb mode instead of the checks (protocol § One-verb mode).</summary>
    public VerbRequest? Command { get; init; }

    public RegisterMap Map => new(CommandBase, StatusBase);
}

/// <summary>The verbs of one-verb mode (protocol § Command line, <c>--command</c>).</summary>
public enum Verb
{
    Enable,
    Disable,
    Home,
    Stop,
    Reset,
    Move,
    Jog,
}

/// <summary>
/// One verb with its arguments, in axis units: <c>move &lt;target&gt; [--speed &lt;pct&gt;]</c> (pct of <c>MaxVelocity</c>,
/// default 10) and <c>jog &lt;signed velocity&gt; [--for S]</c>. <paramref name="Speed"/> is the <c>--speed</c> argument as
/// typed (#68): it is what every message prints; <see cref="SpeedPercent"/> is its value, parsed once, for arithmetic only.
/// </summary>
public sealed record VerbRequest(Verb Verb, double? Target = null, string Speed = VerbRequest.DefaultSpeed,
    double? Velocity = null, TimeSpan? For = null)
{
    public const string DefaultSpeed = "10";

    /// <summary>The <c>--speed</c> percentage as a number — arithmetic only, never printed (print <see cref="Speed"/>).</summary>
    public double SpeedPercent => double.Parse(Speed, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary><c>home</c>, <c>move</c> and <c>jog</c> need <c>--allow-motion</c>.</summary>
    public bool Moves => Verb is Verb.Home or Verb.Move or Verb.Jog;

    /// <summary>The verb's name as typed: <c>move</c>.</summary>
    public string Name => Verb.ToString().ToLowerInvariant();
}

/// <summary>
/// Parses <c>--check &lt;host&gt;[:port] [--unit N] [--command-base N] [--status-base N] [--owner-id N]
/// [--allow-motion] [--tolerance X] [--report PATH]</c>. The leading <c>--check</c> is optional here.
/// </summary>
public static class CheckCommandLine
{
    public const string Flag = "--check";

    /// <summary>One-verb mode; routes to <see cref="CommandRunner"/> with or without <c>--check</c>.</summary>
    public const string CommandFlag = "--command";

    public const string Usage =
        "Usage: --check <host>[:port] [--unit N] [--command-base N (holding, default 0)] [--status-base N (input, default 0)] [--owner-id N] "
        + "[--allow-motion] [--tolerance X] [--report FILE.md|FILE.json] | --check <host>[:port] --dump [--watch] "
        + "| --command enable|disable|home|stop|reset|move <target> [--speed <pct>]|jog <signed velocity> [--for S] <host>[:port] "
        + "[--unit N] [--command-base N] [--status-base N] [--owner-id N] [--allow-motion]";

    /// <summary>Returns the options, or an error message for exit code 2.</summary>
    public static (CheckerOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        string? target = null;
        var unit = 1;
        var commandBase = RegisterMap.DefaultCommandBase;
        var statusBase = RegisterMap.DefaultStatusBase;
        var owner = (int)CheckerOptions.DefaultOwnerId;
        var allowMotion = false;
        var tolerance = 0.1;
        ReportTarget? report = null;
        var dump = false;
        var watch = false;
        Verb? verb = null;
        double? verbNumber = null;
        string? speed = null;
        double? forSeconds = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new FormatException($"{arg} needs a value");

            try
            {
                switch (arg)
                {
                    case Flag:
                        break;
                    case "--unit":
                        unit = Int(arg, Value(), 0, 255);
                        break;
                    case "--command-base":
                        commandBase = Int(arg, Value(), 0, 65535);
                        break;
                    case "--status-base":
                        statusBase = Int(arg, Value(), 0, 65535);
                        break;
                    case "--owner-id":
                        owner = Int(arg, Value(), 1, 65535);
                        break;
                    case "--allow-motion":
                        allowMotion = true;
                        break;
                    case "--tolerance":
                        var t = Value();
                        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance) || !(tolerance > 0))
                            throw new FormatException($"--tolerance must be a positive number, got '{t}'");
                        break;
                    case "--dump":
                        dump = true;
                        break;
                    case "--watch":
                        watch = true;
                        break;
                    case "--report":
                        report = Report(Value());
                        break;
                    case CommandFlag:
                        if (verb is not null) throw new FormatException("one --command only");
                        var name = Value();
                        verb = Enum.GetValues<Verb>().FirstOrDefault(v => v.ToString().ToLowerInvariant() == name) is var v
                               && v.ToString().ToLowerInvariant() == name
                            ? v
                            : throw new FormatException($"--command '{name}' is not a verb (enable, disable, home, stop, reset, move, jog)");
                        if (verb is Verb.Move or Verb.Jog)
                        {
                            var n = i + 1 < args.Count ? args[++i] : throw new FormatException($"--command {name} needs a number");
                            verbNumber = double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x)
                                ? x
                                : throw new FormatException($"--command {name} needs a number, got '{n}'");
                        }

                        break;
                    case "--speed":
                        var sp = Value();
                        // A number is the parser's business; its range (0 < pct ≤ 100) is a guard (protocol step 3, #39).
                        speed = double.TryParse(sp, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && double.IsFinite(pct)
                            ? sp // kept as typed (#68)
                            : throw new FormatException($"--speed needs a number, got '{sp}'");
                        break;
                    case "--for":
                        var fs = Value();
                        forSeconds = double.TryParse(fs, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0 && double.IsFinite(sec)
                            ? sec
                            : throw new FormatException($"--for must be a positive number of seconds, got '{fs}'");
                        break;
                    default:
                        if (arg.StartsWith('-')) throw new FormatException($"unknown argument '{arg}'");
                        if (target is not null) throw new FormatException($"one target only; got '{target}' and '{arg}'");
                        target = arg;
                        break;
                }
            }
            catch (FormatException ex)
            {
                return (null, ex.Message);
            }
        }

        if (target is null) return (null, "missing <host>[:port]");

        var host = target;
        var port = 502;
        var colon = target.LastIndexOf(':');
        if (colon >= 0)
        {
            host = target[..colon];
            if (!int.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
                return (null, $"invalid port in '{target}'");
        }

        if (host.Length == 0) return (null, $"missing host in '{target}'");
        if (watch && !dump) return (null, "--watch needs --dump");
        if (verb is { } vb)
        {
            if (dump || report is not null) return (null, "--command runs one verb; it takes no --dump or --report");
            if (speed is not null && vb != Verb.Move) return (null, "--speed belongs to --command move");
            if (forSeconds is not null && vb != Verb.Jog) return (null, "--for belongs to --command jog");
            if (new VerbRequest(vb).Moves && !allowMotion) // the one list, bound to protocol.md by GA-U-146
                return (null, $"--command {vb.ToString().ToLowerInvariant()} moves the axis: it needs --allow-motion (an operator at the machine, the travel clear)");
        }
        else if (speed is not null || forSeconds is not null)
        {
            return (null, "--speed and --for belong to --command");
        }
        if (owner == CheckerOptions.ForeignOwnerId)
            return (null, $"--owner-id {owner} is the foreign id CHK-11 impersonates; use another");

        var map = new RegisterMap(commandBase, statusBase);
        try
        {
            map.Validate();
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        return (new CheckerOptions
        {
            Host = host, Port = port, Unit = (byte)unit, CommandBase = commandBase, StatusBase = statusBase,
            OwnerId = (ushort)owner, AllowMotion = allowMotion, Tolerance = tolerance, Report = report, Dump = dump, Watch = watch,
            Command = verb switch
            {
                null => null,
                Verb.Move => new VerbRequest(Verb.Move, Target: verbNumber, Speed: speed ?? VerbRequest.DefaultSpeed),
                Verb.Jog => new VerbRequest(Verb.Jog, Velocity: verbNumber, For: forSeconds is { } f ? TimeSpan.FromSeconds(f) : null),
                var other => new VerbRequest(other.Value),
            },
        }, null);
    }

    private static int Int(string name, string value, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max
            ? n
            : throw new FormatException($"{name} must be an integer {min}–{max}, got '{value}'");

    private static ReportTarget Report(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".md" => new ReportTarget(path, Path.ChangeExtension(path, ".json")),
            ".json" => new ReportTarget(null, path),
            _ => throw new FormatException($"--report must end in .md or .json, got '{path}'"),
        };
    }
}
