using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// ADR-37 (review #54): a PLC that answers a request with Modbus exception 01/02/03 refused it as not served. That is
/// the PLC answering outside the map — Protocol / ProtocolMismatch, no retry — never a link fault. The likeliest
/// ADR-36 transition failure is a PLC whose input block is not mapped answering FC04 with exception 02. Every other
/// exception code, and a PLC that does not answer at all, stays Transport with the one retry.
/// </summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class ModbusRefusalTests
{
    private static async Task<MotionException> Throws(Func<Task> act) =>
        (await act.Invoking(a => a().WaitAsync(LiveRig.T)).Should().ThrowAsync<MotionException>()).Which;

    [Fact(DisplayName = "GA-I-27 A PLC refusing FC04 (exception 02) is ProtocolMismatch at attach: no retry, nothing written")]
    public async Task Connect_InputRegistersRefused_ProtocolMismatchNoRetryNothingWritten()
    {
        await using var rig = new LiveRig();
        rig.Plc.Faults.RefuseInputRegisters = true;
        var track = rig.Track();

        var ex = await Throws(() => track.ConnectAsync());

        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Protocol);
        ex.Message.Should().Be(
            "carriage: ProtocolMismatch: read status block: FC04 read S+0…S+14 = input 0…14 refused: Modbus exception 02 "
            + "(illegal data address) — the PLC does not serve the status block as input registers "
            + $"(127.0.0.1:{rig.Plc.Port} unit 1).");
        ex.Message.Should().Contain("FC04 read S+0…S+14 = input 0…14 refused: Modbus exception 02 (illegal data address) "
            + "— the PLC does not serve the status block as input registers", "protocol § Errors and debugging, ADR-37 example");
        ex.Message.Should().NotContainEquivalentOf("CommunicationLost");
        var logs = rig.Logs.GetSnapshot();
        logs.Should().Contain(r => r.Level == LogLevel.Error && r.Message.Contains(ex.Message));
        logs.Should().NotContain(r => r.Message.Contains("retrying once"), "a refusal is not retried");
        rig.Plc.InputReads.Should().Be(1, "exactly one FC04 was sent: a refusal is answered once, never retried");
        await Task.Delay(100);
        rig.Plc.WrittenRegisters.Should().BeEmpty("the pre-write read failed, so nothing was written");
        rig.Plc.Truth.LeaseOwner.Should().Be(0);
    }

    [Fact(DisplayName = "GA-I-28 A busy PLC (exception 04) stays Transport: one retry, then CommunicationLost")]
    public async Task Connect_ServerDeviceFailure_TransportAfterOneRetry()
    {
        await using var rig = new LiveRig();
        rig.Plc.Faults.InputRegistersException = 4;
        var track = rig.Track();

        var ex = await Throws(() => track.ConnectAsync());

        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().Contain("(FC04 read S+0…S+14 = input 0…14)").And.Contain("failed twice (reconnected once)");
        rig.Plc.InputReads.Should().Be(2, "the one retry");
        rig.Logs.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Warning && r.Message.Contains("retrying once"));
        rig.Plc.WrittenRegisters.Should().BeEmpty();
    }

    [Fact(DisplayName = "GA-I-29 A silent PLC stays Transport while a refusing one is Protocol (twin of GA-I-27)")]
    public async Task Connect_SilentPlc_TransportNotProtocol()
    {
        await using var rig = new LiveRig();
        rig.Plc.SetSilent(true);
        var track = rig.Track();

        var ex = await Throws(() => track.ConnectAsync());

        ex.Error.Should().Be(MotionError.CommunicationLost);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Transport);
        ex.Message.Should().Contain("failed twice (reconnected once)").And.NotContain("Modbus exception");
        rig.Plc.WrittenRegisters.Should().BeEmpty();
    }
}

/// <summary>ADR-37: which Modbus exception codes are a refusal (Protocol) and which a transient fault (Transport).</summary>
public class ModbusRefusalCodeTests
{
    [Theory(DisplayName = "GA-U-142 Exception 01/02/03 are refusals; 04/05/06/08/0A/0B keep the one retry")]
    [InlineData(0x01, true)]
    [InlineData(0x02, true)]
    [InlineData(0x03, true)]
    [InlineData(0x04, false)]
    [InlineData(0x05, false)]
    [InlineData(0x06, false)]
    [InlineData(0x08, false)]
    [InlineData(0x0A, false)]
    [InlineData(0x0B, false)]
    public void IsRefusal_ByExceptionCode(int code, bool refusal) =>
        ModbusChannel.IsRefusal((FluentModbus.ModbusExceptionCode)code).Should().Be(refusal);
}
