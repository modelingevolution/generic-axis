namespace ModelingEvolution.GenericAxis.TestApp;

/// <summary>
/// The arguments the UI (web) mode owns. Anything else is a usage error (exit 2), never a silent start of a simulated
/// PLC and an unauthenticated <c>/driver</c> page (review #32: <c>--dump 192.168.58.20</c> without <c>--check</c>).
/// </summary>
public static class WebMode
{
    public const string Usage =
        "Usage: (no arguments) the UI on http://localhost:5070 | --urls URL | --Section:Key=value … "
        + "| --check <host>[:port] … | --command <verb> … <host>[:port] | --headless [--port N]";

    /// <summary>Null when every argument belongs to web mode, else the error for exit code 2.</summary>
    public static string? Validate(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--urls" or "--environment" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Configuration overrides: --Simulator:Port=5100, Simulator:Faults:SuppressAck=true, --urls=…
            var key = a.TrimStart('-').Split('=', 2);
            if (key.Length == 2 && key[0].Length > 0 && (key[0].Contains(':') || key[0] is "urls" or "environment")) continue;

            return $"'{a}' is not an argument of the UI mode"
                   + (a is "--dump" or "--watch" ? " (--dump needs --check <host>[:port] --dump)" : "");
        }

        return null;
    }
}
