using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// Runs CHK-01…16 against one PLC (protocol § Rules for every run): pre-flight (exit 3 on a live foreign lease),
/// id order, prerequisite skipping, per-check restore from CHK-06 on, and a cleanup that always runs — after a FAIL,
/// an exception or a cancellation (Ctrl-C) — with its own token.
/// </summary>
public sealed class ConformanceRunner(ILoggerFactory loggerFactory)
{
    private IReadOnlyList<CheckDefinition> _catalog = CheckCatalog.All;

    /// <summary>A runner over another catalog (runner-logic tests).</summary>
    internal ConformanceRunner(ILoggerFactory loggerFactory, IReadOnlyList<CheckDefinition> catalog) : this(loggerFactory) =>
        _catalog = catalog;

    private static readonly TimeSpan PreflightWindow = TimeSpan.FromSeconds(1);
    private readonly ILogger _log = loggerFactory.CreateLogger<ConformanceRunner>();

    /// <summary>The tool's version for the report.</summary>
    public static string ToolVersion { get; } =
        typeof(ConformanceRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Runs the whole checklist. <paramref name="progress"/> receives an immutable report after every step.</summary>
    public Task<ConformanceReport> RunAsync(CheckerOptions options, CancellationToken ct, Action<ConformanceReport>? progress = null) =>
        RunAsync(options, new ModbusChannel(options.Host, options.Port, loggerFactory.CreateLogger<ModbusChannel>()), ct, progress);

    internal async Task<ConformanceReport> RunAsync(CheckerOptions options, IModbusChannel channel, CancellationToken ct,
        Action<ConformanceReport>? progress = null)
    {
        var report = new ConformanceReport { Options = options, ToolVersion = ToolVersion, StartedAt = DateTimeOffset.UtcNow };
        _log.LogInformation("Conformance check of {Host}:{Port} unit {Unit} (C={C}, S={S}), owner {Owner}, motion {Motion}",
            options.Host, options.Port, options.Unit, options.CommandBase, options.StatusBase, options.OwnerId,
            options.AllowMotion ? "ALLOWED" : "not allowed");

        await using var ctx = new CheckContext(options, channel, _log);
        var results = ImmutableArray.CreateBuilder<CheckResult>();
        void Publish(string? running = null) =>
            progress?.Invoke(report with { Checks = results.ToImmutable(), Cleanup = [.. ctx.CleanupLog], Running = running });

        var refused = false;
        try
        {
            var incumbent = await PreflightAsync(ctx, ct);
            if (incumbent is { } owner)
            {
                refused = true;
                var reason = $"refused to start: LeaseOwner {owner} is beating — another commander is attached; stop it first";
                _log.LogWarning("{Reason}", reason);
                foreach (var def in _catalog) results.Add(Skipped(def, reason));
            }
            else
            {
                await RunChecksAsync(ctx, options, results, Publish, ct);
            }
        }
        finally
        {
            await CleanupAsync(ctx);
        }

        var final = Complete(report with { Refused = refused, Checks = results.ToImmutable() }, ctx.CleanupLog);
        _log.LogInformation("RESULT: {Result} — {Pass} pass, {Fail} fail, {Skipped} skipped (exit {Exit})",
            final.Passed ? "PASS" : "FAIL", final.PassCount, final.FailCount, final.SkippedCount, final.ExitCode);
        progress?.Invoke(final);
        return final;
    }

    private async Task RunChecksAsync(CheckContext ctx, CheckerOptions options, ImmutableArray<CheckResult>.Builder results,
        Action<string?> publish, CancellationToken ct)
    {
        string? blocked = null;
        var byId = new Dictionary<string, CheckResultKind>();
        foreach (var def in _catalog)
        {
            if (ct.IsCancellationRequested) blocked ??= "run interrupted";
            var skip = blocked ?? SkipReason(def, options, byId);
            if (skip is not null)
            {
                results.Add(Skipped(def, skip));
                byId[def.Id] = CheckResultKind.Skipped;
                publish(null);
                continue;
            }

            publish(def.Id);
            var (outcome, durationMs) = await RunOneAsync(def, ctx, ct);

            if (def.Restores && !ct.IsCancellationRequested)
            {
                var failed = await RestoreAsync(ctx, ct);
                if (failed is not null)
                {
                    outcome = CheckOutcome.Fail(
                        outcome.Message.Length == 0 ? $"cannot restore: {failed}" : $"{outcome.Message}; cannot restore: {failed}",
                        [.. outcome.Observed.Select(kv => (kv.Key, kv.Value))]);
                    blocked = $"{def.Id} could not restore the axis";
                }
            }

            if (ct.IsCancellationRequested) blocked ??= "run interrupted";
            _log.LogInformation("{Id} {Result}: {Message}", def.Id, ReportWriter.Result(outcome.Result), outcome.Message);
            results.Add(new CheckResult(def.Id, def.Title, def.Section, outcome.Result, durationMs, outcome.Message, outcome.Observed));
            byId[def.Id] = outcome.Result;
            publish(null);
        }
    }

    /// <summary>Completes the report after cleanup (cleanup lines and the finish time).</summary>
    public static ConformanceReport Complete(ConformanceReport report, IReadOnlyList<string> cleanup) =>
        report with { Cleanup = [.. cleanup], FinishedAt = DateTimeOffset.UtcNow, Running = null };

    private static CheckResult Skipped(CheckDefinition def, string reason) =>
        new(def.Id, def.Title, def.Section, CheckResultKind.Skipped, 0, reason, []);

    private static string? SkipReason(CheckDefinition def, CheckerOptions options, IReadOnlyDictionary<string, CheckResultKind> done)
    {
        if (def.RequiresMotion && !options.AllowMotion) return "needs --allow-motion";
        foreach (var need in def.Needs)
        {
            var result = done.GetValueOrDefault(need, CheckResultKind.Skipped);
            if (result != CheckResultKind.Pass) return $"needs {need}, which {(result == CheckResultKind.Fail ? "FAILED" : "was SKIPPED")}";
        }

        return null;
    }

    private async Task<(CheckOutcome Outcome, long DurationMs)> RunOneAsync(CheckDefinition def, CheckContext ctx, CancellationToken ct)
    {
        _log.LogInformation("{Id} {Title} — running", def.Id, def.Title);
        var t = Stopwatch.StartNew();
        CheckOutcome outcome;
        try
        {
            outcome = await def.RunAsync(ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = CheckOutcome.Fail("interrupted by the operator (not a PLC defect)");
        }
        catch (MotionException ex)
        {
            outcome = CheckOutcome.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Id} threw", def.Id);
            outcome = CheckOutcome.Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        return (outcome, (long)t.Elapsed.TotalMilliseconds);
    }

    /// <summary>Reads <c>LeaseOwner</c> and watches <c>Heartbeat</c> for 1 s; returns a live foreign owner, else null.</summary>
    private async Task<ushort?> PreflightAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.Channel.ConnectAsync(ct);
            var first = await ReadBeatAndOwnerAsync(ctx, ct);
            var beating = false;
            var owner = first.Owner;
            var until = Stopwatch.StartNew();
            while (until.Elapsed < PreflightWindow)
            {
                await Task.Delay(Beater.Period, ct);
                var now = await ReadBeatAndOwnerAsync(ctx, ct);
                beating |= now.Beat != first.Beat;
                owner = now.Owner;
            }

            _log.LogInformation("Pre-flight: LeaseOwner {Owner}, Heartbeat {State}", owner, beating ? "changing" : "still");
            return beating && owner != 0 && owner != ctx.Options.OwnerId ? owner : null;
        }
        catch (MotionException ex)
        {
            _log.LogWarning("Pre-flight could not read the PLC ({Message}); CHK-01 will report it", ex.Message);
            return null;
        }
    }

    private static async Task<(ushort Beat, ushort Owner)> ReadBeatAndOwnerAsync(CheckContext ctx, CancellationToken ct)
    {
        var words = await ctx.Channel.ReadHoldingAsync(ctx.Unit, ctx.Map.Heartbeat, 2, "pre-flight: heartbeat and lease owner",
            ChannelPriority.Move, ct);
        return (words[0], words[1]);
    }

    /// <summary>
    /// Every check from CHK-06 on ends in State 0 or 1, no latched fault, the lease held and the beat running.
    /// Returns null when restored, else the reason.
    /// </summary>
    private async Task<string?> RestoreAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            var v = await ctx.ReadViewAsync(ct);
            if (v.State is 2 or 3 or 4)
            {
                await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
            }

            if (v.State is 2 or 3 or 4 or 6)
            {
                var still = await ctx.WaitForAsync(x => x.State is not (2 or 3 or 4 or 6), TimeSpan.FromSeconds(5), CheckContext.Now(), ct);
                if (!still.Met) return $"still in State {still.View.State} 5 s after Stop";
                v = still.View;
            }

            if (v.LeaseOwner != ctx.Options.OwnerId) await ctx.TakeLeaseAsync(ct);
            if (v.WatchdogFault != 0) await ctx.ClearWatchdogFaultAsync(ct);
            await ctx.Beater.StartAsync(ct);

            if (v.State == 7)
            {
                var reset = await ctx.Commands.SendAsync(CommandBits.Reset, ct);
                if (!reset.Acked) return $"Reset not acknowledged within 500 ms (State 7, FaultCode {v.Status.FaultCode})";
                var w = await ctx.WaitForAsync(x => x.State != 7, TimeSpan.FromSeconds(5), reset.WrittenAt, ct);
                if (!w.Met) return $"still in ErrorStop (FaultCode {w.View.Status.FaultCode}) 5 s after Reset";
            }

            var end = await ctx.ReadViewAsync(ct);
            if (end.State is not (0 or 1)) return $"State {end.State}, expected 0 or 1";
            if (end.Status.FaultCode != 0) return $"FaultCode {end.Status.FaultCode} latched";
            if (end.WatchdogFault != 0) return $"WatchdogFault {end.WatchdogFault} latched";
            if (end.LeaseOwner != ctx.Options.OwnerId) return $"LeaseOwner {end.LeaseOwner}, not the checker's {ctx.Options.OwnerId}";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null; // cleanup follows
        }
        catch (MotionException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Always: Stop edge if State is 2, 3 or 4 · clear edge bits and Enable 0 · stop beating · <c>WatchdogFault = 0</c> if the
    /// checker caused a trip · <c>LeaseOwner = 0</c> if it holds the checker's id. Nothing is written that the checker did
    /// not change. Each write is journalled for the report.
    /// </summary>
    private async Task CleanupAsync(CheckContext ctx)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = budget.Token;
        var c = ctx.Map;
        try
        {
            if (!ctx.Commands.Used && !ctx.TookLease && !ctx.Beater.IsRunning && !ctx.CausedTrip)
            {
                _log.LogInformation("Cleanup: the checker wrote no command, lease or beat — nothing to undo");
                return;
            }

            var v = await ctx.ReadViewAsync(ct);
            if (v.State is 2 or 3 or 4)
            {
                var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
                ctx.Journal($"C+0 = 0x{(ushort)(CommandBits.Enable | CommandBits.Stop):X4} (Stop), CommandSeq {stop.Seq}");
                await ctx.WaitForAsync(x => x.State is not (2 or 3 or 4 or 6), TimeSpan.FromSeconds(2), CheckContext.Now(), ct);
            }

            if (ctx.Commands.Used)
            {
                var off = await ctx.Commands.SendAsync(CommandBits.None, ct, ChannelPriority.Stop);
                ctx.Journal($"C+0 = 0x0000 (clear edge bits, Enable 0), CommandSeq {off.Seq}");
            }

            if (ctx.Beater.IsRunning)
            {
                await ctx.Beater.StopAsync();
                ctx.Journal($"C+{c.Heartbeat - c.CommandBase} (Heartbeat): stopped beating");
            }

            v = await ctx.ReadViewAsync(ct);
            if (ctx.CausedTrip && v.WatchdogFault != 0)
            {
                await ctx.ClearWatchdogFaultAsync(ct);
                ctx.Journal($"C+{c.WatchdogFault - c.CommandBase} = 0 (clear the WatchdogFault the checker caused)");
            }

            if (v.LeaseOwner == ctx.Options.OwnerId)
            {
                await ctx.ReleaseLeaseAsync(ct);
                ctx.Journal($"C+{c.LeaseOwner - c.CommandBase} = 0 (release lease {ctx.Options.OwnerId})");
            }
        }
        catch (Exception ex)
        {
            ctx.Journal($"cleanup incomplete: {ex.Message}");
            _log.LogError(ex, "Cleanup failed; check the PLC by hand (Enable, LeaseOwner, WatchdogFault)");
        }
    }
}
