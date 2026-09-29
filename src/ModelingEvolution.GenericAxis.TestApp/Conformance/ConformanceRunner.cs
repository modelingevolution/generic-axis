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
            string? refusal = null;
            var interruptedInPreflight = false;
            try
            {
                refusal = await PreflightAsync(ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Review #29: Ctrl-C before pre-flight proved the axis free. Nothing has been written, so the cleanup
                // below finds nothing to undo and writes nothing — the axis may belong to a live commander.
                interruptedInPreflight = true;
                var reason = Interrupted("pre-flight");
                _log.LogWarning("{Reason}; nothing was written", reason);
                foreach (var def in _catalog) results.Add(Skipped(def, reason));
            }

            if (interruptedInPreflight) { }
            else if (refusal is { } reason)
            {
                refused = true;
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

        var final = Complete(report with { Refused = refused, Interrupted = !refused && ct.IsCancellationRequested, Checks = results.ToImmutable() }, ctx.CleanupLog);
        _log.LogInformation("RESULT: {Result} — {Pass} pass, {Fail} fail, {Skipped} skipped (exit {Exit})",
            final.SummaryResult, final.PassCount, final.FailCount, final.SkippedCount, final.ExitCode);
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
            var skip = blocked ?? (ct.IsCancellationRequested ? Interrupted(def.Id) : SkipReason(def, options, byId));
            if (skip is not null)
            {
                if (ct.IsCancellationRequested) blocked ??= skip;
                results.Add(Skipped(def, skip));
                byId[def.Id] = CheckResultKind.Skipped;
                publish(null);
                continue;
            }

            publish(def.Id);
            var retriesBefore = ctx.Retries;
            var (outcome, durationMs) = await RunOneAsync(def, ctx, ct);

            if (ct.IsCancellationRequested)
            {
                // Interruption is not a FAIL: the running check and every later one are SKIPPED.
                blocked = Interrupted(def.Id);
                results.Add(Skipped(def, blocked) with { DurationMs = durationMs });
                byId[def.Id] = CheckResultKind.Skipped;
                publish(null);
                continue;
            }

            LastRead? lastRead = null;
            if (outcome.Result == CheckResultKind.Fail)
            {
                // A fresh read of both blocks when the failure is detected, before any restore write.
                lastRead = await ctx.FreshLastReadAsync();
                outcome = outcome.With(SeenIn(lastRead, def, ctx));
            }

            if (def.Restores)
            {
                var failed = await RestoreAsync(ctx, ct);
                if (ct.IsCancellationRequested) blocked = Interrupted(def.Id);
                else if (failed is not null)
                {
                    lastRead ??= ctx.LastValues();
                    outcome = outcome.With([failed with { Text = $"cannot restore the axis: {failed.Text}" }]);
                    blocked = $"{def.Id} could not restore the axis";
                }
            }

            outcome = outcome with { Observed = Canonical(def, outcome, ctx.RetriesSince(retriesBefore)) };
            _log.LogInformation("{Id} {Result}: {Message}", def.Id, ReportWriter.Result(outcome.Result),
                outcome.Result == CheckResultKind.Fail ? $"{def.Id}: {outcome.Message}" : outcome.Message);
            results.Add(new CheckResult(def.Id, def.Title, def.Section, outcome.Result, durationMs, outcome.Message, outcome.Observed,
                outcome.Deciding?.Class, outcome.Result == CheckResultKind.Fail ? lastRead : null));
            byId[def.Id] = outcome.Result;
            publish(null);
        }
    }

    /// <summary>
    /// protocol § Observed values: exactly the check's keys, in order, <c>null</c> for a value never observed, and
    /// <c>retries</c> last. A key a check reports that the table does not list is a defect in the catalog.
    /// </summary>
    internal ImmutableArray<KeyValuePair<string, long?>> Canonical(CheckDefinition def, CheckOutcome outcome, long retries)
    {
        var seen = outcome.Observed.ToDictionary(kv => kv.Key, kv => kv.Value);
        var unknown = seen.Keys.Except(def.ObservedKeys).ToList();
        if (unknown.Count > 0) // a catalog defect: never abort a run on a real PLC for it, but say so loudly
            _log.LogError("{Id} reported observed keys the protocol does not list, dropped: {Keys}", def.Id, string.Join(", ", unknown));
        return [.. def.ObservedKeys.Select(k => KeyValuePair.Create(k, seen.GetValueOrDefault(k))), KeyValuePair.Create("retries", (long?)retries)];
    }

    private static string Interrupted(string id) => $"interrupted by the operator during {id}";

    /// <summary>
    /// What the last read itself shows (protocol § "Error class of a FAIL", rows 3 and 4): a non-protocol State, ErrorStop
    /// without a FaultCode, or a fault the check did not expect.
    /// </summary>
    private static IEnumerable<Failure> SeenIn(LastRead read, CheckDefinition def, CheckContext ctx)
    {
        if (read.Status[0] is not { } state) yield break;
        var fault = (ushort)(read.Status[6] ?? 0);
        var where = ctx.Where(ctx.Map.State);
        if (state is 5 or > 7 || (state == 7 && fault == 0)) yield return Failure.InvalidState((ushort)state, fault, where);
        else if (state == 7 && !(fault == 4 && CheckCatalog.ExpectWatchdogFault.Contains(def.Id)))
            yield return Failure.Fault(fault, ctx.Where(ctx.Map.FaultCode));
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
            outcome = CheckOutcome.Skipped(Interrupted(def.Id));
        }
        catch (MotionException ex) when (ex.Error == MotionError.CommunicationLost)
        {
            outcome = CheckOutcome.Fail(Failure.Transport(ex.Message));
        }
        catch (MotionException ex)
        {
            outcome = CheckOutcome.Fail(Failure.Protocol($"{ex.Error}: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Id} threw", def.Id);
            outcome = CheckOutcome.Fail(Failure.Protocol($"the checker failed unexpectedly: {ex.GetType().Name}: {ex.Message}"));
        }

        return (outcome, (long)t.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Before its own first beat, reads <c>LeaseOwner</c> and watches <c>Heartbeat</c> (C+8) for 1 s. Any change, whatever
    /// <c>LeaseOwner</c> holds (0, a station id or the tool's own id), means another commander is live: returns the
    /// refusal message naming the beat values and the owner. Null when nothing beats.
    /// </summary>
    private async Task<string?> PreflightAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.Channel.ConnectAsync(ct);
            var first = await ReadBeatAndOwnerAsync(ctx, ct);
            var beats = new List<ushort> { first.Beat };
            var owner = first.Owner;
            var until = Stopwatch.StartNew();
            while (until.Elapsed < PreflightWindow)
            {
                await Task.Delay(Beater.Period, ct);
                var now = await ReadBeatAndOwnerAsync(ctx, ct);
                if (now.Beat != beats[^1]) beats.Add(now.Beat);
                owner = now.Owner;
            }

            _log.LogInformation("Pre-flight: LeaseOwner {Owner}, Heartbeat {Beats}", owner, string.Join(" → ", beats));
            return beats.Count > 1
                ? $"refused to start: another commander is beating — Heartbeat ({ctx.Where(ctx.Map.Heartbeat)}) read {string.Join(" → ", beats.Take(6))}"
                  + $"{(beats.Count > 6 ? " …" : "")} within 1 s, LeaseOwner ({ctx.Where(ctx.Map.LeaseOwner)}) = {owner}; stop it first"
                : null;
        }
        catch (MotionException ex)
        {
            _log.LogWarning("Pre-flight could not read the PLC ({Message}); CHK-01 will report it", ex.Message);
            return null;
        }
    }

    private static async Task<(ushort Beat, ushort Owner)> ReadBeatAndOwnerAsync(CheckContext ctx, CancellationToken ct)
    {
        var words = await ctx.Channel.ReadHoldingAsync(ctx.Unit, ctx.Map.Heartbeat, 2, $"read C+8…C+9 unit {ctx.Unit} (pre-flight: Heartbeat, LeaseOwner)",
            ChannelPriority.Move, ct);
        return (words[0], words[1]);
    }

    /// <summary>
    /// Every check from CHK-06 on ends in State 0 or 1, no latched fault, the lease held and the beat running.
    /// Returns null when restored, else the reason.
    /// </summary>
    private async Task<Failure?> RestoreAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            var v = await ctx.ReadViewAsync(ct);
            if (v.State is 2 or 3 or 4)
            {
                var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
                if (!stop.Acked) return Failure.NotAcknowledged("Stop", stop.Seq, stop.View.Status.CommandAck, stop.View.State);
            }

            if (v.State is 2 or 3 or 4 or 6)
            {
                var still = await ctx.WaitForAsync(x => x.State is not (2 or 3 or 4 or 6), TimeSpan.FromSeconds(5), CheckContext.Now(), ct);
                if (!still.Met) return Failure.Machine("MotionFailed", $"still moving 5 s after Stop. Read State ({ctx.Where(ctx.Map.State)}) = {still.View.State}, expected 0 or 1.");
                v = still.View;
            }

            if (v.LeaseOwner != ctx.Options.OwnerId) await ctx.TakeLeaseAsync(ct);
            if (v.WatchdogFault != 0) await ctx.ClearWatchdogFaultAsync(ct);
            await ctx.Beater.StartAsync(ct);

            if (v.State == 7)
            {
                var reset = await ctx.Commands.SendAsync(CommandBits.Reset, ct);
                if (!reset.Acked) return Failure.NotAcknowledged("Reset", reset.Seq, reset.View.Status.CommandAck, reset.View.State);
                var w = await ctx.WaitForAsync(x => x.State != 7, TimeSpan.FromSeconds(5), reset.WrittenAt, ct);
                if (!w.Met) return Failure.Fault(w.View.Status.FaultCode, ctx.Where(ctx.Map.FaultCode)) with { Text = $"still in ErrorStop 5 s after Reset. Read FaultCode ({ctx.Where(ctx.Map.FaultCode)}) = {w.View.Status.FaultCode}." };
            }

            var end = await ctx.ReadViewAsync(ct);
            if (end.State is not (0 or 1)) return Failure.Machine("MotionFailed", $"Read State ({ctx.Where(ctx.Map.State)}) = {end.State}, expected 0 or 1.");
            if (end.Status.FaultCode != 0) return Failure.Fault(end.Status.FaultCode, ctx.Where(ctx.Map.FaultCode));
            if (end.WatchdogFault != 0) return Failure.Protocol($"WatchdogFault did not clear. Read WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = {end.WatchdogFault}, expected 0.");
            if (end.LeaseOwner != ctx.Options.OwnerId) return Failure.Protocol($"the lease write did not hold. Read LeaseOwner ({ctx.Where(ctx.Map.LeaseOwner)}) = {end.LeaseOwner}, expected {ctx.Options.OwnerId}.");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null; // cleanup follows
        }
        catch (MotionException ex)
        {
            return ex.Error == MotionError.CommunicationLost ? Failure.Transport(ex.Message) : Failure.Protocol($"{ex.Error}: {ex.Message}");
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
