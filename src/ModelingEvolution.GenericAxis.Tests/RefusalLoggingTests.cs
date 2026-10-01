using FluentAssertions;
using FluentModbus;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// Protocol rule 3 (ADR-37, review #58): a refusal is never retried, so a refusing PLC fails every 100 ms tick. A
/// refusal identical to the previous tick's logs at Warning once, then at Debug until the cause changes or the link
/// recovers; the latched error itself is logged once at Error.
/// </summary>
public class RefusalLoggingTests
{
    private const string Failed = "heartbeat tick failed";
    private const string Again = "heartbeat tick refused again";

    [Fact(DisplayName = "GA-U-143 A repeated refusal logs Warning once, then Debug; a changed code or a recovery warns again")]
    public async Task Tick_RepeatedRefusal_WarningOnceThenDebug()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        ModbusExceptionCode? refuse = null;
        rig.Plc.ThrowWhen = op => op.IsInputRead && refuse is { } code
            ? ModbusChannel.Refusal("carriage", "read status block", "(FC04 read S+0…S+14 = input 0…14)", code,
                "fake-plc", 502, 1)
            : null;
        rig.Logs.Clear();

        (int Warnings, int Debugs) Count() => (
            rig.LogsAt(LogLevel.Warning).Count(r => r.Message.Contains(Failed)),
            rig.Logs.GetSnapshot().Count(r => r.Level == LogLevel.Debug && r.Message.Contains(Again)));

        refuse = ModbusExceptionCode.IllegalDataAddress;
        await rig.TickAsync();
        Count().Should().Be((1, 0), "the first refusal is news");
        await rig.TickAsync();
        Count().Should().Be((1, 1), "the same refusal on the next tick drops to Debug");
        await rig.TickAsync(3);
        Count().Should().Be((1, 4), "and stays at Debug while it repeats");

        refuse = ModbusExceptionCode.IllegalFunction;
        await rig.TickAsync();
        Count().Should().Be((2, 4), "a different refusal code is a changed cause");
        await rig.TickAsync();
        Count().Should().Be((2, 5));

        refuse = null;
        await rig.TickAsync();
        Count().Should().Be((2, 5), "anchor: a served tick logs no failure");
        refuse = ModbusExceptionCode.IllegalFunction;
        await rig.TickAsync();
        Count().Should().Be((3, 5), "after the link recovered, the same refusal is news again");

        rig.LogsAt(LogLevel.Error).Where(r => r.Message.Contains("ProtocolMismatch")).Should()
            .ContainSingle("the latched overlay is logged once at Error");
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);
    }

    [Fact(DisplayName = "GA-U-144 A repeated non-refusal tick failure still warns every tick")]
    public async Task Tick_RepeatedTransportFailure_WarnsEachTick()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.FailWhen = op => op.IsInputRead;
        rig.Logs.Clear();

        await rig.TickAsync(3);

        rig.LogsAt(LogLevel.Warning).Count(r => r.Message.Contains(Failed)).Should().Be(3,
            "the Debug drop is scoped to refusals; a Transport tick failure has its own retry pause and warns");
    }
}
