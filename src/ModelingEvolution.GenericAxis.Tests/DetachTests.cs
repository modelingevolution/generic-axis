using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Unit — review #8: a verb the device detaches under fails with CommunicationLost naming the verb;
/// a caller's own cancellation stays OperationCanceledException (GA-U-37, SDK AC-10), and so does a Stop's (GA-U-75).
/// </summary>
public class DetachTests
{
    private static async Task<(DriverRig Rig, Task Move)> Moving()
    {
        var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100));
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.DiscreteMotion, "anchor: the move is in flight");
        return (rig, move);
    }

    /// <summary>Plays the PLC through a Stop: Stopping → Standstill on the next tick.</summary>
    private static async Task PumpUntil(DriverRig rig, Task task)
    {
        for (var i = 0; i < 50 && !task.IsCompleted; i++)
        {
            await rig.TickAsync();
            if (rig.Plc.State == 6) rig.Plc.State = 1;
        }
    }

    [Fact(DisplayName = "GA-U-73 DisconnectAsync during a verb fails it with CommunicationLost naming the verb")]
    public async Task Disconnect_VerbInFlight_CommunicationLostDetachedDuring()
    {
        var (rig, move) = await Moving();
        await using var _ = rig;

        var disconnect = rig.Device.DisconnectAsync();
        await PumpUntil(rig, disconnect);
        await disconnect.WaitAsync(DriverRig.RealTimeout);

        var ex = (await move.Awaiting(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<MotionException>())
            .Which;
        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().Be(
            "carriage: CommunicationLost: the device was detached during MoveAbsoluteAsync; MoveAbsoluteAsync did not complete.");
        rig.Plc.Writes.Should().Contain(w => w.Address == 0 && (w.Values[0] & 0x10) != 0,
            "anchor: the disconnect still wrote its Stop");
    }

    [Fact(DisplayName = "GA-U-74 Dispose (the in-process kill) during a verb fails it with CommunicationLost")]
    public async Task Dispose_VerbInFlight_CommunicationLost()
    {
        var (rig, move) = await Moving();
        await using var _ = rig;

        rig.Device.Dispose();

        var ex = (await move.Awaiting(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<MotionException>())
            .Which;
        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().Contain("detached during MoveAbsoluteAsync");
    }

    [Fact(DisplayName = "GA-U-75 control: a verb cancelled by StopAsync is OperationCanceledException, not CommunicationLost")]
    public async Task Stop_VerbInFlight_OperationCanceled()
    {
        var (rig, move) = await Moving();
        await using var _ = rig;

        var stop = rig.Axis.StopAsync();
        await PumpUntil(rig, stop);
        await stop.WaitAsync(DriverRig.RealTimeout);

        await move.Awaiting(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<OperationCanceledException>();
        rig.Device.IsConnected.Should().BeTrue();
    }
}
