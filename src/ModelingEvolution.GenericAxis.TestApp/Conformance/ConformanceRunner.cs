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
        string? preflightNote = null;
        void Publish(string? running = null) =>
            progress?.Invoke(report with { Checks = results.ToImmutable(), Cleanup = [.. ctx.CleanupLog], Running = running, Preflight = preflightNote });

        var refused = false;
        try
        {
            string? refusal = null;
            MotionException? unreadable = null;
            var interruptedInPreflight = false;
            try
            {
                var preflight = await PreflightAsync(ctx, ct);
                refusal = preflight.Refusal;
                unreadable = preflight.Unreadable;
                preflightNote = preflight.Note ?? preflight.Refusal;
                if (preflight.Note is { } note) _log.LogWarning("Pre-flight: {Note}", note);
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
            else if (unreadable is not null)
            {
                // No lease, no beat, no write: CHK-01 FAILs with the read's message, every later check needs it.
                var chk01 = _catalog[0];
                var outcome = CheckOutcome.Fail(Failure.FromMotion(unreadable));
                outcome = outcome with { Observed = Canonical(chk01, outcome, ctx.Retries) };
                results.Add(new CheckResult(chk01.Id, chk01.Title, chk01.Section, CheckResultKind.Fail, 0, outcome.Message, outcome.Observed,
                    outcome.Deciding?.Class, ctx.LastValues()));
                foreach (var def in _catalog.Skip(1)) results.Add(Skipped(def, $"needs {chk01.Id}, which FAILED"));
            }
            else if (refusal is { } reason)
            {
                refused = true;
                _log.LogWarning("{Reason}", reason);
                foreach (var def in _catalog) results.Add(Skipped(def, reason));
            }
            else
            {
                // protocol § Rules, "Lease and beat between checks": take the lease and beat as soon as pre-flight
                // passes, so a second tool is refused from CHK-01 on. Not over a dead holder's trip: that axis is left
                // as found for its operator (review #35).
                if (!ctx.ForeignTrip)
                {
                    try
                    {
                        // Only on a PLC that speaks map v1: a wrong MapVersion stops the run at CHK-02 with nothing written.
                        var mapVersion = (await ctx.ReadAsync(ctx.Map.MapVersion, 1, ct))[0];
                        if (mapVersion == RegisterMap.Version)
                        {
                            await ctx.TakeLeaseAsync(ct);
                            await ctx.Beater.StartAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Ctrl-C here: RunChecksAsync reports every check interrupted, and cleanup undoes the lease.
                    }
                    catch (MotionException ex)
                    {
                        _log.LogWarning("Taking the lease after pre-flight failed ({Message}); CHK-01 will report the transport", CheckerText.Describe(ex));
                    }
                }

                await RunChecksAsync(ctx, options, results, Publish, ct);
            }
        }
        finally
        {
            await CleanupAsync(ctx);
        }

        var final = Complete(report with { Refused = refused, Interrupted = !refused && ct.IsCancellationRequested, Checks = results.ToImmutable(), Preflight = preflightNote }, ctx.CleanupLog);
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

            // A check may catch the guard's exception itself; the lost lease still fails it.
            if (ctx.LeaseLost is { } lost) outcome = outcome.With([Failure.Protocol(lost)]);

            LastRead? lastRead = null;
            if (outcome.Result == CheckResultKind.Fail)
            {
                // A fresh read of both blocks when the failure is detected, before any restore write.
                lastRead = await ctx.FreshLastReadAsync();
                outcome = outcome.With(SeenIn(lastRead, def, ctx));
            }

            if (ctx.LeaseLost is { } lostLease)
            {
                // Another commander owns the axis now: no restore, and every later check is SKIPPED with the reason.
                blocked = $"{def.Id}: {lostLease}";
            }
            else if (ctx.NotRestorable)
            {
                // A precondition FAIL is a failure to restore: the checker did not fault this axis, so it does not Reset it.
                blocked = $"restore after {def.Id} failed";
            }
            else if (outcome.Deciding is { Class: null })
            {
                // The checker itself failed: the axis is in a state the checker cannot vouch for; nothing more runs.
                blocked = $"not run: checker error during {def.Id}";
            }
            else if (def.Restores)
            {
                var failed = await RestoreAsync(ctx, ct);
                if (ct.IsCancellationRequested) blocked = Interrupted(def.Id);
                else if (failed is not null)
                {
                    lastRead ??= ctx.LastValues();
                    outcome = outcome.With([failed with { Text = $"cannot restore the axis: {failed.Text}" }]);
                    blocked = ctx.LeaseLost is { } lostInRestore ? $"{def.Id}: {lostInRestore}" : $"{def.Id} could not restore the axis";
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
        catch (MotionException ex)
        {
            // Under its own MotionError name and the protocol's class for it; never relabelled (review #33).
            outcome = CheckOutcome.Fail(Failure.FromMotion(ex));
        }
        catch (Exception ex)
        {
            // A defect in the checker, not a verdict on the PLC: no class (protocol § Report schema).
            _log.LogError(ex, "{Id} threw", def.Id);
            outcome = CheckOutcome.Fail(Failure.CheckerError(ex));
        }

        return (outcome, (long)t.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// protocol § Rules, Pre-flight. Before its own first beat, reads <c>LeaseOwner</c> and <c>WatchdogFault</c> and watches
    /// <c>Heartbeat</c> (C+8): 1 s, or 1.6 s while <c>LeaseOwner ≠ 0</c> (review #35: a commander silent for 1 s may still
    /// be alive, the watchdog trips only 1.0–1.5 s after its last beat). Any beat → refused. A held lease with
    /// <c>WatchdogFault = 1</c> and no beat → its holder is dead: proceed with a note, never clearing that trip. A held
    /// lease with neither within 1.6 s → refused.
    /// </summary>
    private async Task<Preflight> PreflightAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.Channel.ConnectAsync(ct);
            var first = await ReadBeatOwnerFaultAsync(ctx, ct);
            var beats = new List<ushort> { first.Beat };
            var (owner, fault) = (first.Owner, first.Fault);
            var watched = Stopwatch.StartNew();
            // Watch until a beat, a trip under a held lease, or the window's end; a beat is still watched to 1 s so the
            // refusal names several values.
            while (beats.Count > 1
                       ? watched.Elapsed < PreflightWindow
                       : !(owner != 0 && fault != 0) && watched.Elapsed < (owner == 0 ? PreflightWindow : HeldLeaseWindow))
            {
                await Task.Delay(Beater.Period, ct);
                var now = await ReadBeatOwnerFaultAsync(ctx, ct);
                if (now.Beat != beats[^1]) beats.Add(now.Beat);
                (owner, fault) = (now.Owner, now.Fault);
            }

            var ownerAt = $"LeaseOwner ({ctx.Where(ctx.Map.LeaseOwner)}) = {owner}";
            var faultAt = $"WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = {fault}";
            _log.LogInformation("Pre-flight after {Ms} ms: {Owner}, {Fault}, Heartbeat {Beats}",
                (long)watched.Elapsed.TotalMilliseconds, ownerAt, faultAt, string.Join(" → ", beats));
            if (beats.Count > 1)
                return new Preflight(
                    $"refused to start: another commander is beating — Heartbeat ({ctx.Where(ctx.Map.Heartbeat)}) read {string.Join(" → ", beats.Take(6))}"
                    + $"{(beats.Count > 6 ? " …" : "")} within {watched.Elapsed.TotalSeconds:0.0} s, {ownerAt}; stop it first", null);
            if (owner == 0) return Preflight.Free;
            if (fault != 0)
            {
                ctx.ForeignTrip = true;
                return new Preflight(null,
                    $"{ownerAt} held with no beat and {faultAt}: the previous commander is dead; its trip is left for its operator.");
            }

            return new Preflight(
                $"refused to start: {ownerAt} is held and {faultAt}: no beat and no trip within {HeldLeaseWindow.TotalSeconds:0.0} s — "
                + "a live commander, or a PLC without a working watchdog; release LeaseOwner by hand only if no commander runs", null);
        }
        catch (MotionException ex)
        {
            // Python #4 mirror (lead ruling): a pre-flight that could not prove the axis free must not proceed.
            _log.LogWarning("Pre-flight could not read the PLC ({Message}); CHK-01 FAILs and nothing is written", CheckerText.Describe(ex));
            return new Preflight(null, null, ex);
        }
    }

    /// <summary>
    /// The pre-flight verdict: a refusal (exit 3), a note for the report when the run proceeds, or the read that failed
    /// (the axis was never proved free: CHK-01 FAILs, nothing more runs).
    /// </summary>
    private sealed record Preflight(string? Refusal, string? Note, MotionException? Unreadable = null)
    {
        public static Preflight Free { get; } = new(null, null);
    }

    /// <summary>1.5 s (the latest trip, FR-11) plus one 100 ms read.</summary>
    private static readonly TimeSpan HeldLeaseWindow = TimeSpan.FromMilliseconds(1600);

    private static async Task<(ushort Beat, ushort Owner, ushort Fault)> ReadBeatOwnerFaultAsync(CheckContext ctx, CancellationToken ct)
    {
        var words = await ctx.Channel.ReadHoldingAsync(ctx.Unit, ctx.Map.Heartbeat, 3, "read C+8…C+10 (pre-flight: Heartbeat, LeaseOwner, WatchdogFault)",
            ChannelPriority.Move, ct);
        return (words[0], words[1], words[2]);
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
            if (ctx.ForeignTrip && !ctx.CausedTrip && v.WatchdogFault != 0)
                return Failure.Fault(4, ctx.Where(ctx.Map.FaultCode)) with
                {
                    Text = $"the previous commander's watchdog trip is left for its operator. Read WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = "
                           + $"{v.WatchdogFault}, LeaseOwner ({ctx.Where(ctx.Map.LeaseOwner)}) = {v.LeaseOwner}; the checker does not clear it.",
                };

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
            else ctx.HoldsLease = true; // e.g. taken by CHK-11's lease client
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
            return Failure.FromMotion(ex);
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
            if (ctx.LeaseLost is { } lost)
            {
                // The axis has another owner: stop our beat and write nothing to it (no Stop, no Enable 0, no release).
                if (ctx.Beater.IsRunning)
                {
                    await ctx.Beater.StopAsync();
                    ctx.Journal($"C+{c.Heartbeat - c.CommandBase} (Heartbeat): stopped beating");
                }

                _log.LogWarning("Cleanup: {Lost} Nothing else is written to an axis the checker no longer owns", lost);
                return;
            }

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
