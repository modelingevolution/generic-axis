using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — Lease and heartbeat (GA-U-06 … GA-U-12).</summary>
public class LeaseAndHeartbeatTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Theory(DisplayName = "GA-U-06 The lease vectors hold")]
    [InlineData((ushort)0, 0.0, true)]
    [InlineData((ushort)0, 5.0, true)]
    [InlineData((ushort)7, 0.0, true)]
    [InlineData((ushort)3, 0.2, false)]
    [InlineData((ushort)3, 1.0, true)]
    [InlineData((ushort)3, 5.0, true)]
    public void Evaluate_Vectors(ushort owner, double ageSeconds, bool granted)
    {
        var decision = AdvisoryLease.Evaluate(owner, TimeSpan.FromSeconds(ageSeconds), TimeSpan.FromSeconds(1), 7);

        decision.Granted.Should().Be(granted, decision.Reason);
        decision.Owner.Should().Be(owner);
        if (!granted) decision.Reason.Should().Contain("LeaseOwner is 3");
    }

    [Fact(DisplayName = "GA-U-06 exactly one window minus a tick is still refused")]
    public void Evaluate_JustUnderExpiry_Refused() =>
        AdvisoryLease.Evaluate(3, TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1), 7)
            .Granted.Should().BeFalse();

    [Fact(DisplayName = "GA-U-07 A live foreign lease is refused at the timeout")]
    public async Task Acquire_ForeignOwnerBeating_RefusedAfterTimeoutWithoutWriting()
    {
        var time = new FakeTimeProvider();
        var plc = new FakePlcChannel { LeaseOwner = 3 };
        // The incumbent beats: every observation (one per 100 ms of fake time) sees a new value.
        plc.OnRead = (p, op) => { if (op.Address == p.Map.Heartbeat) p.Heartbeat = AxisHeartbeat.NextBeat(p.Heartbeat); };
        var heartbeat = new AxisHeartbeat("carriage", plc, 1, RegisterMap.Default, 7, Interval, null, time);
        var started = time.GetUtcNow();

        var acquire = heartbeat.AcquireAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        await Pump(time, plc, () => acquire.IsCompleted);

        var ex = await acquire.Invoking(a => a).Should().ThrowAsync<MotionException>();
        ex.Which.Error.Should().Be(MotionError.LeaseHeld);
        ex.Which.Message.Should().StartWith("carriage: LeaseHeld: ")
            .And.Contain("Read LeaseOwner (C+9 = holding 9) = 3, expected 0 or 7");
        (time.GetUtcNow() - started).Should().BeCloseTo(TimeSpan.FromSeconds(3), Interval);
        plc.Writes.Should().NotContain(w => w.Address == plc.Map.LeaseOwner, "a refused lease writes nothing");
        plc.Ops.Count(o => !o.IsWrite && o.Address == plc.Map.Heartbeat).Should().BeGreaterThan(20,
            "the incumbent was watched continuously, every interval");
    }

    [Fact(DisplayName = "GA-U-08 A stale foreign lease is taken after one unchanged second")]
    public async Task Acquire_ForeignOwnerStale_TakenAfterOneSecond()
    {
        var time = new FakeTimeProvider();
        var plc = new FakePlcChannel { LeaseOwner = 3, Heartbeat = 1234 };
        var t0 = time.GetUtcNow();
        DateTimeOffset? writtenAt = null;
        plc.OnWrite = (p, op) => { if (op.Address == p.Map.LeaseOwner) writtenAt = time.GetUtcNow(); };
        var heartbeat = new AxisHeartbeat("carriage", plc, 1, RegisterMap.Default, 7, Interval, null, time);

        var acquire = heartbeat.AcquireAsync(null, CancellationToken.None);
        await Pump(time, plc, () => acquire.IsCompleted);

        var decision = await acquire;
        decision.Granted.Should().BeTrue();
        plc.LeaseOwner.Should().Be(7);
        writtenAt.Should().NotBeNull();
        (writtenAt!.Value - t0).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1), "not before one full window")
            .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(1) + Interval, "within one tick of expiry");
    }

    [Fact(DisplayName = "GA-U-08 control: an incumbent that releases is replaced at once")]
    public async Task Acquire_ForeignOwnerReleases_TakenOnNextObservation()
    {
        var time = new FakeTimeProvider();
        var plc = new FakePlcChannel { LeaseOwner = 3 };
        var reads = 0;
        plc.OnRead = (p, op) =>
        {
            if (op.Address != p.Map.Heartbeat) return;
            p.Heartbeat = AxisHeartbeat.NextBeat(p.Heartbeat);
            if (++reads == 3) p.LeaseOwner = 0;
        };
        var heartbeat = new AxisHeartbeat("carriage", plc, 1, RegisterMap.Default, 7, Interval, null, time);
        var t0 = time.GetUtcNow();

        var acquire = heartbeat.AcquireAsync(null, CancellationToken.None);
        await Pump(time, plc, () => acquire.IsCompleted);

        (await acquire).Granted.Should().BeTrue();
        (time.GetUtcNow() - t0).Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "GA-U-09 A tick is beat, watchdog read, status read, in the heartbeat lane")]
    public async Task TickOnce_WritesBeatThenReadsWatchdogThenStatus()
    {
        var plc = new FakePlcChannel { MapVersion = 1, LeaseOwner = 7, WatchdogTrips = 2, State = 1 };
        var heartbeat = new AxisHeartbeat("carriage", plc, 1, RegisterMap.Default, 7, Interval, null, new FakeTimeProvider());
        PlcSnapshot? raised = null;
        heartbeat.Ticked += (_, s) => raised = s;

        var snapshot = await heartbeat.TickOnceAsync(CancellationToken.None);

        var ops = plc.Ops;
        ops.Should().HaveCount(3);
        ops[0].Should().Match<ChannelOp>(o => o.IsWrite && o.Address == 8 && o.Values.Length == 1 && o.Values[0] != 0);
        ops[1].Should().Match<ChannelOp>(o => o.IsHoldingRead && o.Address == 9 && o.Count == 3, "FC03 read C+9…C+11");
        ops[2].Should().Match<ChannelOp>(o => o.IsInputRead && o.Address == RegisterMap.Default.Status && o.Count == 15,
            "FC04 read S+0…S+14 (ADR-36)");
        ops.Should().OnlyContain(o => o.Lane == ChannelPriority.Heartbeat);
        raised.Should().Be(snapshot);
        snapshot.LeaseOwner.Should().Be(7);
        snapshot.WatchdogTrips.Should().Be(2);
        snapshot.Status.MapVersion.Should().Be(1);
    }

    [Fact(DisplayName = "GA-U-10 A failed tick is not fatal")]
    public async Task Tick_ChannelFailsOnce_NextTickBeatsAgainAndOverlayShows()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        var failures = 0;
        rig.Device.Heartbeat.TickFailed += (_, _) => Interlocked.Increment(ref failures);

        rig.Plc.FailWhen = op => op.IsWrite && op.Address == rig.Plc.Map.Heartbeat;
        await rig.TickAsync();
        await DriverRig.Until(() => Volatile.Read(ref failures) == 1);
        rig.Plc.FailWhen = null;
        var beatsBefore = rig.Plc.Writes.Count(w => w.Address == rig.Plc.Map.Heartbeat);

        await rig.TickAsync();

        rig.Plc.Writes.Count(w => w.Address == rig.Plc.Map.Heartbeat).Should().Be(beatsBefore + 1, "the loop beat again");
        failures.Should().Be(1);
        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.CommunicationLost);
    }

    [Fact(DisplayName = "GA-U-11 Release writes 0 only over its own id")]
    public async Task Stop_ReleasesOnlyOwnLease()
    {
        var plc = new FakePlcChannel();
        var heartbeat = new AxisHeartbeat("carriage", plc, 1, RegisterMap.Default, 7, Interval, null, new FakeTimeProvider());
        await heartbeat.AcquireAsync(null, CancellationToken.None);
        plc.LeaseOwner.Should().Be(7, "anchor: the lease was taken");
        plc.LeaseOwner = 5;
        var writesBefore = plc.Writes.Count;

        await heartbeat.StopAsync();

        plc.Writes.Count.Should().Be(writesBefore, "a foreign LeaseOwner is not ours to clear");
        plc.LeaseOwner.Should().Be(5);

        var own = new FakePlcChannel();
        var mine = new AxisHeartbeat("carriage", own, 1, RegisterMap.Default, 7, Interval, null, new FakeTimeProvider());
        await mine.AcquireAsync(null, CancellationToken.None);
        await mine.StopAsync();
        own.Writes.Last().Should().Match<ChannelOp>(w => w.Address == own.Map.LeaseOwner && w.Values[0] == 0);
        own.LeaseOwner.Should().Be(0);
    }

    [Fact(DisplayName = "GA-U-12 A foreign owner seen in a tick latches LeaseHeld")]
    public async Task Tick_ForeignLeaseOwner_LatchesLeaseHeld()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.Standstill, "anchor: healthy before the takeover");

        rig.Plc.LeaseOwner = 9;
        await rig.TickAsync();

        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.LeaseHeld);
        rig.LogsAt(LogLevel.Error).Should().Contain(r => r.Message.Contains("9"));

        var reset = () => rig.Axis.ResetAsync();
        (await DriverRig.Bounded(reset).Should().ThrowAsync<MotionException>()).Which.Error.Should().Be(MotionError.LeaseHeld);
        rig.Plc.LeaseOwner = 1;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(MotionError.LeaseHeld, "only a reconnect clears a lost lease");

        var writes = rig.Plc.CommandWritesSince(0).Count;
        var move = () => rig.Linear.MoveAbsoluteAsync(new(2000));
        (await DriverRig.Bounded(move).Should().ThrowAsync<MotionException>()).Which.Error.Should().Be(MotionError.LeaseHeld);
        rig.Plc.CommandWritesSince(0).Count.Should().Be(writes, "a refusal writes nothing");
    }

    [Fact(DisplayName = "GA-U-12b An unowned lease seen in a tick is re-taken in place, along the attach path (issue #7)")]
    public async Task Tick_LeaseOwnerZero_RetakesLeaseClearsWatchdogContinuesFromAck()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.Standstill, "anchor: healthy before the restart");

        // The PLC restarts: command block cleared, Disabled, not homed, CommandAck 0.
        rig.Plc.Set(p =>
        {
            p.LeaseOwner = 0;
            p.WatchdogFault = 0;
            p.State = 0;
            p.Flags = StatusFlags.DriveReady;
            p.CommandAck = 0;
        });
        var from = rig.Plc.OpCount;
        await rig.TickAsync();

        var writes = rig.Plc.Ops.Skip(from).Where(o => o.IsWrite && o.Address != rig.Plc.Map.Heartbeat).ToArray();
        writes.Select(w => (w.Address, w.Values[0])).Should().Equal(
            [(rig.Plc.Map.LeaseOwner, DriverRig.Owner), (rig.Plc.Map.WatchdogFault, 0), (rig.Plc.Map.Command, 0)],
            "own id, then WatchdogFault 0, then the attach's [Command = 0 (Disabled), CommandSeq = CommandAck]");
        writes[2].Values.Should().Equal([(ushort)0, (ushort)0]);
        rig.Plc.LeaseOwner.Should().Be(DriverRig.Owner);
        rig.Device.IsConnected.Should().BeTrue();
        rig.Axis.State.Should().Be(AxisState.Disabled);
        rig.Axis.Status.Error.Should().BeNull();
        rig.Device.Engine.Sequence.Should().Be(0, "the sequence continues from the restarted PLC's CommandAck");
        rig.LogsAt(LogLevel.Warning).Should().ContainSingle(r => r.Message.Contains("LeaseOwner (C+9 = holding 9) = 0"));
        rig.LogsAt(LogLevel.Error).Should().BeEmpty();

        await rig.TickAsync(3);
        rig.Plc.Writes.Count(w => w.Address == rig.Plc.Map.LeaseOwner).Should().Be(2, "taken at attach, re-taken once");
    }

    [Fact(DisplayName = "GA-U-12c A re-take onto a PLC with another map version writes nothing and latches ProtocolMismatch")]
    public async Task Tick_LeaseOwnerZeroWrongMapVersion_RefusedNothingWritten()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        await rig.TickAsync();
        var failures = 0;
        rig.Device.Heartbeat.TickFailed += (_, _) => Interlocked.Increment(ref failures);

        rig.Plc.Set(p =>
        {
            p.LeaseOwner = 0;
            p.MapVersion = 2;
        });
        var from = rig.Plc.OpCount;
        rig.Time.Advance(rig.Options.HeartbeatInterval);
        await DriverRig.Until(() => Volatile.Read(ref failures) == 1);
        await DriverRig.Settle();

        rig.Plc.Ops.Skip(from).Where(o => o.IsWrite && o.Address != rig.Plc.Map.Heartbeat).Should().BeEmpty();
        rig.Plc.LeaseOwner.Should().Be(0);
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);
        rig.LogsAt(LogLevel.Error).Should().Contain(r => r.Message.Contains("lease re-take refused; nothing was written"));
    }

    /// <summary>Advances fake time one interval at a time, waiting for the loop's next read before the next step.</summary>
    private static async Task Pump(FakeTimeProvider time, FakePlcChannel plc, Func<bool> done)
    {
        for (var step = 0; step < 200 && !done(); step++)
        {
            var ops = plc.OpCount;
            time.Advance(Interval);
            await DriverRig.Until(() => done() || plc.OpCount > ops);
            await Task.Delay(10);
        }
    }
}
