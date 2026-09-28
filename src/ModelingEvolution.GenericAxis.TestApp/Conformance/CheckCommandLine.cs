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

    public RegisterMap Map => new(CommandBase, StatusBase);
}

/// <summary>
/// Parses <c>--check &lt;host&gt;[:port] [--unit N] [--command-base N] [--status-base N] [--owner-id N]
/// [--allow-motion] [--tolerance X] [--report PATH]</c>. The leading <c>--check</c> is optional here.
/// </summary>
public static class CheckCommandLine
{
    public const string Flag = "--check";

    public const string Usage =
        "Usage: --check <host>[:port] [--unit N] [--command-base N] [--status-base N] [--owner-id N] "
        + "[--allow-motion] [--tolerance X] [--report FILE.md|FILE.json] | --check <host>[:port] --dump [--watch]";

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
