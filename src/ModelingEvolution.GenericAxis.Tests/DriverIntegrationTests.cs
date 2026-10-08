using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Xunit.Abstractions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.Drawing;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Integration, driver side (GA-I-01 … GA-I-24): the real driver over real Modbus TCP against
/// <see cref="MiniPlc"/>. A halt is timed on the PLC's ground truth: the timestamp of the first scan that shows it
/// (<see cref="MiniPlc.WhenScan"/>), not when the test noticed. Budgets are the protocol's, never Pamet physics; a budget
/// failure while the fixture missed its own scan cadence is INCONCLUSIVE (<see cref="Cadence"/>).
/// </summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class DriverIntegrationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan T = LiveRig.T;

    private static async Task<MotionException> Throws(Func<Task> act)
    {
        var ex = await act.Invoking(a => a().WaitAsync(T)).Should().ThrowAsync<MotionException>();
        return ex.Which;
    }

    /// <summary>Starts a move to <paramref name="target"/> and waits until the PLC cruises at <paramref name="speed"/>.</summary>
    private static async Task<Task> Cruise(LiveRig rig, ModbusLinearTrack track, double target, double speed)
    {
        var move = track.Carriage.MoveAbsoluteAsync(new Mm(target), new MmPerS(speed));
        await rig.Plc.WaitFor(t => Math.Abs(t.Velocity) >= speed * 0.99, T, "cruising");
        return move;
    }

    [Fact(DisplayName = "GA-I-01 Attach takes the lease and arms the watchdog")]
    public async Task Connect_FreshPlc_LeaseTakenWatchdogArmedLimitsFromPlc()
    {
        await using var rig = new LiveRig();
        rig.Plc.Truth.LeaseOwner.Should().Be(0);

        var track = await rig.ConnectedTrack();

        rig.Plc.Truth.LeaseOwner.Should().Be(1);
        await rig.Plc.WaitFor(t => t.WatchdogArmed, T, "the first beat arms the watchdog");
        track.Limits.Should().Be(new AxisLimits(0, 10_000, 500, LimitSource.Plc));
        rig.Logs.GetSnapshot().Where(r => r.Level == LogLevel.Information)
            .Should().Contain(r => r.Message.Contains("attached as owner 1")
                                   && r.Message.Contains($"127.0.0.1:{rig.Plc.Port}/1")
                                   && r.Message.Contains("10000") && r.Message.Contains("published by the PLC"));
    }

    [Fact(DisplayName = "Acceleration set on the device between moves is written to C+6…C+7; null writes 0 = PLC default")]
    public async Task Acceleration_SetBetweenMoves_WrittenWithTheNextMove()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack();
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        await track.Carriage.MoveAbsoluteAsync(new Mm(1000), new MmPerS(500)).WaitAsync(T);
        rig.Plc.AccelerationRegister.Should().Be(0, "no Acceleration configured means the PLC's default ramp");

        track.Acceleration = 250;
        await track.Carriage.MoveAbsoluteAsync(new Mm(1500), new MmPerS(500)).WaitAsync(T);
        rig.Plc.AccelerationRegister.Should().Be(250_000, "unit/s² × 1000");

        track.Acceleration = null;
        await track.Carriage.MoveAbsoluteAsync(new Mm(1000), new MmPerS(500)).WaitAsync(T);
        rig.Plc.AccelerationRegister.Should().Be(0);

        var zero = () => track.Acceleration = 0;
        zero.Should().Throw<ArgumentOutOfRangeException>("a ramp must be > 0 when set");
        track.Acceleration.Should().BeNull();
    }

    [Fact(DisplayName = "GA-I-02 Home from Disabled")]
    public async Task Home_UnhomedDisabled_StandstillAtHomeSensor()
    {
        await using var rig = new LiveRig(new MiniPlcOptions { HomedAtPowerUp = false });
        var track = await rig.ConnectedTrack();
        track.Carriage.State.Should().Be(AxisState.Disabled);
        track.Carriage.Status.Position.Should().BeNull();

        await track.Carriage.HomeAsync().WaitAsync(T);

        track.Carriage.State.Should().Be(AxisState.Standstill);
        track.Carriage.Status.Position.Should().BeApproximately(rig.Plc.Options.HomeSensorPosition, 0.001);
        rig.Plc.Truth.Homed.Should().BeTrue();
    }

    [TimingFact(DisplayName = "GA-I-03 MoveAbsolute arrives and the reading is the register")]
    public async Task MoveAbsolute_Arrives_ReadingEqualsRegister()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);

        // A fixture scan stalled ≥ 1 s trips the fixture's own watchdog mid-move (a false WatchdogTripped).
        rig.Plc.ResetMaxScanGap();
        await Cadence.Budget(rig.Plc, () => track.Carriage.MoveAbsoluteAsync(new Mm(2500), new MmPerS(250)).WaitAsync(T));

        var truth = rig.Plc.Truth;
        var register = Math.Round(truth.Position * 1000, MidpointRounding.AwayFromZero) / 1000;
        track.Carriage.Status.Position.Should().Be(register);
        track.Carriage.Status.Position.Should().BeApproximately(2500, rig.Plc.Options.InPositionWindow);
        track.Carriage.State.Should().Be(AxisState.Standstill);
    }

    [Fact(DisplayName = "GA-I-04 MoveRelative")]
    public async Task MoveRelative_FromTarget_ArrivesAtSum()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        await track.Carriage.MoveAbsoluteAsync(new Mm(2500), new MmPerS(500)).WaitAsync(T);

        await track.Carriage.MoveRelativeAsync(new Mm(-500), new MmPerS(500)).WaitAsync(T);

        track.Carriage.Status.Position.Should().BeApproximately(2000, 0.005);
        rig.Plc.Truth.Position.Should().BeApproximately(2000, 0.005);
    }

    [TimingFact(DisplayName = "GA-I-05 STOP halts within 200 ms")]
    public async Task Stop_Cruising_HaltsWithin200ms()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var move = await Cruise(rig, track, 9000, 500);

        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "halted");
        var stop = track.Carriage.StopAsync();
        var haltMs = Stopwatch.GetElapsedTime(start, (await halted).At).TotalMilliseconds;
        var gap = rig.Plc.MaxScanGap;
        await stop.WaitAsync(T);

        output.WriteLine($"GA-I-05 halt after StopAsync: {haltMs:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        Cadence.Budget(gap, () => haltMs.Should().BeLessThanOrEqualTo(200, "protocol: motion ceases within 200 ms of the Stop write"));
        track.Carriage.State.Should().Be(AxisState.Standstill);
        var ended = await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<Exception>();
        ended.Which.Should().Match<Exception>(e => e is OperationCanceledException
                                                   || (e is MotionException && ((MotionException)e).Error == MotionError.MotionFailed));
    }

    [Fact(DisplayName = "GA-I-26 A fixture that misses its scan cadence makes a failed budget INCONCLUSIVE, not red")]
    public async Task Stop_FixtureScanStalled_BudgetFailureIsInconclusive()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var move = await Cruise(rig, track, 9000, 500);

        // GA-I-05's window, with the fixture's scan thread stalled 300 ms (what a starved host does to it).
        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "halted");
        rig.Plc.OnScan(() => Thread.Sleep(300));
        var stop = track.Carriage.StopAsync();
        var haltMs = Stopwatch.GetElapsedTime(start, (await halted).At).TotalMilliseconds;
        var gap = rig.Plc.MaxScanGap;
        await stop.WaitAsync(T);
        await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<Exception>();

        output.WriteLine($"GA-I-26 halt {haltMs:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        haltMs.Should().BeGreaterThan(200, "the stalled fixture halted late");
        FluentActions.Invoking(() => Cadence.Budget(gap, () => haltMs.Should().BeLessThanOrEqualTo(200)))
            .Should().Throw<InconclusiveException>()
            .WithMessage($"INCONCLUSIVE: fixture missed cadence: max scan gap {gap.TotalMilliseconds:F0} ms*");
    }

    [TimingFact(DisplayName = "GA-I-06 The station STOP path stops the axis")]
    public async Task StopAll_Cruising_HaltsWithin200ms()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var move = await Cruise(rig, track, 9000, 500);

        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "halted");
        var stop = ((IMotionDevice)track).StopAllAsync();
        var haltMs = Stopwatch.GetElapsedTime(start, (await halted).At).TotalMilliseconds;
        var gap = rig.Plc.MaxScanGap;
        output.WriteLine($"GA-I-06 halt after StopAllAsync: {haltMs:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        Cadence.Budget(gap, () => haltMs.Should().BeLessThanOrEqualTo(200));
        await stop.WaitAsync(T);
        await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<Exception>();
    }

    [TimingFact(DisplayName = "GA-I-07 + GA-I-08 Killing the commander halts the axis within 1 s; recovery needs no re-home")]
    public async Task Kill_Cruising_WatchdogHaltsThenSuccessorRecoversWithoutHome()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var tripsBefore = rig.Plc.Truth.WatchdogTrips;
        _ = await Cruise(rig, track, 9000, 500);

        // GA-I-07: the in-process kill — no network I/O.
        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "the watchdog halts the axis");
        track.Dispose();
        var haltS = Stopwatch.GetElapsedTime(start, (await halted).At).TotalSeconds;
        var gap = rig.Plc.MaxScanGap;

        output.WriteLine($"GA-I-07 halt after the kill: {haltS * 1000:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        Cadence.Budget(gap, () =>
        {
            haltS.Should().BeLessThanOrEqualTo(1.05, "FR-11: 1 s stall window (ADR-31 keeps 1.05 s for the simulator)");
            haltS.Should().BeGreaterThanOrEqualTo(0.85, "the axis must not stop before the stall window (last beat ≤ 100 ms before the kill)");
        });
        var truth = rig.Plc.Truth;
        truth.FaultCode.Should().Be(4);
        truth.WatchdogFault.Should().Be(1);
        truth.WatchdogTrips.Should().Be((ushort)(tripsBefore + 1));
        truth.Homed.Should().BeTrue("a trip does not touch Homed");
        truth.State.Should().Be(7);

        // GA-I-08: a successor with another owner id takes the stale lease and recovers without homing.
        var homesBefore = truth.HomeCommandsAccepted;
        var successor = await rig.ConnectedTrack(owner: 2);
        rig.Plc.Truth.LeaseOwner.Should().Be(2);
        rig.Plc.Truth.WatchdogFault.Should().Be(0, "attach clears a predecessor's latched trip");
        successor.Carriage.State.Should().Be(AxisState.ErrorStop);
        successor.Carriage.Status.Error.Should().Be(MotionError.WatchdogTripped);

        await successor.Carriage.ResetAsync().WaitAsync(T);
        await successor.Carriage.PowerAsync(true).WaitAsync(T);
        await successor.Carriage.MoveAbsoluteAsync(new Mm(1000), new MmPerS(500)).WaitAsync(T);

        successor.Carriage.Status.Position.Should().BeApproximately(1000, 0.005);
        rig.Plc.Truth.HomeCommandsAccepted.Should().Be(homesBefore, "restart = reset + re-command, no re-home");
    }

    [TimingFact(DisplayName = "GA-I-09 A second commander is refused while the first beats")]
    public async Task SecondCommander_WhileFirstBeats_LeaseHeldAfterTimeout()
    {
        await using var rig = new LiveRig();
        var a = await rig.ConnectedTrack(power: true);
        var move = a.Carriage.MoveAbsoluteAsync(new Mm(9000), new MmPerS(100));
        await rig.Plc.WaitFor(t => t.State == 3, T, "A is moving");
        var owners = new ConcurrentBag<ushort>();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                owners.Add(rig.Plc.Truth.LeaseOwner);
                await Task.Delay(5);
            }
        });

        var b = rig.Track(o => o with { LeaseTimeout = TimeSpan.FromSeconds(3) }, owner: 2);
        rig.Plc.ResetMaxScanGap();
        var sw = Stopwatch.StartNew();
        var ex = await Throws(() => b.ConnectAsync());
        var refusedAfter = sw.Elapsed.TotalSeconds;
        var gap = rig.Plc.MaxScanGap;
        await sampling.CancelAsync();
        await sampler;

        output.WriteLine($"GA-I-09 refused after {refusedAfter * 1000:F0} ms: {ex.Message}");
        ex.Error.Should().Be(MotionError.LeaseHeld);
        ex.Message.Should().Contain("Read LeaseOwner (C+9 = holding 9) = 1, expected 0 or 2");
        Cadence.Budget(gap, () => refusedAfter.Should().BeInRange(3.0, 4.0));
        owners.Should().NotBeEmpty().And.OnlyContain(o => o == 1);
        move.IsCompleted.Should().BeFalse("A's move is undisturbed");
        rig.Plc.Truth.State.Should().Be(3);
        await a.Carriage.StopAsync().WaitAsync(T);
    }

    [TimingFact(DisplayName = "GA-I-10 The successor attaches within 2 s of the incumbent's death")]
    public async Task Successor_IncumbentKilled_AttachesWithin2s()
    {
        await using var rig = new LiveRig();
        var a = await rig.ConnectedTrack();
        var b = rig.Track(owner: 2);
        var connecting = b.ConnectAsync();
        await Task.Delay(500);
        connecting.IsCompleted.Should().BeFalse("B is watching a live incumbent");

        rig.Plc.ResetMaxScanGap();
        var sw = Stopwatch.StartNew();
        a.Dispose();
        await connecting.WaitAsync(T);
        var attachedAfter = sw.Elapsed;
        var gap = rig.Plc.MaxScanGap;
        await rig.Plc.NextScanAsync();

        output.WriteLine($"GA-I-10 successor attached {attachedAfter.TotalMilliseconds:F0} ms after the kill, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        Cadence.Budget(gap, () => attachedAfter.TotalSeconds.Should().BeLessThanOrEqualTo(2.0));
        rig.Plc.Truth.LeaseOwner.Should().Be(2);
    }

    [TimingFact(DisplayName = "GA-I-11 A clean disconnect trips nothing")]
    public async Task Disconnect_Clean_DisabledReleasedNoTrip()
    {
        await using var rig = new LiveRig();
        var a = await rig.ConnectedTrack(power: true);
        await rig.Plc.WaitFor(t => t.WatchdogArmed && t.State == 1, T, "armed and energised");
        var trips = rig.Plc.Truth.WatchdogTrips;

        await a.DisconnectAsync().WaitAsync(T);
        // The writes have landed; the PLC publishes them at its next scan (10 ms).
        rig.Plc.ResetMaxScanGap();
        await Cadence.Budget(rig.Plc, () => rig.Plc.WaitFor(t => t.State == 0 && t.LeaseOwner == 0,
            TimeSpan.FromMilliseconds(200), "Disabled and released one scan after the disconnect"));
        await Task.Delay(2000);

        rig.Plc.Truth.WatchdogTrips.Should().Be(trips);
        rig.Plc.Truth.WatchdogFault.Should().Be(0);
        rig.Plc.Truth.FaultCode.Should().Be(0);
        rig.Plc.ResetMaxScanGap();
        var sw = Stopwatch.StartNew();
        await rig.ConnectedTrack(owner: 2);
        var attachedS = sw.Elapsed.TotalSeconds;
        var gap = rig.Plc.MaxScanGap;
        Cadence.Budget(gap, () => attachedS.Should().BeLessThan(1.0, "a released lease is taken without waiting for expiry"));
    }

    [Fact(DisplayName = "GA-I-12 Another map version is refused")]
    public async Task Connect_MapVersion2_RefusedNothingWritten()
    {
        await using var rig = new LiveRig(new MiniPlcOptions { MapVersion = 2 });
        var track = rig.Track();

        var ex = await Throws(() => track.ConnectAsync());

        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        ex.Message.Should().Contain("Read MapVersion (S+14 = input 14) = 2, expected 1.");
        rig.Logs.GetSnapshot().Should().Contain(r => r.Level == LogLevel.Error && r.Message == ex.Message);
        await Task.Delay(100);
        rig.Plc.WrittenRegisters.Should().BeEmpty();
        rig.Plc.Truth.LeaseOwner.Should().Be(0);
    }

    [Fact(DisplayName = "GA-I-13 Out-of-range requests are refused and nothing moves")]
    public async Task Move_OutOfRange_RefusedNothingWrittenNothingMoves()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        await Task.Delay(300);
        var seq = rig.Plc.Truth.CommandSeq;
        var position = rig.Plc.Truth.Position;

        var range = await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(10_500)));
        range.Error.Should().Be(MotionError.OutOfRange);
        range.Message.Should().Contain("0..10000").And.Contain("published by the PLC");
        var speed = await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(5000), new MmPerS(600)));
        speed.Error.Should().Be(MotionError.UnreachableSpeed);

        await Task.Delay(300);
        rig.Plc.Truth.CommandSeq.Should().Be(seq);
        rig.Plc.Truth.Position.Should().Be(position);
    }

    [TimingFact(DisplayName = "GA-I-14 A communication drop surfaces as CommunicationLost")]
    public async Task CommsDrop_Cruising_CommunicationLostWatchdogHaltsRecoveryByReset()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var move = await Cruise(rig, track, 9000, 500);

        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "the watchdog halts the axis");
        rig.Plc.SetCommunicationDown(true);
        var ex = await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<MotionException>();
        var thrownS = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var haltS = Stopwatch.GetElapsedTime(start, (await halted).At).TotalSeconds;
        var gap = rig.Plc.MaxScanGap;

        output.WriteLine($"GA-I-14 CommunicationLost after {thrownS * 1000:F0} ms, halted after {haltS * 1000:F0} ms, "
                         + $"fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        ex.Which.Error.Should().Be(MotionError.CommunicationLost);
        ex.Which.Message.Should().StartWith("carriage: CommunicationLost: ")
            .And.Contain($"127.0.0.1:{rig.Plc.Port} unit 1 failed twice (reconnected once): ")
            .And.MatchRegex(@"\(FC(03|04|06|16) (read|write) (C\+[0-9]+(…C\+[0-9]+)? = holding|S\+[0-9]+(…S\+[0-9]+)? = input) [0-9]+");
        rig.Logs.GetSnapshot().Should().Contain(r => r.Level == LogLevel.Warning && r.Exception != null
                                                     && r.Message.Contains("reconnecting and retrying once"),
            "the one retry is logged at Warning with the exception");
        Cadence.Budget(gap, () =>
        {
            thrownS.Should().BeLessThanOrEqualTo(1.0);
            haltS.Should().BeLessThanOrEqualTo(1.05);
        });
        track.Carriage.State.Should().Be(AxisState.ErrorStop);
        track.Carriage.Status.Error.Should().Be(MotionError.CommunicationLost);
        rig.Plc.Truth.FaultCode.Should().Be(4);

        rig.Plc.SetCommunicationDown(false);
        var ticks = track.Heartbeat.TickCount;
        await DriverRig.Until(() => track.Heartbeat.TickCount > ticks + 1, "ticks succeed again");
        rig.Logs.GetSnapshot().Should().Contain(r => r.Level == LogLevel.Error
                                                     && r.Message.StartsWith("carriage: WatchdogTripped: "),
            "the trip the PLC reports after the link returns is its own Machine error");
        track.Carriage.Status.Error.Should().Be(MotionError.CommunicationLost, "the loss stays latched until Reset");
        await track.Carriage.ResetAsync().WaitAsync(T);
        track.Carriage.Status.Error.Should().BeNull();
        track.Carriage.State.Should().Be(AxisState.Disabled);
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        track.Carriage.State.Should().Be(AxisState.Standstill);
        rig.Plc.Truth.Homed.Should().BeTrue();
    }

    [TimingFact(DisplayName = "GA-I-25 A silent PLC surfaces as CommunicationLost and a STOP to it returns")]
    public async Task SilentPlc_Cruising_CommunicationLostStopReturnsRecoveryByReset()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var move = await Cruise(rig, track, 9000, 500);

        rig.Plc.ResetMaxScanGap();
        var start = Stopwatch.GetTimestamp();
        var halted = rig.Plc.WhenScan(t => t.Velocity == 0, T, "the watchdog halts the axis");
        rig.Plc.SetSilent(true);
        var ex = await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<MotionException>();
        var thrownS = Stopwatch.GetElapsedTime(start).TotalSeconds;
        var haltS = Stopwatch.GetElapsedTime(start, (await halted).At).TotalSeconds;

        // Review #34: the STOP lane must not block forever behind a silent read. The Stop is still attempted and
        // fails as CommunicationLost within one in-flight frame plus its own two bounded attempts.
        var stopSw = Stopwatch.StartNew();
        var stop = await track.Carriage.Invoking(c => c.StopAsync().WaitAsync(T)).Should().ThrowAsync<MotionException>();
        var stopS = stopSw.Elapsed.TotalSeconds;
        var gap = rig.Plc.MaxScanGap;

        output.WriteLine($"GA-I-25 CommunicationLost after {thrownS * 1000:F0} ms, halted after {haltS * 1000:F0} ms, "
                         + $"STOP returned after {stopS * 1000:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms: {stop.Which.Message}");
        ex.Which.Error.Should().Be(MotionError.CommunicationLost);
        track.Carriage.Status.Error.Should().Be(MotionError.CommunicationLost);
        rig.Plc.Truth.FaultCode.Should().Be(4);
        stop.Which.Error.Should().Be(MotionError.CommunicationLost);
        stop.Which.Message.Should().Contain("timed out");
        Cadence.Budget(gap, () =>
        {
            thrownS.Should().BeLessThanOrEqualTo(1.8, "≤ 1.6 s for the failing call plus one tick interval");
            haltS.Should().BeLessThanOrEqualTo(1.05);
            stopS.Should().BeLessThanOrEqualTo(3.5, "an in-flight frame (≤ 1.3 s) plus the Stop's own two attempts (≤ 1.3 s), plus scheduling slack; never unbounded");
        });

        rig.Plc.SetSilent(false);
        var ticks = track.Heartbeat.TickCount;
        await DriverRig.Until(() => track.Heartbeat.TickCount > ticks + 1, "ticks succeed again");
        rig.Logs.GetSnapshot().Should().Contain(r => r.Level == LogLevel.Error
                                                     && r.Message.StartsWith("carriage: WatchdogTripped: "));
        await track.Carriage.ResetAsync().WaitAsync(T);
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        track.Carriage.State.Should().Be(AxisState.Standstill);
        rig.Plc.Truth.Homed.Should().BeTrue();
    }

    [TimingFact(DisplayName = "GA-I-15 A missing ack is NotAcknowledged, not a link failure")]
    public async Task Home_SuppressAck_CommunicationLostWithin700msBeatContinues()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack();
        rig.Plc.Faults.SuppressAck = true;

        rig.Plc.ResetMaxScanGap();
        var sw = Stopwatch.StartNew();
        var ex = await Throws(() => track.Carriage.HomeAsync());
        var failedMs = sw.Elapsed.TotalMilliseconds;
        var gap = rig.Plc.MaxScanGap;

        output.WriteLine($"GA-I-15 {failedMs:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms: {ex.Message}");
        Cadence.Budget(gap, () => failedMs.Should().BeLessThanOrEqualTo(700));
        ex.Error.Should().Be(MotionError.NotAcknowledged);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Protocol);
        ex.Message.Should().MatchRegex("CommandSeq [0-9]+ written, CommandAck [0-9]+ read after 500 ms, State [0-9]+ read");
        var beat = rig.Plc.Truth.Heartbeat;
        await Task.Delay(1500);
        rig.Plc.Truth.Heartbeat.Should().NotBe(beat, "the heartbeat keeps beating");
        rig.Plc.Truth.WatchdogTrips.Should().Be(0);
    }

    [Fact(DisplayName = "GA-I-16 Configured limits stand in for unpublished ones")]
    public async Task UnpublishedLimits_ConfiguredUsed_NoneRefusesMovesButHomes()
    {
        await using (var rig = new LiveRig(new MiniPlcOptions { PublishLimits = false }))
        {
            var track = await rig.ConnectedTrack(o => o with
            {
                ConfiguredTravelMin = 0, ConfiguredTravelMax = 8000, ConfiguredMaxVelocity = 300,
            }, power: true);

            track.Limits.Source.Should().Be(LimitSource.Configuration);
            var ex = await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(8500)));
            ex.Error.Should().Be(MotionError.OutOfRange);
            ex.Message.Should().Contain("configuration");
        }

        await using (var rig = new LiveRig(new MiniPlcOptions { PublishLimits = false, HomedAtPowerUp = false }))
        {
            var track = await rig.ConnectedTrack();
            track.Limits.Source.Should().Be(LimitSource.None);

            await track.Carriage.HomeAsync().WaitAsync(T);
            rig.Plc.Truth.Homed.Should().BeTrue();

            (await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(100)))).Message.Should().Contain("no limit source");
            (await Throws(() => track.Carriage.MoveVelocityAsync(new MmPerS(10)))).Error.Should().Be(MotionError.OutOfRange);
        }
    }

    [Fact(DisplayName = "GA-I-17 A jog runs unhomed and stops at the travel limit once homed")]
    public async Task Jog_UnhomedRunsHomedStopsAtTravelMax()
    {
        await using (var rig = new LiveRig(new MiniPlcOptions { HomedAtPowerUp = false }))
        {
            var track = await rig.ConnectedTrack(power: true);
            var start = rig.Plc.Truth.Position;

            await track.Carriage.MoveVelocityAsync(new MmPerS(50)).WaitAsync(T);
            await Task.Delay(300);
            await track.Carriage.StopAsync().WaitAsync(T);

            rig.Plc.Truth.Position.Should().BeGreaterThan(start);
            rig.Plc.Truth.Velocity.Should().Be(0);
            track.Carriage.State.Should().Be(AxisState.Standstill);
        }

        await using (var rig = new LiveRig())
        {
            rig.Plc.Teleport(9900);
            var track = await rig.ConnectedTrack(power: true);

            await track.Carriage.MoveVelocityAsync(new MmPerS(100)).WaitAsync(T);
            await rig.Plc.WaitFor(t => t.State == 1, T, "controlled stop at TravelMax");

            rig.Plc.Truth.Position.Should().BeApproximately(10_000, 0.005);
            rig.Plc.Truth.FaultCode.Should().Be(0);
        }
    }

    [Fact(DisplayName = "GA-I-18 A limit switch trips and recovers")]
    public async Task Jog_ForceLimitMax_LimitTrippedThenNegativeJogRuns()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        await track.Carriage.MoveVelocityAsync(new MmPerS(50)).WaitAsync(T);

        rig.Plc.Faults.ForceLimitMax = true;
        await DriverRig.Until(() => track.Carriage.State == AxisState.ErrorStop, "the switch trips");

        track.Carriage.Status.Error.Should().Be(MotionError.LimitTripped);
        track.Carriage.Status.Limits.Should().Be(LimitSwitchState.Max);

        rig.Plc.Faults.ForceLimitMax = false;
        await Task.Delay(300);
        await track.Carriage.ResetAsync().WaitAsync(T);
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        await track.Carriage.MoveVelocityAsync(new MmPerS(-50)).WaitAsync(T);
        rig.Plc.Truth.Velocity.Should().BeLessThan(0);
        await track.Carriage.StopAsync().WaitAsync(T);
    }

    [Fact(DisplayName = "GA-I-19 Faults surface by remedy")]
    public async Task Faults_DuringMoves_SurfaceByRemedy()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);

        var move = await Cruise(rig, track, 9000, 500);
        rig.Plc.Faults.InjectDriveFault();
        (await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<MotionException>()).Which.Error
            .Should().Be(MotionError.DriveFault);

        await track.Carriage.ResetAsync().WaitAsync(T);
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        move = await Cruise(rig, track, 9000, 500);
        rig.Plc.Faults.InjectSafetyStop();
        (await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<MotionException>()).Which.Error
            .Should().Be(MotionError.SafetyStop);

        await track.Carriage.ResetAsync().WaitAsync(T);
        await track.Carriage.PowerAsync(true).WaitAsync(T);
        await track.Carriage.MoveAbsoluteAsync(new Mm(1000), new MmPerS(500)).WaitAsync(T);
        rig.Plc.Faults.FollowingErrorAtHalfway = true;
        var ex = await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(3000), new MmPerS(500)));
        ex.Error.Should().Be(MotionError.MotionFailed);
        ex.Message.Should().Contain("following error");
        rig.Plc.Truth.Position.Should().BeApproximately(2000, 50, "the fault hit at about half the distance");
    }

    [Fact(DisplayName = "GA-I-20 Homing failure surfaces as HomeLatchFailed")]
    public async Task Home_SensorDead_HomeLatchFailed()
    {
        await using var rig = new LiveRig(new MiniPlcOptions { HomedAtPowerUp = false });
        rig.Plc.Faults.HomeSensorDead = true;
        var track = await rig.ConnectedTrack();

        var ex = await Throws(() => track.Carriage.HomeAsync());

        ex.Error.Should().Be(MotionError.HomeLatchFailed);
        track.Carriage.State.Should().Be(AxisState.ErrorStop);
        rig.Plc.Truth.FaultCode.Should().Be(5);
    }

    [Fact(DisplayName = "GA-I-21 A reading outside the reading range blocks motion but not homing")]
    public async Task ReadingOutsideReadRange_MoveRefusedHomeSucceeds()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(o => o with { ReadMin = -20 }, power: true);
        rig.Plc.Teleport(-500);
        await DriverRig.Until(() => track.Carriage.Status.Position is < -499);

        var ex = await Throws(() => track.Carriage.MoveAbsoluteAsync(new Mm(100)));
        ex.Error.Should().Be(MotionError.NotHomed);
        ex.Message.Should().Contain("reading range");

        await track.Carriage.HomeAsync().WaitAsync(T);
        track.Carriage.Status.Position.Should().BeApproximately(0, 0.001);
    }

    [Fact(DisplayName = "GA-I-22 Relocated blocks work end to end")]
    public async Task RelocatedBases_AttachHomeMove_LowRegistersNeverWritten()
    {
        await using var rig = new LiveRig(new MiniPlcOptions { CommandBase = 200, StatusBase = 300 });
        var track = await rig.ConnectedTrack();

        await track.Carriage.HomeAsync().WaitAsync(T);
        await track.Carriage.MoveAbsoluteAsync(new Mm(1000), new MmPerS(500)).WaitAsync(T);

        track.Carriage.Status.Position.Should().BeApproximately(1000, 0.005);
        var written = rig.Plc.WrittenRegisters.ToArray();
        written.Should().Contain(r => r >= 200 && r <= 211, "anchor: the relocated command block was written");
        written.Should().NotContain(r => r < RegisterMap.CommandLength, "holding 0–11 of the default bases are never written");
    }

    [Fact(DisplayName = "GA-I-23 A rotary positioner moves in degrees")]
    public async Task Positioner_HomeAndMove_Degrees_NonShortestRefused()
    {
        await using var rig = new LiveRig(new MiniPlcOptions
        {
            TravelMin = -180, TravelMax = 180, MaxVelocity = 90, InitialPosition = 30, HomedAtPowerUp = false,
            HomingVelocity = 60,
        });
        var positioner = rig.Positioner();
        await positioner.ConnectAsync().WaitAsync(T);
        var turntable = positioner.Turntable;

        await turntable.HomeAsync().WaitAsync(T);
        await turntable.MoveAbsoluteAsync(Degree<double>.Create(90)).WaitAsync(T);

        ((double)turntable.Angle!.Value).Should().BeApproximately(90, 0.005);
        ((double)turntable.Max).Should().Be(180);
        var seq = rig.Plc.Truth.CommandSeq;
        (await Throws(() => turntable.MoveAbsoluteAsync(Degree<double>.Create(0), null, RotationSense.Negative)))
            .Error.Should().Be(MotionError.UnsupportedSense);
        await Task.Delay(200);
        rig.Plc.Truth.CommandSeq.Should().Be(seq);
    }

    [TimingFact(DisplayName = "GA-I-24 Status is published at the tick cadence (NFR-2)")]
    public async Task StatusChanged_IdleAndCruising_AtLeast5PerSecondAndPublishedValues()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack(power: true);
        var events = new ConcurrentQueue<(long At, AxisStatus Status)>();
        track.Carriage.StatusChanged += (_, s) => events.Enqueue((Stopwatch.GetTimestamp(), s));

        rig.Plc.ResetMaxScanGap();
        var idleStart = Stopwatch.GetTimestamp();
        await Task.Delay(2000);
        var move = track.Carriage.MoveAbsoluteAsync(new Mm(9000), new MmPerS(500));
        var cruiseStart = Stopwatch.GetTimestamp();
        await Task.Delay(2000);
        var end = Stopwatch.GetTimestamp();
        var gap = rig.Plc.MaxScanGap;
        await track.Carriage.StopAsync().WaitAsync(T);
        await move.Invoking(m => m.WaitAsync(T)).Should().ThrowAsync<Exception>();

        var all = events.ToArray();
        output.WriteLine($"GA-I-24 {all.Length} events in 4 s, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        Cadence.Budget(gap, () =>
        {
            foreach (var (from, name) in new[] { (idleStart, "idle"), (cruiseStart, "cruising") })
            {
                for (var w = 0; w < 2; w++)
                {
                    var lo = from + w * Stopwatch.Frequency;
                    var hi = Math.Min(lo + Stopwatch.Frequency, end);
                    all.Count(e => e.At >= lo && e.At < hi).Should().BeGreaterThanOrEqualTo(5, $"{name} window {w + 1}");
                }
            }
        });

        var positions = all.Where(e => e.Status.Position is not null).Select(e => e.Status.Position!.Value).ToArray();
        positions.Should().NotBeEmpty();
        positions.Should().OnlyContain(p => rig.Plc.WasPublished((int)Math.Round(p * 1000, MidpointRounding.AwayFromZero)));
    }
}
