using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// bug-076: one STATION STOP became 73 concurrent halts, each calling <c>StopAsync</c> on the carriage. Every call wrote
/// its own <c>[Stop, CommandSeq]</c> on the one command block; the PLC acknowledges only the sequence it last saw, so
/// every earlier caller failed with NotAcknowledged although the axis stopped. One command is in flight per axis: a
/// second Stop joins the pending one, and nothing else is written while a Stop is pending.
/// The fake PLC here (<see cref="DriverRig.BehaveLikePlc"/>) acks the last <c>CommandSeq</c> it saw, as the real one did.
/// </summary>
public class StopStormTests
{
    private const ushort StopBit = (ushort)CommandBits.Stop;

    private static IReadOnlyList<ChannelOp> StopWrites(DriverRig rig, int from) =>
        rig.Plc.CommandWritesSince(from).Where(w => w.Address == rig.Plc.Map.Command && (w.Values[0] & StopBit) != 0)
            .ToArray();

    private static async Task<Exception?> Outcome(Task task)
    {
        try
        {
            await task.WaitAsync(DriverRig.RealTimeout);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Plays the PLC through a Stop (Stopping → Standstill on the next tick) until every task completed.</summary>
    private static async Task Pump(DriverRig rig, params Task[] tasks)
    {
        for (var i = 0; i < 20 && !tasks.All(t => t.IsCompleted); i++)
        {
            await rig.TickAsync();
            if (rig.Plc.State == 6) rig.Plc.State = 1;
        }
    }

    /// <summary>Starts <paramref name="callers"/> concurrent StopAsync calls and returns once every call has reached the
    /// engine (written its Stop or joined one).</summary>
    private static async Task<Task[]> ConcurrentStops(DriverRig rig, int callers)
    {
        var issued = 0;
        var stops = Enumerable.Range(0, callers).Select(_ => Task.Run(() =>
        {
            var stop = rig.Axis.StopAsync();
            Interlocked.Increment(ref issued);
            return stop;
        })).ToArray();
        await DriverRig.Until(() => Volatile.Read(ref issued) == callers, "every StopAsync call issued");
        return stops;
    }

    private static int NotAcknowledged(IEnumerable<Exception?> outcomes) =>
        outcomes.Count(o => o is MotionException { Error: MotionError.NotAcknowledged });

    [Fact(DisplayName = "bug-076 73 concurrent StopAsync on a moving axis: one Stop on the wire, every caller stopped")]
    public async Task StopAsync_73ConcurrentCallers_OneStopWriteAllSucceedNoNotAcknowledged()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        rig.Plc.State = 3; // DiscreteMotion: the carriage was moving
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.DiscreteMotion, "anchor: the axis is moving");
        var from = rig.Plc.OpCount;

        var stops = await ConcurrentStops(rig, 73);
        await Pump(rig, stops);
        var outcomes = await Task.WhenAll(stops.Select(Outcome));

        var failed = outcomes.Count(o => o is not null);
        StopWrites(rig, from).Should().ContainSingle(
            "N concurrent Stops are one Stop on the command registers ({0} Stop writes, {1} of {2} callers failed, "
            + "{3} NotAcknowledged)", StopWrites(rig, from).Count, failed, stops.Length, NotAcknowledged(outcomes));
        NotAcknowledged(outcomes).Should().Be(0);
        outcomes.Should().OnlyContain(o => o == null, "every caller gets the one Stop's result: stopped");
        rig.Axis.State.Should().Be(AxisState.Standstill);
    }

    [Fact(DisplayName = "bug-076 concurrent StopAsync joining a Stop that fails: every caller gets that failure, one Stop on the wire")]
    public async Task StopAsync_ConcurrentCallers_PendingStopFails_EveryCallerGetsTheSameFailure()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        // No OnCommand: the PLC never acknowledges. A joined failure must stay a failure.
        rig.Plc.State = 3;
        await rig.TickAsync();
        var from = rig.Plc.OpCount;

        var stops = await ConcurrentStops(rig, 10);
        await Pump(rig, stops);
        var outcomes = await Task.WhenAll(stops.Select(Outcome));

        StopWrites(rig, from).Should().ContainSingle();
        outcomes.Should().OnlyContain(o => o is MotionException && ((MotionException)o).Error == MotionError.NotAcknowledged,
            "the pending Stop's failure is every joiner's result, never turned into success");
        outcomes.Select(o => o!.Message).Distinct().Should().ContainSingle("they all joined one Stop");
    }

    [Fact(DisplayName = "bug-076 a StopAsync after the earlier Stop finished sends a new Stop")]
    public async Task StopAsync_AfterEarlierStopCompleted_SendsANewStop()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var from = rig.Plc.OpCount;

        var first = rig.Axis.StopAsync();
        await Pump(rig, first);
        await first.WaitAsync(DriverRig.RealTimeout);
        StopWrites(rig, from).Should().ContainSingle("anchor: the first Stop is on the wire");

        var second = rig.Axis.StopAsync();
        StopWrites(rig, from).Should().HaveCount(2, "a Stop after a finished Stop is written at once, not joined");
        await Pump(rig, second);
        await second.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "bug-076 Stop while a move waits for its ack: the Stop is written at once and pre-empts the move")]
    public async Task StopAsync_MoveAwaitingAck_StopWrittenImmediatelyMoveCancelled()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        // The PLC is slow to ack the move; everything else it acks at once (the sequence it last saw).
        rig.Plc.OnCommand = (plc, word, seq) =>
        {
            if (seq == plc.CommandAck || (word & (ushort)CommandBits.MoveAbsolute) != 0) return;
            plc.CommandAck = seq;
        };
        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100));
        var moveSeq = rig.Plc.CommandSeq;
        rig.Plc.Command.Should().Be((ushort)(CommandBits.Enable | CommandBits.MoveAbsolute), "anchor: the move is written");
        var from = rig.Plc.OpCount;

        var stop = rig.Axis.StopAsync();

        // Before any tick, before any time passes: the Stop does not queue behind the move's 500 ms ack wait.
        var stopSeq = AxisEngine.NextSeq(moveSeq);
        rig.Plc.CommandWritesSince(from).Should().NotBeEmpty();
        rig.Plc.CommandWritesSince(from)[0].Should().Match<ChannelOp>(w => w.Address == 0 && w.Values[0] == 0x0011
            && w.Values[1] == stopSeq && w.Lane == ChannelPriority.Stop);

        await Pump(rig, stop, move);
        await stop.WaitAsync(DriverRig.RealTimeout);
        await move.Awaiting(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<OperationCanceledException>();
        rig.Plc.CommandWritesSince(from).Select(w => (w.Values[0], w.Values[1])).Should().Equal(
            [(0x0011, stopSeq), (0x0001, stopSeq)], "after the Stop only its own clear edge is written");
    }

    [Fact(DisplayName = "bug-076 Stop during a move's ack-deadline read: the move writes nothing after the Stop")]
    public async Task StopAsync_DuringMoveAckDeadlineRead_NoStaleClearEdgeAfterStop()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.OnCommand = (plc, word, seq) =>
        {
            if (seq == plc.CommandAck || (word & (ushort)CommandBits.MoveAbsolute) != 0) return;
            plc.CommandAck = seq;
        };
        Task? stop = null;
        var from = 0;
        rig.Plc.OnRead = (plc, op) =>
        {
            if (stop is not null || !op.What.Contains("ack deadline")) return;
            from = plc.OpCount;
            stop = rig.Axis.StopAsync(); // the STOP lands while the move reads the status block at its deadline
        };

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100));
        for (var i = 0; i < 20 && stop is null; i++) await rig.TickAsync();
        stop.Should().NotBeNull("anchor: the move reached its ack-deadline read");
        var stopSeq = rig.Plc.CommandSeq;
        await Pump(rig, stop!, move);

        (await Outcome(stop!)).Should().BeNull("the Stop is acknowledged; nothing overwrote its sequence");
        (await Outcome(move)).Should().BeOfType<OperationCanceledException>("the Stop pre-empted the move");
        rig.Plc.CommandWritesSince(from).Select(w => (w.Values[0], w.Values[1])).Should().Equal(
            [(0x0011, stopSeq), (0x0001, stopSeq)], "the move does not clear its edge with a stale sequence after the Stop");
    }

    [Fact(DisplayName = "bug-076 Stop cancels a move's ack-deadline read: the move writes nothing after the Stop")]
    public async Task StopAsync_CancelsMoveAckDeadlineRead_NoStaleClearEdgeAfterStop()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.OnCommand = (plc, word, seq) =>
        {
            if (seq == plc.CommandAck || (word & (ushort)CommandBits.MoveAbsolute) != 0) return;
            plc.CommandAck = seq;
        };
        Task? stop = null;
        var from = 0;
        // The deadline read was still queued when the STOP came: the Stop cancels it (the real PriorityGate drops a
        // queued waiter whose token is cancelled).
        rig.Plc.ThrowWhen = op =>
        {
            if (stop is not null || !op.What.Contains("ack deadline")) return null;
            from = rig.Plc.OpCount;
            stop = rig.Axis.StopAsync();
            return new OperationCanceledException();
        };

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100));
        for (var i = 0; i < 20 && stop is null; i++) await rig.TickAsync();
        stop.Should().NotBeNull("anchor: the move reached its ack-deadline read");
        var stopSeq = rig.Plc.CommandSeq;
        await Pump(rig, stop!, move);

        (await Outcome(stop!)).Should().BeNull("the Stop is acknowledged; nothing overwrote its sequence");
        (await Outcome(move)).Should().BeAssignableTo<OperationCanceledException>();
        rig.Plc.CommandWritesSince(from).Select(w => (w.Values[0], w.Values[1])).Should().Equal(
            [(0x0011, stopSeq), (0x0001, stopSeq)], "the move does not clear its edge with a stale sequence after the Stop");
    }

    [Fact(DisplayName = "bug-076 the disconnect's Enable 0 while a Stop is pending goes after the Stop, never over it")]
    public async Task DisableForDetach_WhileStopPending_WrittenAfterTheStopCompletes()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var from = rig.Plc.OpCount;

        var stop = rig.Axis.StopAsync();
        var stopSeq = rig.Plc.CommandSeq;
        var disable = rig.Device.Engine.DisableForDetachAsync(CancellationToken.None);
        rig.Plc.CommandWritesSince(from).Should().ContainSingle("Enable 0 waits while the Stop is pending");
        await Pump(rig, stop, disable);

        (await Outcome(stop)).Should().BeNull();
        (await Outcome(disable)).Should().BeNull();
        rig.Plc.CommandWritesSince(from).Select(w => (w.Values[0], w.Values[1])).Should().Equal(
            [(0x0011, stopSeq), (0x0001, stopSeq), (0x0000, AxisEngine.NextSeq(stopSeq))],
            "one command in flight: the Stop's handshake completes before the next command word");
    }

    [Fact(DisplayName = "bug-076 a move commanded while a Stop waits for its ack is refused; nothing overwrites the Stop")]
    public async Task MoveAbsolute_WhileStopPending_RefusedBusyStopSucceeds()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var from = rig.Plc.OpCount;

        var stop = rig.Axis.StopAsync(); // at Standstill: written, waiting for the tick that shows the ack
        StopWrites(rig, from).Should().ContainSingle("anchor: the Stop is pending");
        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100));
        await Pump(rig, stop, move);

        (await Outcome(stop)).Should().BeNull();
        var refused = (await Outcome(move)).Should().BeOfType<MotionException>().Which;
        refused.Error.Should().Be(MotionError.Busy);
        refused.Message.Should().Contain("Stop");
        rig.Plc.CommandWritesSince(from).Should().OnlyContain(w => w.Address == 0 && (w.Values[0] & ~0x0011) == 0,
            "no move parameter or move command was written while the Stop was pending");
        rig.LogsAt(LogLevel.Warning).Should().Contain(r => r.Message.Contains("Nothing was written"));
    }
}
