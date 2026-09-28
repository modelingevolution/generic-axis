using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// CHK-01…CHK-16 of <c>docs/protocol.md</c> § Conformance checks, in id order, one method per check. Ids, titles,
/// sections and needs are the table's text verbatim; a unit test compares them with the protocol file.
/// </summary>
internal static class CheckCatalog
{
    private const ushort Disabled = 0, Standstill = 1, Homing = 2, Discrete = 3, Continuous = 4, Stopping = 6, ErrorStop = 7;
    private static readonly ushort[] ValidStates = [Disabled, Standstill, Homing, Discrete, Continuous, Stopping, ErrorStop];
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HomingTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan HaltBudget = TimeSpan.FromMilliseconds(200);
    private const long TripMinMs = 1000, TripMaxMs = 1500;

    public static IReadOnlyList<CheckDefinition> All { get; } =
    [
        new("CHK-01", "Transport and unit", "Transport", [], false, Chk01),
        new("CHK-02", "Map version", "Status block", ["CHK-01"], false, Chk02),
        new("CHK-03", "Machine limits published", "Status block, \"Limits come from the machine\"", ["CHK-02"], false, Chk03),
        new("CHK-04", "Status mirror cadence", "Status block; FR-11 tick", ["CHK-02"], false, Chk04),
        new("CHK-05", "32-bit word order and driver ownership of parameters", "Transport (word order); Command block", ["CHK-02"], false, Chk05),
        new("CHK-06", "Enable handshake (level)", "Command semantics: Handshake, Enable", ["CHK-02"], false, Chk06),
        new("CHK-07", "Reset handshake (edge)", "Command semantics: Reset, Acknowledge", ["CHK-06"], false, Chk07),
        new("CHK-08", "Watchdog trips on a stalled beat", "FR-11", ["CHK-06"], false, Chk08),
        new("CHK-09", "Watchdog disarms after a trip and re-arms on clear", "FR-11", ["CHK-08"], false, Chk09),
        new("CHK-10", "Clean release disarms", "FR-11 \"Clean release disarms\"", ["CHK-08"], false, Chk10),
        new("CHK-11", "Advisory lease", "FR-11 \"Advisory lease\"", ["CHK-02"], false, Chk11),
        new("CHK-12", "Home", "Command semantics: Home", ["CHK-06"], true, Chk12),
        new("CHK-13", "MoveAbsolute to TravelMin + 10", "Command semantics: MoveAbsolute", ["CHK-03", "CHK-12"], true, Chk13),
        new("CHK-14", "Stop mid-move", "Command semantics: Stop; FR-11 priority", ["CHK-13"], true, Chk14),
        new("CHK-15", "MoveVelocity", "Command semantics: MoveVelocity", ["CHK-13"], true, Chk15),
        new("CHK-16", "Kill test", "FR-11", ["CHK-15"], true, Chk16),
    ];

    // ---- CHK-01 … CHK-05: read-only apart from the parameter registers -----------------------------------------

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
            : CheckOutcome.Fail($"MapVersion {version}, expected {RegisterMap.Version}", ("mapVersion", version));
    }

    private static async Task<CheckOutcome> Chk03(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        (string, long)[] observed = [("travelMin", s.TravelMin), ("travelMax", s.TravelMax), ("maxVelocity", s.MaxVelocity)];
        var failures = new List<string>();
        if (!s.LimitsPublished) failures.Add("TravelMin, TravelMax and MaxVelocity are all 0 (limits not published)");
        else
        {
            if (s.TravelMin >= s.TravelMax) failures.Add($"TravelMin {s.TravelMin} is not below TravelMax {s.TravelMax}");
            if (s.MaxVelocity <= 0) failures.Add($"MaxVelocity {s.MaxVelocity} is not above 0");
        }

        if (failures.Count == 0) ctx.Limits = (s.TravelMin, s.TravelMax, s.MaxVelocity);
        return CheckOutcome.Judge(failures,
            $"travel {Words.FromRaw(s.TravelMin)}..{Words.FromRaw(s.TravelMax)}, max velocity {Words.FromRaw(s.MaxVelocity)}", observed);
    }

    private static async Task<CheckOutcome> Chk04(CheckContext ctx, CancellationToken ct)
    {
        const int reads = 30;
        var answered = 0;
        long slowest = 0;
        var invalid = new List<ushort>();
        var errors = new List<string>();
        var next = CheckContext.Now();
        for (var i = 0; i < reads; i++)
        {
            var t = CheckContext.Now();
            try
            {
                var s = await ctx.ReadStatusAsync(ct);
                answered++;
                if (!ValidStates.Contains(s.State)) invalid.Add(s.State);
            }
            catch (MotionException ex)
            {
                errors.Add(ex.Message);
            }

            slowest = Math.Max(slowest, CheckContext.MsSince(t));
            next += System.Diagnostics.Stopwatch.Frequency / 10;
            var delay = System.Diagnostics.Stopwatch.GetElapsedTime(CheckContext.Now(), next);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }

        var failures = new List<string>();
        if (answered < reads) failures.Add($"{reads - answered} of {reads} reads failed ({errors.FirstOrDefault()})");
        if (slowest > 100) failures.Add($"slowest round trip {slowest} ms > 100 ms");
        if (invalid.Count > 0) failures.Add($"State outside {{0,1,2,3,4,6,7}}: {string.Join(", ", invalid.Distinct())}");
        return CheckOutcome.Judge(failures, $"{answered}/{reads} reads, slowest {slowest} ms",
            ("reads", answered), ("slowestMs", slowest), ("invalidStates", invalid.Count));
    }

    private static async Task<CheckOutcome> Chk05(CheckContext ctx, CancellationToken ct)
    {
        var address = ctx.Map.TargetPosition;
        async Task<int> ReadBackAsync() =>
            Words.Read(await ctx.Channel.ReadHoldingAsync(ctx.Unit, address, 2, "read TargetPosition", ChannelPriority.Move, ct));

        await ctx.WriteAsync(address, [0x0002, 0x0001], "TargetPosition = 65538", ct);
        var first = await ReadBackAsync();
        await ctx.WriteAsync(address, [0xFFFE, 0xFFFF], "TargetPosition = -2", ct);
        var second = await ReadBackAsync();
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var after = await ReadBackAsync();

        var failures = new List<string>();
        if (first != 65538) failures.Add($"[0x0002, 0x0001] read back as {first}, expected 65538");
        if (second != -2) failures.Add($"[0xFFFE, 0xFFFF] read back as {second}, expected -2");
        if (after != second) failures.Add($"TargetPosition changed to {after} within 1 s — the PLC writes a driver-owned register");
        return CheckOutcome.Judge(failures, "both values read back exactly and stayed",
            ("firstReadBack", first), ("secondReadBack", second), ("afterOneSecond", after));
    }

    // ---- CHK-06 … CHK-11: handshake, watchdog, lease ------------------------------------------------------------

    private static async Task<CheckOutcome> Chk06(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State is not (Disabled or Standstill))
            return CheckOutcome.Fail($"precondition: State {s.State}, expected 0 or 1", ("state", s.State));

        await ctx.TakeLeaseAsync(ct);
        await ctx.Beater.StartAsync(ct);

        var on = await ctx.Commands.SendAsync(CommandBits.Enable, ct);
        if (!on.Acked) return NoAck("Enable 1", on);
        var standstill = await ctx.WaitForAsync(v => v.State == Standstill, StateTimeout, on.WrittenAt, ct);

        var off = await ctx.Commands.SendAsync(CommandBits.None, ct);
        if (!off.Acked) return NoAck("Enable 0", off, ("enableAckMs", on.AckMs), ("standstillMs", standstill.ElapsedMs));
        var disabled = await ctx.WaitForAsync(v => v.State == Disabled, StateTimeout, off.WrittenAt, ct);

        var failures = new List<string>();
        if (on.AckMs > AckMs) failures.Add($"Enable 1 acknowledged after {on.AckMs} ms > 500 ms");
        if (!standstill.Met) failures.Add($"State {standstill.View.State}, not 1, 5 s after Enable 1");
        if (off.AckMs > AckMs) failures.Add($"Enable 0 acknowledged after {off.AckMs} ms > 500 ms");
        if (!disabled.Met) failures.Add($"State {disabled.View.State}, not 0, 5 s after Enable 0");
        return CheckOutcome.Judge(failures, $"Enable 1 ack {on.AckMs} ms, State 1 at {standstill.ElapsedMs} ms; Enable 0 ack {off.AckMs} ms, State 0 at {disabled.ElapsedMs} ms",
            ("enableAckMs", on.AckMs), ("standstillMs", standstill.ElapsedMs), ("disableAckMs", off.AckMs), ("disabledMs", disabled.ElapsedMs));
    }

    private static async Task<CheckOutcome> Chk07(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State != Disabled) return CheckOutcome.Fail($"precondition: State {s.State}, expected 0", ("state", s.State));

        var reset = await ctx.Commands.SendAsync(CommandBits.Reset, ct);
        if (!reset.Acked) return NoAck("Reset", reset);
        var (changed, view) = await ctx.WatchAsync(v => v.State != Disabled || v.Status.FaultCode != 0, TimeSpan.FromMilliseconds(300), ct);

        var failures = new List<string>();
        if (reset.AckMs > AckMs) failures.Add($"Reset acknowledged after {reset.AckMs} ms > 500 ms");
        if (changed) failures.Add($"Reset outside ErrorStop changed the axis: State {view.State}, FaultCode {view.Status.FaultCode}");
        return CheckOutcome.Judge(failures, $"ack {reset.AckMs} ms, no-op as required",
            ("ackMs", reset.AckMs), ("state", view.State), ("faultCode", view.Status.FaultCode));
    }

    private static async Task<CheckOutcome> Chk08(CheckContext ctx, CancellationToken ct)
    {
        var trip = await StallAndTripAsync(ctx, ct);
        return trip.Judge("trip");
    }

    private static async Task<CheckOutcome> Chk09(CheckContext ctx, CancellationToken ct)
    {
        // Setup: every check restores, so CHK-08's trip is gone; provoke one (see dev-log: CHK-09 reading).
        var setup = await StallAndTripAsync(ctx, ct);
        if (!setup.Tripped) return CheckOutcome.Fail($"setup: {setup.Describe()}", setup.Observed("setupTripAfterMs"));

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

        var failures = new List<string>();
        if (tripWhileLatched) failures.Add($"a trip was counted while WatchdogFault was latched (+{tripsWhileLatched})");
        if (!reset.Acked) failures.Add("Reset not acknowledged within 500 ms");
        if (tripWhileBeating) failures.Add($"tripped while beating after the clear (+{tripsWhileBeating}, WatchdogFault {beatingView.WatchdogFault})");
        failures.AddRange(second.Failures());
        return CheckOutcome.Judge(failures, $"no trip while latched or beating; second trip after {second.AfterMs} ms",
            ("tripsWhileLatched", tripsWhileLatched), ("tripsWhileBeating", tripsWhileBeating),
            ("secondTripAfterMs", second.AfterMs), ("watchdogTrips", second.View.WatchdogTrips));
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
            ? CheckOutcome.Fail($"tripped after a clean release: WatchdogFault {view.WatchdogFault}, trips +{delta}",
                ("watchdogFault", view.WatchdogFault), ("tripsDelta", delta))
            : CheckOutcome.Pass("no trip 2 s after LeaseOwner = 0", ("watchdogFault", view.WatchdogFault), ("tripsDelta", delta));
    }

    private static async Task<CheckOutcome> Chk11(CheckContext ctx, CancellationToken ct)
    {
        var failures = new List<string>();
        var interval = Beater.Period;

        // (a) Unowned → the driver's lease client takes it, then releases.
        await ctx.Beater.StopAsync();
        await ctx.ReleaseLeaseAsync(ct);
        var clientA = LeaseClient(ctx, interval);
        await clientA.AcquireAsync(TimeSpan.FromSeconds(3), ct);
        ctx.TookLease = true;
        var aOwner = (await ctx.ReadViewAsync(ct)).LeaseOwner;
        if (aOwner != ctx.Options.OwnerId) failures.Add($"(a) LeaseOwner read back {aOwner}, expected {ctx.Options.OwnerId}");
        await clientA.StopAsync(); // releases: writes 0 over its own id

        // (b) A live incumbent 65534 → refused with LeaseHeld after the 3 s timeout.
        await ctx.WriteAsync(ctx.Map.LeaseOwner, CheckerOptions.ForeignOwnerId, "impersonate incumbent 65534", ct);
        var incumbent = new Beater(ctx, $"incumbent {CheckerOptions.ForeignOwnerId}");
        await incumbent.StartAsync(ct);
        long refusedMs = -1;
        var t = CheckContext.Now();
        try
        {
            await LeaseClient(ctx, interval).AcquireAsync(TimeSpan.FromSeconds(3), ct);
            ctx.TookLease = true;
            failures.Add("(b) the lease client took the lease from a live incumbent");
        }
        catch (MotionException ex) when (ex.Error == MotionError.LeaseHeld)
        {
            refusedMs = CheckContext.MsSince(t);
            if (!ex.Message.Contains(CheckerOptions.ForeignOwnerId.ToString(), StringComparison.Ordinal))
                failures.Add($"(b) LeaseHeld does not name {CheckerOptions.ForeignOwnerId}: {ex.Message}");
        }

        var bOwner = (await ctx.ReadViewAsync(ct)).LeaseOwner;
        if (bOwner != CheckerOptions.ForeignOwnerId) failures.Add($"(b) LeaseOwner is {bOwner}, expected {CheckerOptions.ForeignOwnerId} to be untouched");

        // (c) The incumbent dies while the client watches → taken within 2 s of its last beat.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(6));
        var acquire = LeaseClient(ctx, interval).AcquireAsync(null, budget.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        await incumbent.StopAsync();
        ctx.CausedTrip = true; // 65534 held the lease and beat, so the PLC armed; its death trips the watchdog
        long takenMs = -1;
        try
        {
            await acquire;
            ctx.TookLease = true;
            takenMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(incumbent.LastBeatAt).TotalMilliseconds;
            if (takenMs > 2000) failures.Add($"(c) lease taken {takenMs} ms after the incumbent's last beat, > 2000 ms");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            failures.Add("(c) the lease was not taken within 6 s of the incumbent's death");
        }

        return CheckOutcome.Judge(failures, $"(a) read back {aOwner}; (b) refused after {refusedMs} ms; (c) taken {takenMs} ms after the last beat",
            ("aLeaseOwner", aOwner), ("bRefusedAfterMs", refusedMs), ("bLeaseOwner", bOwner), ("cTakenAfterMs", takenMs));
    }

    // ---- CHK-12 … CHK-16: motion (only with --allow-motion) -----------------------------------------------------

    private static async Task<CheckOutcome> Chk12(CheckContext ctx, CancellationToken ct)
    {
        var enable = await EnsureEnabledAsync(ctx, ct);
        if (enable is not null) return CheckOutcome.Fail(enable);

        var home = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Home, ct);
        if (!home.Acked) return NoAck("Home", home);
        var done = await ctx.WaitForAsync(v => (v.State == Standstill && v.Status.Homed) || v.State == ErrorStop,
            HomingTimeout, home.WrittenAt, ct);
        var s = done.View.Status;

        var failures = new List<string>();
        if (home.AckMs > AckMs) failures.Add($"Home acknowledged after {home.AckMs} ms > 500 ms");
        if (!done.Met) failures.Add($"not homed within 120 s (State {s.State})");
        else if (s.State != Standstill || !s.Homed) failures.Add($"homing ended in State {s.State}, FaultCode {s.FaultCode}");
        if (s.FaultCode != 0) failures.Add($"FaultCode {s.FaultCode}");
        return CheckOutcome.Judge(failures, $"homed in {done.ElapsedMs} ms at {Words.FromRaw(s.ActualPosition)}",
            ("ackMs", home.AckMs), ("homingMs", done.ElapsedMs), ("actualPosition", s.ActualPosition), ("faultCode", s.FaultCode));
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
        if (!move.Acked) return NoAck("MoveAbsolute", move);

        var budget = TimeSpan.FromSeconds(Math.Abs((double)target - start) / speed + 10);
        var done = await ctx.WaitForAsync(v => (v.State == Standstill && v.Status.InPosition) || v.State == ErrorStop, budget, move.WrittenAt, ct);
        var s = done.View.Status;
        var error = Math.Abs((long)s.ActualPosition - target);
        var tolerance = (long)Math.Round(ctx.Options.Tolerance * Words.Scale);

        var failures = new List<string>();
        if (move.AckMs > AckMs) failures.Add($"MoveAbsolute acknowledged after {move.AckMs} ms > 500 ms");
        if (move.View.State != Discrete) failures.Add($"the ack showed State {move.View.State}, expected 3");
        if (!done.Met || s.State != Standstill || !s.InPosition)
            failures.Add($"no Standstill with InPosition within {budget.TotalSeconds:F0} s (State {s.State}, FaultCode {s.FaultCode})");
        if (error > tolerance) failures.Add($"position error {Words.FromRaw((int)error)} > tolerance {ctx.Options.Tolerance}");
        return CheckOutcome.Judge(failures, $"arrived in {done.ElapsedMs} ms, error {Words.FromRaw((int)error)}",
            ("ackMs", move.AckMs), ("target", target), ("actualPosition", s.ActualPosition), ("errorRaw", error), ("durationMs", done.ElapsedMs));
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
        if (!move.Acked) return NoAck("MoveAbsolute", move);

        var cruising = await ctx.WaitForAsync(v => Math.Abs((long)v.Status.ActualVelocity) * 10 >= speed * 9L,
            TimeSpan.FromSeconds(2), move.WrittenAt, ct);
        var velocityAtStop = cruising.View.Status.ActualVelocity;

        var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
        var halt = await ctx.WaitForAsync(v => v.Status.ActualVelocity == 0 && v.State == Standstill, TimeSpan.FromSeconds(1), stop.WrittenAt, ct);

        var failures = new List<string>();
        if (!stop.Acked) failures.Add("Stop not acknowledged within 500 ms");
        if (!halt.Met) failures.Add($"not halted 1 s after Stop (State {halt.View.State}, ActualVelocity {halt.View.Status.ActualVelocity})");
        else if (halt.ElapsedMs > HaltBudget.TotalMilliseconds) failures.Add($"halted {halt.ElapsedMs} ms after Stop, > 200 ms");
        return CheckOutcome.Judge(failures, $"Stop ack {stop.AckMs} ms, halted {halt.ElapsedMs} ms after the write",
            ("velocityAtStop", velocityAtStop), ("stopAckMs", stop.AckMs), ("haltMs", halt.ElapsedMs));
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
        if (!jog.Acked) return NoAck("MoveVelocity", jog);

        var fastest = 0;
        await ctx.WaitForAsync(v => { fastest = Math.Max(fastest, v.Status.ActualVelocity); return v.State == ErrorStop; },
            TimeSpan.FromSeconds(1), CheckContext.Now(), ct);

        var stop = await ctx.Commands.SendAsync(CommandBits.Enable | CommandBits.Stop, ct, ChannelPriority.Stop);
        var halt = await ctx.WaitForAsync(v => v.State == Standstill, TimeSpan.FromSeconds(1), stop.WrittenAt, ct);

        var failures = new List<string>();
        if (jog.AckMs > AckMs) failures.Add($"MoveVelocity acknowledged after {jog.AckMs} ms > 500 ms");
        if (jog.View.State != Continuous) failures.Add($"the ack showed State {jog.View.State}, expected 4");
        if (fastest <= 0) failures.Add("ActualVelocity never went above 0 during the run");
        if (!stop.Acked) failures.Add("Stop not acknowledged within 500 ms");
        if (!halt.Met) failures.Add($"State {halt.View.State}, not 1, 1 s after Stop");
        else if (halt.ElapsedMs > HaltBudget.TotalMilliseconds) failures.Add($"State 1 {halt.ElapsedMs} ms after Stop, > 200 ms");
        return CheckOutcome.Judge(failures, $"ran at up to {Words.FromRaw(fastest)}, stopped in {halt.ElapsedMs} ms",
            ("ackMs", jog.AckMs), ("maxActualVelocity", fastest), ("haltMs", halt.ElapsedMs));
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
        if (!jog.Acked) return NoAck("MoveVelocity", jog);
        var moving = await ctx.WaitForAsync(v => v.Status.ActualVelocity > 0, TimeSpan.FromSeconds(1), jog.WrittenAt, ct);
        if (!moving.Met) return CheckOutcome.Fail($"precondition: the axis is not moving (State {moving.View.State})");

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

        var failures = new List<string>();
        if (!trip.Met) failures.Add($"no trip (FaultCode 4, State 7) within 3 s of the last beat (State {trip.View.State}, FaultCode {trip.View.Status.FaultCode})");
        else if (trip.ElapsedMs is < TripMinMs or > TripMaxMs) failures.Add($"tripped {trip.ElapsedMs} ms after the last beat, outside 1000–1500 ms");
        if (!halt.Met || halt.ElapsedMs > HaltBudget.TotalMilliseconds) failures.Add($"ActualVelocity {halt.View.Status.ActualVelocity} not 0 within 200 ms of the trip");
        if (!homed) failures.Add("Homed was lost by the trip");
        return CheckOutcome.Judge(failures, $"trip after {trip.ElapsedMs} ms, halted {halt.ElapsedMs} ms later, Homed kept",
            ("tripAfterMs", trip.ElapsedMs), ("haltAfterTripMs", halt.ElapsedMs), ("homed", homed ? 1 : 0));
    }

    // ---- shared steps -------------------------------------------------------------------------------------------

    private const long AckMs = 500;

    private static AxisHeartbeat LeaseClient(CheckContext ctx, TimeSpan interval) =>
        new("conformance", ctx.Channel, ctx.Unit, ctx.Map, ctx.Options.OwnerId, interval, ctx.Logger);

    private static CheckOutcome NoAck(string what, Ack ack, params (string, long)[] observed) =>
        CheckOutcome.Fail($"no CommandAck within 500 ms ({what}, CommandSeq {ack.Seq}; CommandAck {ack.View.Status.CommandAck}, State {ack.View.State})",
            [.. observed, ("commandSeq", ack.Seq), ("commandAck", ack.View.Status.CommandAck)]);

    private static long Delta(ushort now, ushort before) => (ushort)(now - before);

    /// <summary>Enable when Disabled and wait for Standstill; a message when that fails.</summary>
    private static async Task<string?> EnsureEnabledAsync(CheckContext ctx, CancellationToken ct)
    {
        var s = await ctx.ReadStatusAsync(ct);
        if (s.State == Standstill) return null;
        if (s.State != Disabled) return $"precondition: State {s.State}, expected 0 or 1";
        var on = await ctx.Commands.SendAsync(CommandBits.Enable, ct);
        if (!on.Acked) return $"no CommandAck within 500 ms (Enable, CommandSeq {on.Seq})";
        var w = await ctx.WaitForAsync(v => v.State == Standstill, StateTimeout, on.WrittenAt, ct);
        return w.Met ? null : $"State {w.View.State}, not 1, 5 s after Enable";
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
        public IEnumerable<string> Failures()
        {
            if (!Tripped)
            {
                yield return "no trip within 1.5 s of the last beat";
                yield break;
            }

            if (AfterMs < TripMinMs) yield return $"tripped {AfterMs} ms after the last beat, before 1.0 s";
            if (AfterMs > TripMaxMs) yield return $"no trip within 1.5 s of the last beat (tripped after {AfterMs} ms)";
            if (View.WatchdogFault != 1) yield return $"WatchdogFault {View.WatchdogFault}, expected 1";
            if (Delta(View.WatchdogTrips, TripsBefore) != 1) yield return $"WatchdogTrips +{Delta(View.WatchdogTrips, TripsBefore)}, expected +1";
            if (View.State != ErrorStop) yield return $"State {View.State}, expected 7";
            if (View.Status.FaultCode != 4) yield return $"FaultCode {View.Status.FaultCode}, expected 4";
        }

        public string Describe() => string.Join("; ", Failures().DefaultIfEmpty($"trip after {AfterMs} ms"));

        public (string, long)[] Observed(string afterKey) =>
            [(afterKey, Tripped ? AfterMs : -1), ("watchdogTrips", View.WatchdogTrips), ("faultCode", View.Status.FaultCode), ("state", View.State)];

        public CheckOutcome Judge(string what) =>
            CheckOutcome.Judge([.. Failures()], $"{what} after {AfterMs} ms",
                ("tripAfterMs", Tripped ? AfterMs : -1), ("watchdogTrips", View.WatchdogTrips), ("faultCode", View.Status.FaultCode), ("state", View.State));
    }
}
