using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// protocol § Command semantics › Acknowledge (lead ruling 2026-09-29): at the 500 ms deadline the driver reads the
/// status block once more before declaring NotAcknowledged. The ticks here answer with a block that predates the write
/// (what a starved host produced: "CommandAck 5 read after 500 ms" came from the tick before the write).
/// </summary>
public class AckDeadlineTests
{
    private const string TickStatusRead = "read status block";

    private static ushort[] StatusWords(DriverRig rig) =>
        rig.Plc.StatusWords();

    [Fact(DisplayName = "GA-U-132 An ack the ticks never showed is found by the deadline read; the verb proceeds")]
    public async Task Power_TicksStale_AckedPlc_DeadlineReadFindsAck()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        rig.Plc.State = 0; // Disabled, so PowerAsync(true) writes Enable 1
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.Disabled, "anchor");
        var stale = StatusWords(rig); // State 0, CommandAck before the write
        var serveStale = true;
        rig.Plc.AnswerWith = op => serveStale && !op.IsWrite && op.What == TickStatusRead ? stale : null;
        rig.Plc.OnRead = (_, op) => { if (op.What.EndsWith("at the ack deadline")) serveStale = false; };
        var from = rig.Plc.OpCount;

        var power = rig.Axis.PowerAsync(true);
        for (var i = 0; i < 10 && !power.IsCompleted; i++) await rig.TickAsync();

        await power.WaitAsync(DriverRig.RealTimeout); // no NotAcknowledged
        rig.Axis.State.Should().Be(AxisState.Standstill);
        rig.Plc.Ops.Skip(from).Should().ContainSingle(o => o.What == "Enable 0 before a fresh Enable: read status block at the ack deadline");
    }

    [Fact(DisplayName = "GA-U-133 A missing ack states the CommandAck read at the deadline, not a stale tick's")]
    public async Task Home_TicksStale_NoAck_MessageStatesTheDeadlineRead()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        var stale = StatusWords(rig);
        stale[rig.Plc.Map.CommandAck - rig.Plc.Map.Status] = 999; // what a stale tick would say
        rig.Plc.AnswerWith = op => !op.IsWrite && op.What == TickStatusRead ? stale : null;

        var home = rig.Axis.HomeAsync();
        for (var i = 0; i < 8 && !home.IsCompleted; i++) await rig.TickAsync();

        var ex = (await home.Invoking(h => h.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(MotionError.NotAcknowledged);
        ex.Message.Should().Be("carriage: NotAcknowledged: Home not accepted. CommandSeq 1 written, "
                               + "CommandAck 0 read after 500 ms, State 1 read.");
        rig.Plc.CommandWritesSince(0).Last().Values.Should().Equal(0x0001, 1); // the edge is still cleared
    }
}
