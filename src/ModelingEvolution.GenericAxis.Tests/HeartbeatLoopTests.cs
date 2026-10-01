using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Unit — review #11: the heartbeat loop never dies silently. A malformed PLC answer is
/// ProtocolMismatch (latched, the #7 overlay); any other defect is logged at Error under its CLR type with no class
/// and no overlay; in both cases the beat continues (GA-U-79, GA-U-83, GA-U-84).
/// </summary>
public class HeartbeatLoopTests
{
    [Fact(DisplayName = "GA-U-79 A short status read is TickFailed/ProtocolMismatch, latches the overlay, and the beat continues")]
    public async Task ShortStatusRead_ProtocolMismatchOverlay_BeatContinues()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        var failures = new List<MotionException>();
        rig.Device.Heartbeat.TickFailed += (_, e) => failures.Add(e);
        rig.Logs.Clear();

        rig.Plc.AnswerWith = op => op.Address == rig.Plc.Map.Status ? new ushort[14] : null;
        await rig.TickAsync(3);

        failures.Should().HaveCount(3, "every malformed tick is reported").And
            .OnlyContain(e => e.Error == MotionError.ProtocolMismatch);
        failures[0].Message.Should().Be("carriage: ProtocolMismatch: the PLC answered a read of the status block with 14 "
                                        + "registers. Read status block (S+0…S+14 = input 100…114) = 14 registers, expected 15.");
        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch, "the class is not rewritten to CommunicationLost");
        rig.LogsAt(LogLevel.Error).Should().ContainSingle(r => r.Message.Contains("overlay ProtocolMismatch"),
            "the latch is logged once at Error");
        rig.Logs.GetSnapshot().Should().NotContain(r => r.Message.Contains("CommunicationLost"));

        rig.Plc.AnswerWith = null;
        var ticks = rig.Device.Heartbeat.TickCount;
        await rig.TickAsync();
        rig.Device.Heartbeat.TickCount.Should().Be(ticks + 1, "the loop is alive");
        await rig.Axis.ResetAsync().WaitAsync(DriverRig.RealTimeout);
        rig.Axis.Status.Error.Should().BeNull();
    }

    [Fact(DisplayName = "GA-U-83 A non-protocol defect in a tick is logged at Error under its type, with no class and no overlay")]
    public async Task UnexpectedException_ErrorUnderClrType_NoOverlay_BeatContinues()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        var failures = new List<MotionException>();
        rig.Device.Heartbeat.TickFailed += (_, e) => failures.Add(e);
        rig.Logs.Clear();

        rig.Plc.ThrowWhen = op => op.IsWrite && op.Address == rig.Plc.Map.Heartbeat
            ? new NotSupportedException("injected defect")
            : null;
        await rig.TickAsync(2);

        var errors = rig.LogsAt(LogLevel.Error);
        errors.Should().HaveCount(2).And.OnlyContain(r =>
            r.Message == "carriage: heartbeat tick threw System.NotSupportedException: injected defect; the beat continues"
            && r.Exception is NotSupportedException);
        failures.Should().BeEmpty("no MotionError class is claimed for a defect");
        rig.Axis.Status.Error.Should().BeNull("no overlay is manufactured");
        rig.Axis.State.Should().Be(AxisState.Standstill);

        rig.Plc.ThrowWhen = null;
        var ticks = rig.Device.Heartbeat.TickCount;
        await rig.TickAsync();
        rig.Device.Heartbeat.TickCount.Should().Be(ticks + 1, "the loop survived the defect");
    }

    [Fact(DisplayName = "GA-U-84 A short status read at attach refuses with ProtocolMismatch, logged once at Error, nothing written")]
    public async Task ShortStatusReadAtAttach_RefusedOnceAtError()
    {
        await using var rig = new DriverRig();
        rig.Plc.AnswerWith = op => op.Address == rig.Plc.Map.Status ? new ushort[16] : null;

        var ex = (await rig.Device.Awaiting(d => d.ConnectAsync().WaitAsync(DriverRig.RealTimeout))
            .Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        ex.Message.Should().EndWith("Read status block (S+0…S+14 = input 100…114) = 16 registers, expected 15.");
        rig.LogsAt(LogLevel.Error).Should().ContainSingle().Which.Message.Should().Contain(ex.Message);
        rig.Plc.Writes.Should().BeEmpty("a PLC that answers outside the protocol is never written to");
    }
}
