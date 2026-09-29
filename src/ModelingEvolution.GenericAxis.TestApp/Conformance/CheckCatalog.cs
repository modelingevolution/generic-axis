using System.Diagnostics;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// CHK-01…CHK-16 of <c>docs/protocol.md</c> § Conformance checks, in id order, one method per check. Ids, titles,
/// sections and needs are the table's text verbatim; a unit test compares them with the protocol file. Every failure
/// carries its class per § "Error class of a FAIL" and says what was read.
/// </summary>
internal static class CheckCatalog
{
    private const ushort Disabled = 0, Standstill = 1, Homing = 2, Discrete = 3, Continuous = 4, Stopping = 6, ErrorStop = 7;
    private static readonly ushort[] ValidStates = [Disabled, Standstill, Homing, Discrete, Continuous, Stopping, ErrorStop];
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HomingTimeout = TimeSpan.FromSeconds(120);
    private const long HaltBudgetMs = 200, TripMinMs = 1000, TripMaxMs = 1500;

    /// <summary>Checks in which <c>FaultCode 4</c> (the watchdog) is the expected outcome, not a fault to classify.</summary>
    public static readonly IReadOnlySet<string> ExpectWatchdogFault = new HashSet<string> { "CHK-08", "CHK-09", "CHK-11", "CHK-16" };

    public static IReadOnlyList<CheckDefinition> All { get; } =
    [
        new("CHK-01", "Transport and unit", "Transport", [], false, Chk01, ["connectMs", "readMs"]),
        new("CHK-02", "Map version", "Status block", ["CHK-01"], false, Chk02, ["mapVersion"]),
        new("CHK-03", "Machine limits published", "Status block, \"Limits come from the machine\"", ["CHK-02"], false, Chk03, ["travelMin", "travelMax", "maxVelocity"]),
        new("CHK-04", "Status mirror cadence", "Status block; FR-11 tick", ["CHK-02"], false, Chk04, ["reads", "slowestMs", "invalidStates"]),
        new("CHK-05", "32-bit word order and driver ownership of parameters", "Transport (word order); Command block", ["CHK-02"], false, Chk05, ["firstReadBack", "secondReadBack", "secondReadBackAfter1s"]),
        new("CHK-06", "Enable handshake (level)", "Command semantics: Handshake, Enable", ["CHK-02"], false, Chk06, ["enableAckMs", "enableStateMs", "disableAckMs", "disableStateMs"]),
        new("CHK-07", "Reset handshake (edge)", "Command semantics: Reset, Acknowledge", ["CHK-06"], false, Chk07, ["ackMs", "state", "faultCode"]),
        new("CHK-08", "Watchdog trips on a stalled beat", "FR-11", ["CHK-06"], false, Chk08, ["tripAfterMs", "watchdogTrips", "faultCode", "state"]),
        new("CHK-09", "Watchdog disarms after a trip and re-arms on clear", "FR-11", ["CHK-08"], false, Chk09, ["setupTripAfterMs", "tripsWhileLatched", "tripsWhileBeating", "secondTripAfterMs", "watchdogTrips"]),
        new("CHK-10", "Clean release disarms", "FR-11 \"Clean release disarms\"", ["CHK-08"], false, Chk10, ["tripsAfterRelease", "watchdogFault"]),
        new("CHK-11", "Advisory lease", "FR-11 \"Advisory lease\"", ["CHK-02"], false, Chk11, ["ownIdReadBack", "refusedAfterMs", "leaseOwnerAfterRefusal", "takenAfterMs"]),
        new("CHK-12", "Home", "Command semantics: Home", ["CHK-06"], true, Chk12, ["ackMs", "homedAfterMs", "faultCode"]),
        new("CHK-13", "MoveAbsolute to TravelMin + 10", "Command semantics: MoveAbsolute", ["CHK-03", "CHK-12"], true, Chk13, ["target", "ackMs", "arrivedAfterMs", "position", "positionError"]),
        new("CHK-14", "Stop mid-move", "Command semantics: Stop; FR-11 priority", ["CHK-13"], true, Chk14, ["commandedVelocity", "velocityAtStop", "ackMs", "haltMs"]),
        new("CHK-15", "MoveVelocity", "Command semantics: MoveVelocity", ["CHK-13"], true, Chk15, ["commandedVelocity", "ackMs", "maxVelocitySeen", "stopAckMs", "haltMs"]),
        new("CHK-16", "Kill test", "FR-11", ["CHK-08", "CHK-15"], true, Chk16, ["commandedVelocity", "tripAfterMs", "haltAfterTripMs", "homedAfterTrip"]),
    ];

    // ---- CHK-01 … CHK-05 ------------------------------------------------------------------------------------------

    private static async Task<CheckOutcome> Chk01(CheckContext ctx, CancellationToken ct)
    {
        var t0 = CheckContext.Now();
        await ctx.Channel.ConnectAsync(ct);
        var connectMs = CheckContext.MsSince(t0);
        var t1 = CheckContext.Now();
        await ctx.ReadStatusAsync(ct);
        var readMs = CheckContext.MsSince(t1);
        return CheckOutcome.Pass($"connected in {connectMs} ms, status block read in {readMs} ms",
            ("connectMs", connectMs), ("readMs", readMs));
    }

    private static async Task<CheckOutcome> Chk02(CheckContext ctx, CancellationToken ct)
    {
        var version = (await ctx.ReadStatusAsync(ct)).MapVersion;
        return version == RegisterMap.Version
            ? CheckOutcome.Pass($"MapVersion {version}", ("mapVersion", version))
            : CheckOutcome.Fail(Failure.Protocol(
                    $"wrong map version. Read MapVersion ({ctx.Where(ctx.Map.MapVersion)}) = {version}, expected {RegisterMap.Version}."),
                ("mapVersion", version));
    }

    private static async Task<CheckOutcome> Chk03(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        var read = $"Read TravelMin ({ctx.Where(ctx.Map.TravelMin)}) = {s.TravelMin}, TravelMax ({ctx.Where(ctx.Map.TravelMax)}) = {s.TravelMax}, "
                   + $"MaxVelocity ({ctx.Where(ctx.Map.MaxVelocity)}) = {s.MaxVelocity}";
        var failures = new List<Failure>();
        if (!s.LimitsPublished) failures.Add(Failure.Protocol($"limits not published (all 0). {read}, expected TravelMin < TravelMax and MaxVelocity > 0."));
        else if (!s.LimitsValid) failures.Add(Failure.Protocol($"limits not sane. {read}, expected TravelMin < TravelMax and MaxVelocity > 0."));
        else ctx.Limits = (s.TravelMin, s.TravelMax, s.MaxVelocity);
        return CheckOutcome.Judge(failures,
            $"travel {Words.FromRaw(s.TravelMin)}..{Words.FromRaw(s.TravelMax)}, max velocity {Words.FromRaw(s.MaxVelocity)}",
            ("travelMin", s.TravelMin), ("travelMax", s.TravelMax), ("maxVelocity", s.MaxVelocity));
    }

    private static async Task<CheckOutcome> Chk04(CheckContext ctx, CancellationToken ct)
    {
        const int reads = 30;
        var answered = 0;
        long slowest = 0;
        var invalid = new List<(ushort State, ushort Fault)>();
        var errors = new List<string>();
        var next = CheckContext.Now();
        for (var i = 0; i < reads; i++)
        {
            var t = CheckContext.Now();
            try
            {
                var s = await ctx.ReadStatusAsync(ct);
                answered++;
                if (!ValidStates.Contains(s.State) || (s.State == ErrorStop && s.FaultCode == 0)) invalid.Add((s.State, s.FaultCode));
            }
            catch (MotionException ex)
            {
                errors.Add(CheckerText.Facts(ex));
            }

            slowest = Math.Max(slowest, CheckContext.MsSince(t));
            next += Stopwatch.Frequency / 10;
            var delay = Stopwatch.GetElapsedTime(CheckContext.Now(), next);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }

        var failures = new List<Failure>();
        if (answered < reads) failures.Add(Failure.Transport($"{reads - answered} of {reads} status reads failed: {errors.First()}"));
        if (invalid.Count > 0) failures.Add(Failure.InvalidState(invalid[0].State, invalid[0].Fault, ctx.Where(ctx.Map.State)));
        if (slowest > 100) failures.Add(Failure.Protocol($"status mirror too slow: slowest round trip {slowest} ms, expected ≤ 100 ms."));
        return CheckOutcome.Judge(failures, $"{answered}/{reads} reads, slowest {slowest} ms",
            ("reads", answered), ("slowestMs", slowest), ("invalidStates", invalid.Count));
    }

    private static async Task<CheckOutcome> Chk05(CheckContext ctx, CancellationToken ct)
    {
        var address = ctx.Map.TargetPosition;
        var where = $"C+2…C+3 = {address}…{address + 1}";
        async Task<(int Value, ushort[] Words)> ReadBackAsync()
        {
            var words = await ctx.ReadAsync(address, 2, ct);
            return (Words.Read(words), words);
        }

        await ctx.WriteAsync(address, [0x0002, 0x0001], "TargetPosition = 65538", ct);
        var first = await ReadBackAsync();
        await ctx.WriteAsync(address, [0xFFFE, 0xFFFF], "TargetPosition = -2", ct);
        var second = await ReadBackAsync();
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var after = await ReadBackAsync();

        static string Hex(ushort[] w) => $"[0x{w[0]:X4}, 0x{w[1]:X4}]";
        var failures = new List<Failure>();
        if (first.Value != 65538) failures.Add(Failure.Protocol($"a written register read back different. Read TargetPosition ({where}) = {Hex(first.Words)}, expected [0x0002, 0x0001]."));
        if (second.Value != -2) failures.Add(Failure.Protocol($"a written register read back different. Read TargetPosition ({where}) = {Hex(second.Words)}, expected [0xFFFE, 0xFFFF]."));
        if (after.Value != second.Value) failures.Add(Failure.Protocol($"the PLC changed a driver-owned register within 1 s. Read TargetPosition ({where}) = {Hex(after.Words)}, expected {Hex(second.Words)}."));
        return CheckOutcome.Judge(failures, "both values read back exactly and stayed",
            ("firstReadBack", first.Value), ("secondReadBack", second.Value), ("secondReadBackAfter1s", after.Value));
    }

    // ---- CHK-06 … CHK-11 ------------------------------------------------------------------------------------------

    private static async Task<CheckOutcome> Chk06(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State is not (Disabled or Standstill)) return Precondition(ctx, s, "State 0 or 1");

        await ctx.TakeLeaseAsync(ct);
        await ctx.Beater.StartAsync(ct);

        var on = await ctx.Commands.SendAsync(CommandBits.Enable, ct);
        if (!on.Acked) return NotAcked("Enable 1", on);
        var standstill = await ctx.WaitForAsync(v => v.State == Standstill, StateTimeout, on.WrittenAt, ct);

        var off = await ctx.Commands.SendAsync(CommandBits.None, ct);
        if (!off.Acked) return NotAcked("Enable 0", off, ("enableAckMs", on.AckMs), ("enableStateMs", standstill.Met ? standstill.ElapsedMs : null));
        var disabled = await ctx.WaitForAsync(v => v.State == Disabled, StateTimeout, off.WrittenAt, ct);

        var failures = new List<Failure>();
        if (!standstill.Met) failures.Add(Failure.Machine("DriveFault", $"no Standstill 5 s after Enable 1. Read State ({ctx.Where(ctx.Map.State)}) = {standstill.View.State}, expected 1."));
        if (!disabled.Met) failures.Add(Failure.Machine("DriveFault", $"no Disabled 5 s after Enable 0. Read State ({ctx.Where(ctx.Map.State)}) = {disabled.View.State}, expected 0."));
        return CheckOutcome.Judge(failures,
            $"Enable 1 ack {on.AckMs} ms, State 1 at {standstill.ElapsedMs} ms; Enable 0 ack {off.AckMs} ms, State 0 at {disabled.ElapsedMs} ms",
            ("enableAckMs", on.AckMs), ("enableStateMs", standstill.Met ? standstill.ElapsedMs : null),
            ("disableAckMs", off.AckMs), ("disableStateMs", disabled.Met ? disabled.ElapsedMs : null));
    }

    private static async Task<CheckOutcome> Chk07(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State != Disabled) return Precondition(ctx, s, "State 0");

        var reset = await ctx.Commands.SendAsync(CommandBits.Reset, ct);
        if (!reset.Acked) return NotAcked("Reset", reset);
        var (changed, view) = await ctx.WatchAsync(v => v.State != Disabled || v.Status.FaultCode != 0, TimeSpan.FromMilliseconds(300), ct);

        var failures = new List<Failure>();
        if (changed)
            failures.Add(Failure.Protocol($"Reset outside ErrorStop changed the axis. Read State ({ctx.Where(ctx.Map.State)}) = {view.State}, "
                                          + $"FaultCode ({ctx.Where(ctx.Map.FaultCode)}) = {view.Status.FaultCode}, expected 0 and 0."));
        return CheckOutcome.Judge(failures, $"ack {reset.AckMs} ms, no-op as required",
            ("ackMs", reset.AckMs), ("state", view.State), ("faultCode", view.Status.FaultCode));
    }

    private static async Task<CheckOutcome> Chk08(CheckContext ctx, CancellationToken ct) =>
        (await StallAndTripAsync(ctx, ct)).Judge(ctx, "trip");

    private static async Task<CheckOutcome> Chk09(CheckContext ctx, CancellationToken ct)
    {
        // protocol.md: "Setup: first trip the watchdog as in CHK-08 … no trip → FAIL 'setup: no trip'".
        var setup = await StallAndTripAsync(ctx, ct);
        if (!setup.Tripped)
            return CheckOutcome.Fail(Failure.Protocol($"setup: no trip within 1.5 s of the last beat. {setup.Read(ctx)}"), ("setupTripAfterMs", null));

        // Latched: beats must not arm or count.
        var trips = setup.View.WatchdogTrips;
        await ctx.Beater.StartAsync(ct);
        var (tripWhileLatched, latchedView) = await ctx.WatchAsync(v => v.WatchdogTrips != trips, TimeSpan.FromSeconds(1), ct);
        var tripsWhileLatched = Delta(latchedView.WatchdogTrips, trips);

        // Reset edge, WatchdogFault = 0, beat 2 s: must not trip.
        var reset = await ctx.Commands.SendAsync(CommandBits.Reset, ct);
        await ctx.ClearWatchdogFaultAsync(ct);
        trips = (await ctx.ReadViewAsync(ct)).WatchdogTrips;
        var (tripWhileBeating, beatingView) = await ctx.WatchAsync(v => v.WatchdogTrips != trips || v.WatchdogFault != 0, TimeSpan.FromSeconds(2), ct);
        var tripsWhileBeating = Delta(beatingView.WatchdogTrips, trips);

        // Stop beating: the re-armed watchdog trips again.
        var second = await StopBeatAndAwaitTripAsync(ctx, trips, ct);

        var failures = new List<Failure>();
        var tripsAt = ctx.Where(ctx.Map.WatchdogTrips);
        if (tripWhileLatched) failures.Add(Failure.Protocol($"a trip was counted while WatchdogFault was latched. Read WatchdogTrips ({tripsAt}) = {latchedView.WatchdogTrips}, expected {setup.View.WatchdogTrips}."));
        if (!reset.Acked) failures.Add(Failure.NotAcknowledged("Reset", reset.Seq, reset.View.Status.CommandAck, reset.View.State));
        if (tripWhileBeating)
            failures.Add(Failure.Protocol($"tripped while beating after the clear. Read WatchdogTrips ({tripsAt}) = {beatingView.WatchdogTrips}, "
                                          + $"WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = {beatingView.WatchdogFault}, expected {trips} and 0."));
        failures.AddRange(second.Failures(ctx));
        return CheckOutcome.Judge(failures, $"no trip while latched or beating; second trip after {second.AfterMs} ms",
            ("setupTripAfterMs", setup.AfterMs), ("tripsWhileLatched", tripsWhileLatched), ("tripsWhileBeating", tripsWhileBeating),
            ("secondTripAfterMs", second.Tripped ? second.AfterMs : null), ("watchdogTrips", second.View.WatchdogTrips));
    }

    private static async Task<CheckOutcome> Chk10(CheckContext ctx, CancellationToken ct)
    {
        await ctx.TakeLeaseAsync(ct);
        await ctx.Beater.StartAsync(ct);
        var trips = (await ctx.ReadViewAsync(ct)).WatchdogTrips;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await ctx.ReleaseLeaseAsync(ct);
        await ctx.Beater.StopAsync();
        var (tripped, view) = await ctx.WatchAsync(v => v.WatchdogFault != 0 || v.WatchdogTrips != trips, TimeSpan.FromSeconds(2), ct);
        if (tripped) ctx.CausedTrip = true;

        var delta = Delta(view.WatchdogTrips, trips);
        return tripped
            ? CheckOutcome.Fail(Failure.Protocol($"tripped after a clean release (LeaseOwner = 0). Read WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = "
                                                 + $"{view.WatchdogFault}, WatchdogTrips ({ctx.Where(ctx.Map.WatchdogTrips)}) = {view.WatchdogTrips}, expected 0 and {trips}."),
                ("tripsAfterRelease", delta), ("watchdogFault", view.WatchdogFault))
            : CheckOutcome.Pass("no trip 2 s after LeaseOwner = 0", ("tripsAfterRelease", delta), ("watchdogFault", view.WatchdogFault));
    }

    private static async Task<CheckOutcome> Chk11(CheckContext ctx, CancellationToken ct)
    {
        var failures = new List<Failure>();
        var interval = Beater.Period;
        var ownerAt = ctx.Where(ctx.Map.LeaseOwner);

        // (a) Unowned → the driver's lease client takes it, then releases.
        await ctx.Beater.StopAsync();
        await ctx.ReleaseLeaseAsync(ct);
        var clientA = LeaseClient(ctx, interval);
        await clientA.AcquireAsync(TimeSpan.FromSeconds(3), ct);
        ctx.TookLease = true;
        var aOwner = (await ctx.ReadViewAsync(ct)).LeaseOwner;
        if (aOwner != ctx.Options.OwnerId) failures.Add(Failure.Protocol($"(a) the lease write did not hold. Read LeaseOwner ({ownerAt}) = {aOwner}, expected {ctx.Options.OwnerId}."));
        await clientA.StopAsync(); // releases: writes 0 over its own id

        // (b) A live incumbent 65534 → refused with LeaseHeld after the 3 s timeout.
        await ctx.WriteAsync(ctx.Map.LeaseOwner, CheckerOptions.ForeignOwnerId, "impersonate incumbent 65534", ct);
        var incumbent = new Beater(ctx, $"incumbent {CheckerOptions.ForeignOwnerId}");
        await incumbent.StartAsync(ct);
        long? refusedMs = null;
        var t = CheckContext.Now();
        try
        {
            await LeaseClient(ctx, interval).AcquireAsync(TimeSpan.FromSeconds(3), ct);
            ctx.TookLease = true;
            failures.Add(Failure.Protocol($"(b) the lease client took the lease from a live incumbent. Read LeaseOwner ({ownerAt}) = {ctx.Options.OwnerId}, expected {CheckerOptions.ForeignOwnerId}."));
        }
        catch (MotionException ex) when (ex.Error == MotionError.LeaseHeld)
        {
            refusedMs = CheckContext.MsSince(t);
            if (!ex.Message.Contains(CheckerOptions.ForeignOwnerId.ToString(), StringComparison.Ordinal))
                failures.Add(Failure.Protocol($"(b) LeaseHeld does not name {CheckerOptions.ForeignOwnerId}: {ex.Message}"));
        }

        var bOwner = (await ctx.ReadViewAsync(ct)).LeaseOwner;
        if (bOwner != CheckerOptions.ForeignOwnerId) failures.Add(Failure.Protocol($"(b) the incumbent's lease did not hold. Read LeaseOwner ({ownerAt}) = {bOwner}, expected {CheckerOptions.ForeignOwnerId}."));

        // (c) The incumbent dies while the client watches → taken within 2 s of its last beat.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));
        var acquire = LeaseClient(ctx, interval).AcquireAsync(null, budget.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        await incumbent.StopAsync();
        ctx.CausedTrip = true; // 65534 held the lease and beat, so the PLC armed; its death trips the watchdog
        long? takenMs = null;
        try
        {
            await acquire;
            ctx.TookLease = true;
            takenMs = (long)Stopwatch.GetElapsedTime(incumbent.LastBeatAt).TotalMilliseconds;
            if (takenMs > 2000) failures.Add(Failure.Protocol($"(c) lease taken {takenMs} ms after the incumbent's last beat, expected ≤ 2000 ms."));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var v = await ctx.ReadViewAsync(ct);
            failures.Add(Failure.Protocol($"(c) the lease was not taken within 6 s of the incumbent's last beat (Heartbeat still changing?). "
                                          + $"Read Heartbeat ({ctx.Where(ctx.Map.Heartbeat)}) = {v.Heartbeat}, LeaseOwner ({ownerAt}) = {v.LeaseOwner}, expected {ctx.Options.OwnerId}."));
        }

        return CheckOutcome.Judge(failures, $"(a) read back {aOwner}; (b) refused after {refusedMs?.ToString() ?? "—"} ms; (c) taken {takenMs?.ToString() ?? "—"} ms after the last beat",
            ("ownIdReadBack", aOwner), ("refusedAfterMs", refusedMs), ("leaseOwnerAfterRefusal", bOwner), ("takenAfterMs", takenMs));
    }

    // ---- CHK-12 … CHK-16: motion (only with --allow-motion) -------------------------------------------------------

    private static async Task<CheckOutcome> Chk12(CheckContext ctx, CancellationToken ct)
    {
        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);

        var home = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Home, ct);
        if (!home.Acked) return NotAcked("Home", home);
        var done = await ctx.WaitForAsync(v => (v.State == Standstill && v.Status.Homed) || v.State == ErrorStop, HomingTimeout, home.WrittenAt, ct);
        var s = done.View.Status;

        var failures = new List<Failure>();
        if (home.View.State != Homing) failures.Add(AckWithoutState(ctx, "Home", home, Homing));
        if (!done.Met) failures.Add(Failure.Machine("HomeLatchFailed", $"not homed within 120 s. Read State ({ctx.Where(ctx.Map.State)}) = {s.State}, Flags.Homed = {(s.Homed ? 1 : 0)}, expected 1 and 1."));
        return CheckOutcome.Judge(failures, $"homed in {done.ElapsedMs} ms, ActualPosition {Words.FromRaw(s.ActualPosition)}",
            ("ackMs", home.AckMs), ("homedAfterMs", done.Met && s.Homed && s.State == Standstill ? done.ElapsedMs : null), ("faultCode", s.FaultCode));
    }

    private static async Task<CheckOutcome> Chk13(CheckContext ctx, CancellationToken ct)
    {
        var (min, _, max) = ctx.Limits ?? throw new InvalidOperationException("CHK-03 passed without recording the limits");
        var target = checked(min + 10_000);
        var speed = Math.Max(1, max / 10);

        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);
        var start = (await ctx.ReadStatusAsync(ct)).ActualPosition;

        await ctx.Commands.WriteParametersAsync(target, speed, 0, ct);
        var move = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.MoveAbsolute, ct);
        if (!move.Acked) return NotAcked("MoveAbsolute", move, ("target", target));

        var budget = TimeSpan.FromSeconds(Math.Abs((double)target - start) / speed + 10);
        var done = await ctx.WaitForAsync(v => (v.State == Standstill && v.Status.InPosition) || v.State == ErrorStop, budget, move.WrittenAt, ct);
        var s = done.View.Status;
        var error = Math.Abs((long)s.ActualPosition - target);
        var tolerance = (long)Math.Round(ctx.Options.Tolerance * Words.Scale);

        var failures = new List<Failure>();
        if (move.View.State != Discrete) failures.Add(AckWithoutState(ctx, "MoveAbsolute", move, Discrete));
        if (!done.Met)
            failures.Add(Failure.Machine("MotionFailed", $"not arrived within {budget.TotalSeconds:F0} s. Read State ({ctx.Where(ctx.Map.State)}) = {s.State}, "
                                                         + $"Flags.InPosition = {(s.InPosition ? 1 : 0)}, ActualPosition ({ctx.Where(ctx.Map.ActualPosition)}) = {s.ActualPosition}, expected 1, 1, {target}."));
        else if (s.State == Standstill && error > tolerance)
            failures.Add(Failure.Machine("MotionFailed", $"stopped outside --tolerance {ctx.Options.Tolerance}. Read ActualPosition ({ctx.Where(ctx.Map.ActualPosition)}) = {s.ActualPosition}, expected {target} ± {tolerance}."));
        return CheckOutcome.Judge(failures, $"arrived in {done.ElapsedMs} ms, error {Words.FromRaw((int)error)}",
            ("target", target), ("ackMs", move.AckMs), ("arrivedAfterMs", done.Met && s.State == Standstill ? done.ElapsedMs : null),
            ("position", done.Met && s.State == Standstill ? s.ActualPosition : null), ("positionError", done.Met && s.State == Standstill ? error : null));
    }

    private static async Task<CheckOutcome> Chk14(CheckContext ctx, CancellationToken ct)
    {
        var (min, travelMax, max) = ctx.Limits!.Value;
        var target = checked((int)(min + ((long)travelMax - min) / 2));
        var speed = Math.Max(1, max / 10);

        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);
        await ctx.Commands.WriteParametersAsync(target, speed, 0, ct);
        var move = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.MoveAbsolute, ct);
        if (!move.Acked) return NotAcked("MoveAbsolute", move, ("commandedVelocity", speed));

        var cruising = await ctx.WaitForAsync(v => Math.Abs((long)v.Status.ActualVelocity) * 10 >= speed * 9L || v.State == ErrorStop,
            TimeSpan.FromSeconds(2), move.WrittenAt, ct);
        var velocityAtStop = cruising.View.Status.ActualVelocity;

        var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
        if (!stop.Acked) return NotAcked("Stop", stop, ("commandedVelocity", speed), ("velocityAtStop", velocityAtStop));
        var halt = await ctx.WaitForAsync(v => v.Status.ActualVelocity == 0 && v.State == Standstill, TimeSpan.FromSeconds(1), stop.WrittenAt, ct);

        var failures = new List<Failure>();
        if (move.View.State != Discrete) failures.Add(AckWithoutState(ctx, "MoveAbsolute", move, Discrete));
        if (!halt.Met || halt.ElapsedMs > HaltBudgetMs)
            failures.Add(Failure.Machine("MotionFailed", $"still moving {halt.ElapsedMs} ms after Stop. Read ActualVelocity ({ctx.Where(ctx.Map.ActualVelocity)}) = "
                                                         + $"{halt.View.Status.ActualVelocity}, State ({ctx.Where(ctx.Map.State)}) = {halt.View.State}, expected 0 and 1 within 200 ms."));
        return CheckOutcome.Judge(failures, $"Stop ack {stop.AckMs} ms, halted {halt.ElapsedMs} ms after the write",
            ("commandedVelocity", speed), ("velocityAtStop", velocityAtStop), ("ackMs", stop.AckMs), ("haltMs", halt.Met ? halt.ElapsedMs : null));
    }

    private static async Task<CheckOutcome> Chk15(CheckContext ctx, CancellationToken ct)
    {
        var (_, _, max) = ctx.Limits!.Value;
        var speed = Math.Max(1, max / 100);

        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);
        var here = (await ctx.ReadStatusAsync(ct)).ActualPosition;
        await ctx.Commands.WriteParametersAsync(here, speed, 0, ct);
        var jog = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.MoveVelocity, ct);
        if (!jog.Acked) return NotAcked("MoveVelocity", jog, ("commandedVelocity", speed));

        var fastest = 0;
        var left = await ctx.WaitForAsync(v => { fastest = Math.Max(fastest, v.Status.ActualVelocity); return v.State != Continuous; },
            TimeSpan.FromSeconds(1), CheckContext.Now(), ct);

        var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
        if (!stop.Acked) return NotAcked("Stop", stop, ("commandedVelocity", speed), ("ackMs", jog.AckMs), ("maxVelocitySeen", fastest));
        var halt = await ctx.WaitForAsync(v => v.State == Standstill, TimeSpan.FromSeconds(1), stop.WrittenAt, ct);

        var failures = new List<Failure>();
        var stateAt = ctx.Where(ctx.Map.State);
        if (jog.View.State != Continuous) failures.Add(AckWithoutState(ctx, "MoveVelocity", jog, Continuous));
        if (left.Met) failures.Add(Failure.Machine("MotionFailed", $"left ContinuousMotion during the 1 s run. Read State ({stateAt}) = {left.View.State}, expected 4."));
        if (fastest <= 0) failures.Add(Failure.Machine("MotionFailed", $"no velocity during the run. Read ActualVelocity ({ctx.Where(ctx.Map.ActualVelocity)}) ≤ 0 throughout, expected > 0."));
        if (!halt.Met || halt.ElapsedMs > HaltBudgetMs)
            failures.Add(Failure.Machine("MotionFailed", $"no Standstill {halt.ElapsedMs} ms after Stop. Read State ({stateAt}) = {halt.View.State}, expected 1 within 200 ms."));
        return CheckOutcome.Judge(failures, $"ran at up to {Words.FromRaw(fastest)}, stopped in {halt.ElapsedMs} ms",
            ("commandedVelocity", speed), ("ackMs", jog.AckMs), ("maxVelocitySeen", fastest), ("stopAckMs", stop.AckMs), ("haltMs", halt.Met ? halt.ElapsedMs : null));
    }

    private static async Task<CheckOutcome> Chk16(CheckContext ctx, CancellationToken ct)
    {
        var (_, _, max) = ctx.Limits!.Value;
        var speed = Math.Max(1, max / 100);

        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);
        var here = (await ctx.ReadStatusAsync(ct)).ActualPosition;
        await ctx.Commands.WriteParametersAsync(here, speed, 0, ct);
        var jog = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.MoveVelocity, ct);
        if (!jog.Acked) return NotAcked("MoveVelocity", jog, ("commandedVelocity", speed));
        var moving = await ctx.WaitForAsync(v => v.Status.ActualVelocity > 0, TimeSpan.FromSeconds(1), jog.WrittenAt, ct);
        if (!moving.Met)
            return CheckOutcome.Fail(Failure.Machine("MotionFailed", $"the jog never moved. Read ActualVelocity ({ctx.Where(ctx.Map.ActualVelocity)}) = "
                                                                     + $"{moving.View.Status.ActualVelocity}, State = {moving.View.State}, expected > 0 and 4."), ("commandedVelocity", speed));

        // The commander dies: the beat stops, the connection stays open, polling continues.
        await ctx.Beater.StopAsync();
        ctx.CausedTrip = true;
        var last = ctx.Beater.LastBeatAt;
        var trip = await ctx.WaitForAsync(v => v.Status.FaultCode == 4 && v.State == ErrorStop, TimeSpan.FromSeconds(3), last, ct);
        var tripSeenAt = CheckContext.Now();
        var halt = trip.View.Status.ActualVelocity == 0
            ? new Wait(trip.View, 0, true)
            : await ctx.WaitForAsync(v => v.Status.ActualVelocity == 0, TimeSpan.FromSeconds(1), tripSeenAt, ct);
        var homed = trip.View.Status.Homed;

        var failures = new List<Failure>();
        var faultAt = ctx.Where(ctx.Map.FaultCode);
        if (jog.View.State != Continuous) failures.Add(AckWithoutState(ctx, "MoveVelocity", jog, Continuous));
        if (!trip.Met)
            failures.Add(Failure.Protocol($"no trip within 1.5 s of the last beat. Read FaultCode ({faultAt}) = {trip.View.Status.FaultCode}, "
                                          + $"State ({ctx.Where(ctx.Map.State)}) = {trip.View.State} after 3 s, expected 4 and 7."));
        else if (trip.ElapsedMs is < TripMinMs or > TripMaxMs)
            failures.Add(Failure.Protocol($"tripped {trip.ElapsedMs} ms after the last beat, expected 1000–1500 ms. Read FaultCode ({faultAt}) = 4."));
        if (trip.Met && (!halt.Met || halt.ElapsedMs > HaltBudgetMs))
            failures.Add(Failure.Machine("MotionFailed", $"still moving {halt.ElapsedMs} ms after the trip. Read ActualVelocity ({ctx.Where(ctx.Map.ActualVelocity)}) = {halt.View.Status.ActualVelocity}, expected 0 within 200 ms."));
        if (trip.Met && !homed) failures.Add(Failure.Protocol($"the trip cleared Homed. Read Flags ({ctx.Where(ctx.Map.Flags)}) = 0x{(ushort)trip.View.Status.Flags:X4}, expected bit 0 set."));
        return CheckOutcome.Judge(failures, $"trip after {trip.ElapsedMs} ms, halted {halt.ElapsedMs} ms later, Homed kept",
            ("commandedVelocity", speed), ("tripAfterMs", trip.Met ? trip.ElapsedMs : null), ("haltAfterTripMs", trip.Met && halt.Met ? halt.ElapsedMs : null),
            ("homedAfterTrip", trip.Met ? (homed ? 1 : 0) : null));
    }

    // ---- shared steps ---------------------------------------------------------------------------------------------

    private static AxisHeartbeat LeaseClient(CheckContext ctx, TimeSpan interval) =>
        new("conformance", ctx.Channel, ctx.Unit, ctx.Map, ctx.Options.OwnerId, interval, ctx.Logger);

    private static CheckOutcome NotAcked(string what, Ack ack, params (string, long?)[] observed) =>
        CheckOutcome.Fail(Failure.NotAcknowledged(what, ack.Seq, ack.View.Status.CommandAck, ack.View.State), observed);

    private static Failure AckWithoutState(CheckContext ctx, string what, Ack ack, ushort expected) =>
        Failure.Protocol($"{what} was acknowledged without entering its state (the ack must land in the scan that enters it). "
                         + $"Read CommandAck ({ctx.Where(ctx.Map.CommandAck)}) = {ack.Seq}, State ({ctx.Where(ctx.Map.State)}) = {ack.View.State}, expected {expected}.");

    private static CheckOutcome Precondition(CheckContext ctx, StatusBlock s, string expected) =>
        CheckOutcome.Fail(s.State == ErrorStop && s.FaultCode != 0
            ? Failure.Fault(s.FaultCode, ctx.Where(ctx.Map.FaultCode))
            : Failure.Machine("MotionFailed", $"precondition: the axis is not at rest. Read State ({ctx.Where(ctx.Map.State)}) = {s.State}, expected {expected}."));

    private static long Delta(ushort now, ushort before) => (ushort)(now - before);

    /// <summary>Enable when Disabled and wait for Standstill; the failure when that fails.</summary>
    private static async Task<Failure?> EnsureEnabledAsync(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State == Standstill) return null;
        if (s.State != Disabled) return Precondition(ctx, s, "0 or 1").Failures[0];
        var on = await ctx.Commands.SendAsync(CommandBits.Enable, ct);
        if (!on.Acked) return Failure.NotAcknowledged("Enable 1", on.Seq, on.View.Status.CommandAck, on.View.State);
        var w = await ctx.WaitForAsync(v => v.State == Standstill, StateTimeout, on.WrittenAt, ct);
        return w.Met ? null : Failure.Machine("DriveFault", $"no Standstill 5 s after Enable 1. Read State ({ctx.Where(ctx.Map.State)}) = {w.View.State}, expected 1.");
    }

    /// <summary>Hold the lease, WatchdogFault = 0, beat 2 s, stop beating, watch for the trip (CHK-08, and CHK-09's setup).</summary>
    private static async Task<Trip> StallAndTripAsync(CheckContext ctx, CancellationToken ct)
    {
        await ctx.TakeLeaseAsync(ct);
        await ctx.ClearWatchdogFaultAsync(ct);
        await ctx.Beater.StopAsync();
        await ctx.Beater.StartAsync(ct); // a fresh beat after the clear arms the watchdog
        var trips = (await ctx.ReadViewAsync(ct)).WatchdogTrips;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return await StopBeatAndAwaitTripAsync(ctx, trips, ct);
    }

    private static async Task<Trip> StopBeatAndAwaitTripAsync(CheckContext ctx, ushort tripsBefore, CancellationToken ct)
    {
        await ctx.Beater.StopAsync();
        var last = ctx.Beater.LastBeatAt;
        var sign = await ctx.WaitForAsync(
            v => v.WatchdogFault != 0 || v.WatchdogTrips != tripsBefore || (v.State == ErrorStop && v.Status.FaultCode == 4),
            TimeSpan.FromSeconds(3), last, ct);
        if (!sign.Met) return new Trip(false, sign.ElapsedMs, sign.View, tripsBefore);

        ctx.CausedTrip = true;
        // The trip actions happen in one scan; the view spans two reads, so judge a fresh view taken after the first sign.
        var settled = await ctx.ReadViewAsync(ct);
        return new Trip(true, sign.ElapsedMs, settled, tripsBefore);
    }

    private readonly record struct Trip(bool Tripped, long AfterMs, PlcView View, ushort TripsBefore)
    {
        public string Read(CheckContext ctx) =>
            $"Read WatchdogFault ({ctx.Where(ctx.Map.WatchdogFault)}) = {View.WatchdogFault}, WatchdogTrips ({ctx.Where(ctx.Map.WatchdogTrips)}) = {View.WatchdogTrips}, "
            + $"State ({ctx.Where(ctx.Map.State)}) = {View.State}, FaultCode ({ctx.Where(ctx.Map.FaultCode)}) = {View.Status.FaultCode}, "
            + $"expected 1, {(ushort)(TripsBefore + 1)}, 7, 4.";

        public IEnumerable<Failure> Failures(CheckContext ctx)
        {
            if (!Tripped)
            {
                yield return Failure.Protocol($"no trip within 1.5 s of the last beat. {Read(ctx)}");
                yield break;
            }

            if (AfterMs < TripMinMs) yield return Failure.Protocol($"tripped {AfterMs} ms after the last beat, before 1.0 s. {Read(ctx)}");
            else if (AfterMs > TripMaxMs) yield return Failure.Protocol($"no trip within 1.5 s of the last beat (tripped after {AfterMs} ms). {Read(ctx)}");
            if (View.WatchdogFault != 1 || Delta(View.WatchdogTrips, TripsBefore) != 1 || View.State != ErrorStop || View.Status.FaultCode != 4)
                yield return Failure.Protocol($"incomplete trip actions. {Read(ctx)}");
        }

        public (string, long?)[] Observed(string afterKey) =>
            [(afterKey, Tripped ? AfterMs : null), ("watchdogTrips", View.WatchdogTrips), ("faultCode", View.Status.FaultCode), ("state", View.State)];

        public CheckOutcome Judge(CheckContext ctx, string what) => CheckOutcome.Judge(Failures(ctx), $"{what} after {AfterMs} ms", Observed("tripAfterMs"));
    }
}
