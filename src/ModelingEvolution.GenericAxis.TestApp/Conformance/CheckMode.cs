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
            if (options.Dump) return await DumpAsync(options, loggerFactory, cts.Token);
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

    /// <summary>
    /// Rule 4: read C+0…C+11 and S+0…S+14 once (or at 5 Hz with <c>--watch</c> until Ctrl-C) and print them decoded.
    /// Writes nothing and takes no lease. Exit 0 when both blocks were read, 1 on a Transport error.
    /// </summary>
    public static async Task<int> DumpAsync(CheckerOptions options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // The context owns and disposes the channel.
        await using var ctx = new CheckContext(options, new ModbusChannel(options.Host, options.Port, loggerFactory.CreateLogger<ModbusChannel>()), loggerFactory.CreateLogger("GenericAxis.Dump"));
        do
        {
            try
            {
                await ctx.ReadAsync(ctx.Map.Command, RegisterMap.CommandLength, ct);
                await ctx.ReadAsync(ctx.Map.Status, RegisterMap.StatusLength, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ConformanceExitCodes.Pass;
            }
            catch (RocketWelder.SDK.Devices.Motion.MotionException ex)
            {
                await Console.Error.WriteLineAsync($"dump: Transport/CommunicationLost: {ex.Message}");
                return ConformanceExitCodes.Fail;
            }

            await Console.Out.WriteAsync(
                $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {options.Host}:{options.Port} unit {options.Unit}\n\n{RegisterDump.Render(ctx.LastValues(), ctx.Map)}\n");
            if (!options.Watch) return ConformanceExitCodes.Pass;
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); }
            catch (OperationCanceledException) { return ConformanceExitCodes.Pass; }
        } while (true);
    }
}
