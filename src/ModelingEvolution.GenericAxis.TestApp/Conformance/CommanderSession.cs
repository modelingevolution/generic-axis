using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// What every run of a conformance tool does around its work (protocol § Rules for every run): the pre-flight and the
/// cleanup. One implementation for the checklist (<see cref="ConformanceRunner"/>) and the one-verb mode
/// (<see cref="CommandRunner"/>).
/// </summary>
internal static class CommanderSession
{
    private static readonly TimeSpan PreflightWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// protocol § Rules, Pre-flight. Before its own first beat, reads <c>LeaseOwner</c> and <c>WatchdogFault</c> and watches
    /// <c>Heartbeat</c> (C+8): 1 s, or 1.6 s while <c>LeaseOwner ≠ 0</c> (review #35: a commander silent for 1 s may still
    /// be alive, the watchdog trips only 1.0–1.5 s after its last beat). Any beat → refused. A held lease with
    /// <c>WatchdogFault = 1</c> and no beat → its holder is dead: proceed with a note, never clearing that trip. A held
    /// lease with neither within 1.6 s → refused.
    /// </summary>
    /// <param name="ctx">The run's context; nothing is written.</param>
    /// <param name="log">The run's log.</param>
    /// <param name="ct">Ctrl-C.</param>
    /// <param name="deadHolder">What the caller does about a dead holder's trip, for the note (#62): the checklist leaves
    /// it for its operator (protocol wording). Null: the note states only what was seen (#64) — the one-verb mode may yet
    /// refuse by a guard and write nothing, and reports the clear on its own line when it happens.</param>
    public static async Task<Preflight> PreflightAsync(CheckContext ctx, ILogger log, CancellationToken ct,
        string? deadHolder = "its trip is left for its operator.")
    {
        try
        {
            await ctx.Channel.ConnectAsync(ct);
            var first = await ReadBeatOwnerFaultAsync(ctx, ct);
            var beats = new List<ushort> { first.Beat };
            var (owner, fault) = (first.Owner, first.Fault);
            var watched = Stopwatch.StartNew();
            // At least the full 1 s is always watched: a beat at any time refuses, and WatchdogFault = 1 is evidence of a
            // dead holder only together with a Heartbeat silent for that whole second (Python review #25). A held lease
            // that neither beats nor shows a trip is watched on to 1.6 s.
            while (watched.Elapsed < PreflightWindow || (beats.Count == 1 && owner != 0 && fault == 0 && watched.Elapsed < HeldLeaseWindow))
            {
                await Task.Delay(Beater.Period, ct);
                var now = await ReadBeatOwnerFaultAsync(ctx, ct);
                if (now.Beat != beats[^1]) beats.Add(now.Beat);
                (owner, fault) = (now.Owner, now.Fault);
            }

            var ownerAt = $"LeaseOwner ({ctx.At(RegisterField.LeaseOwner)}) = {owner}";
            var faultAt = $"WatchdogFault ({ctx.At(RegisterField.WatchdogFault)}) = {fault}";
            log.LogInformation("Pre-flight after {Ms} ms: {Owner}, {Fault}, Heartbeat {Beats}",
                (long)watched.Elapsed.TotalMilliseconds, ownerAt, faultAt, string.Join(" → ", beats));
            if (beats.Count > 1)
                return new Preflight(
                    $"refused to start: another commander is beating — Heartbeat ({ctx.At(RegisterField.Heartbeat)}) read {string.Join(" → ", beats.Take(6))}"
                    + $"{(beats.Count > 6 ? " …" : "")} within {watched.Elapsed.TotalSeconds:0.0} s, {ownerAt}; stop it first", null);
            if (owner == 0) return Preflight.Free;
            if (fault != 0)
            {
                ctx.ForeignTrip = true;
                return new Preflight(null,
                    deadHolder is null
                        ? $"{ownerAt} held with no beat for {watched.Elapsed.TotalSeconds:0.0} s and {faultAt}: the previous commander is dead."
                        : $"{ownerAt} held with no beat and {faultAt}: the previous commander is dead; {deadHolder}");
            }

            return new Preflight(
                $"refused to start: {ownerAt} is held and {faultAt}: no beat and no trip within {HeldLeaseWindow.TotalSeconds:0.0} s — "
                + "a live commander, or a PLC without a working watchdog; release LeaseOwner by hand only if no commander runs", null);
        }
        catch (MotionException ex)
        {
            // Python #4 mirror (lead ruling): a pre-flight that could not prove the axis free must not proceed.
            log.LogWarning("Pre-flight could not read the PLC ({Message}); CHK-01 FAILs and nothing is written", CheckerText.Describe(ex));
            return new Preflight(null, null, ex);
        }
    }

    /// <summary>
    /// The pre-flight verdict: a refusal (exit 3), a note for the report when the run proceeds, or the read that failed
    /// (the axis was never proved free: CHK-01 FAILs, nothing more runs).
    /// </summary>
    internal sealed record Preflight(string? Refusal, string? Note, MotionException? Unreadable = null)
    {
        public static Preflight Free { get; } = new(null, null);
    }

    /// <summary>1.5 s (the latest trip, FR-11) plus one 100 ms read.</summary>
    private static readonly TimeSpan HeldLeaseWindow = TimeSpan.FromMilliseconds(1600);

    private static async Task<(ushort Beat, ushort Owner, ushort Fault)> ReadBeatOwnerFaultAsync(CheckContext ctx, CancellationToken ct)
    {
        var words = await ctx.Channel.ReadHoldingAsync(ctx.Unit, ctx.Map.Heartbeat, 3, "pre-flight: Heartbeat, LeaseOwner, WatchdogFault",
            ChannelPriority.Move, ct);
        return (words[0], words[1], words[2]);
    }

    /// <summary>
    /// Always: Stop edge if State is 2, 3 or 4 · clear edge bits and Enable 0 · stop beating · <c>WatchdogFault = 0</c> if the
    /// checker caused a trip · <c>LeaseOwner = 0</c> if it holds the checker's id. Nothing is written that the checker did
    /// not change. Each write is journalled for the report. <paramref name="disableOnExit"/>: write Enable 0 (the checker
    /// always; one-verb mode only when this run set Enable 1); otherwise only edge bits left set are cleared.
    /// </summary>
    public static async Task CleanupAsync(CheckContext ctx, ILogger log, bool disableOnExit)
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

                log.LogWarning("Cleanup: {Lost} Nothing else is written to an axis the checker no longer owns", lost);
                return;
            }

            if (!ctx.Commands.Used && !ctx.TookLease && !ctx.Beater.IsRunning && !ctx.CausedTrip)
            {
                log.LogInformation("Cleanup: the checker wrote no command, lease or beat — nothing to undo");
                return;
            }

            var v = await ctx.ReadViewAsync(ct);
            if (v.State is 2 or 3 or 4)
            {
                var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
                ctx.Journal($"C+0 = 0x{(ushort)(CommandBits.Enable | CommandBits.Stop):X4} (Stop), CommandSeq {stop.Seq}");
                await ctx.WaitForAsync(x => x.State is not (2 or 3 or 4 or 6), TimeSpan.FromSeconds(2), CheckContext.Now(), ct);
            }

            if (disableOnExit)
            {
                var off = await ctx.Commands.SendAsync(CommandBits.None, ct, ChannelPriority.Stop);
                ctx.Journal($"C+0 = 0x0000 (clear edge bits, Enable 0), CommandSeq {off.Seq}");
            }
            else if (ctx.Commands.Used && await ctx.Commands.ClearEdgesAsync(ct) is { } cleared)
            {
                ctx.Journal(cleared);
            }

            // protocol § Rules, Cleanup (review #52): the beat continues through cleanup and stops just before
            // LeaseOwner = 0, so no unbeaten frames (a read, a clear) can outlast the 1 s stall on a starved host.
            v = await ctx.ReadViewAsync(ct);
            if (ctx.CausedTrip && v.WatchdogFault != 0)
            {
                await ctx.ClearWatchdogFaultAsync(ct);
                ctx.Journal($"C+{c.WatchdogFault - c.CommandBase} = 0 (clear the WatchdogFault the checker caused)");
            }

            if (ctx.Beater.IsRunning)
            {
                await ctx.Beater.StopAsync();
                ctx.Journal($"C+{c.Heartbeat - c.CommandBase} (Heartbeat): stopped beating");
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
            log.LogError(ex, "Cleanup failed; check the PLC by hand (Enable, LeaseOwner, WatchdogFault)");
        }
    }
}
