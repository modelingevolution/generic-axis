using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// <c>--check &lt;host&gt;[:port] …</c>: the headless conformance check. No web host. Logs go to stderr, the Markdown report
/// to stdout, <c>--report</c> files to disk; the process exit code is the protocol's (0 · 1 · 2 · 3).
/// Ctrl-C cancels the run and the cleanup still runs.
/// </summary>
public static class CheckMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (options, error) = CheckCommandLine.Parse(args);
        if (options is null)
        {
            await Console.Error.WriteLineAsync($"error: {error}\n{CheckCommandLine.Usage}");
            return ConformanceExitCodes.Usage;
        }

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("ModelingEvolution.GenericAxis.ModbusChannel", LogLevel.Warning)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // keep the process alive for the cleanup
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var report = await new ConformanceRunner(loggerFactory).RunAsync(options, cts.Token);
            var markdown = ReportWriter.ToMarkdown(report);
            await Console.Out.WriteAsync(markdown);
            if (options.Report is { } target)
            {
                if (target.MarkdownPath is { } md) await File.WriteAllTextAsync(md, markdown, CancellationToken.None);
                await File.WriteAllTextAsync(target.JsonPath, ReportWriter.ToJson(report), CancellationToken.None);
            }

            return report.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
