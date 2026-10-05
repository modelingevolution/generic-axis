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

    private readonly ILogger _log = loggerFactory.CreateLogger<ConformanceRunner>();

    /// <summary>The tool's version for the report.</summary>
    public static string ToolVersion { get; } =
        typeof(ConformanceRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Runs the whole checklist. <paramref name="progress"/> receives an immutable report after every step.</summary>
    public Task<ConformanceReport> RunAsync(CheckerOptions options, CancellationToken ct, Action<ConformanceReport>? progress = null) =>
        RunAsync(options, new ModbusChannel(options.Host, options.Port, loggerFactory.CreateLogger<ModbusChannel>(), map: options.Map), ct, progress);

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
                var preflight = await CommanderSession.PreflightAsync(ctx, _log, ct);
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
                        var mapVersion = (await ctx.ReadInputAsync(ctx.Map.MapVersion, 1, ct))[0];
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
                        _log.LogWarning("Taking the lease after pre-flight failed ({Message}); CHK-01 reports it under its own class", CheckerText.Describe(ex));
                    }
                }

                await RunChecksAsync(ctx, options, results, Publish, ct);
            }
        }
        finally
        {
            await CommanderSession.CleanupAsync(ctx, _log, disableOnExit: ctx.Commands.Used);
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
            else if (def.Restores && outcome.Result != CheckResultKind.Skipped) // a check that skipped itself wrote nothing
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

            // protocol § Observed values: a SKIPPED check has observed {} — also one that skipped itself (Speed rounding).
            outcome = outcome with { Observed = outcome.Result == CheckResultKind.Skipped ? [] : Canonical(def, outcome, ctx.RetriesSince(retriesBefore)) };
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
        var where = ctx.At(RegisterField.State);
        if (state is 5 or > 7 || (state == 7 && fault == 0)) yield return Failure.InvalidState((ushort)state, fault, where);
        else if (state == 7 && !(fault == 4 && CheckCatalog.ExpectWatchdogFault.Contains(def.Id)))
            yield return Failure.Fault(fault, ctx.At(RegisterField.FaultCode));
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
    /// Every check from CHK-06 on ends in State 0 or 1, no latched fault, the lease held and the beat running.
    /// Returns null when restored, else the reason.
    /// </summary>
    private async Task<Failure?> RestoreAsync(CheckContext ctx, CancellationToken ct)
    {
        try
        {
            var v = await ctx.ReadViewAsync(ct);
            if (ctx.ForeignTrip && !ctx.CausedTrip && v.WatchdogFault != 0)
                return Failure.Fault(4, ctx.At(RegisterField.FaultCode)) with
                {
                    Text = $"the previous commander's watchdog trip is left for its operator. Read WatchdogFault ({ctx.At(RegisterField.WatchdogFault)}) = "
                           + $"{v.WatchdogFault}, LeaseOwner ({ctx.At(RegisterField.LeaseOwner)}) = {v.LeaseOwner}; the checker does not clear it.",
                };

            if (v.State is 2 or 3 or 4)
            {
                var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
                if (!stop.Acked) return Failure.NotAcknowledged("Stop", stop.Seq, stop.View.Status.CommandAck, stop.View.State);
            }

            if (v.State is 2 or 3 or 4 or 6)
            {
                var still = await ctx.WaitForAsync(x => x.State is not (2 or 3 or 4 or 6), TimeSpan.FromSeconds(5), CheckContext.Now(), ct);
                if (!still.Met) return Failure.Machine("MotionFailed", $"still moving 5 s after Stop. Read State ({ctx.At(RegisterField.State)}) = {still.View.State}, expected 0 or 1.");
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
                if (!w.Met) return Failure.Fault(w.View.Status.FaultCode, ctx.At(RegisterField.FaultCode)) with { Text = $"still in ErrorStop 5 s after Reset. Read FaultCode ({ctx.At(RegisterField.FaultCode)}) = {w.View.Status.FaultCode}." };
            }

            var end = await ctx.ReadViewAsync(ct);
            if (end.State is not (0 or 1)) return Failure.Machine("MotionFailed", $"Read State ({ctx.At(RegisterField.State)}) = {end.State}, expected 0 or 1.");
            if (end.Status.FaultCode != 0) return Failure.Fault(end.Status.FaultCode, ctx.At(RegisterField.FaultCode));
            if (end.WatchdogFault != 0) return Failure.Protocol($"WatchdogFault did not clear. Read WatchdogFault ({ctx.At(RegisterField.WatchdogFault)}) = {end.WatchdogFault}, expected 0.");
            if (end.LeaseOwner != ctx.Options.OwnerId) return Failure.Protocol($"the lease write did not hold. Read LeaseOwner ({ctx.At(RegisterField.LeaseOwner)}) = {end.LeaseOwner}, expected {ctx.Options.OwnerId}.");
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

}
