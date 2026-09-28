using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — Command handshake (GA-U-33 … GA-U-40) and GA-U-58.</summary>
public class HandshakeTests
{
    private static ushort[] W(params ushort[] values) => values;

    [Fact(DisplayName = "GA-U-33 Parameters go first, then command and sequence")]
    public async Task MoveAbsolute_WritesParametersThenCommandThenClearsEdge()
    {
        await using var rig = new DriverRig();
        rig.Plc.CommandAck = 41;
        await rig.ConnectAsync();
        rig.BehaveLikePlc();
        rig.Logs.Clear();
        var from = rig.Plc.OpCount;

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(2500), new MmPerS(100));
        await rig.TickAsync();
        rig.Plc.Set(p => { p.State = 1; p.Flags |= StatusFlags.InPosition; });
        await rig.TickAsync();
        await move.WaitAsync(DriverRig.RealTimeout);

        var writes = rig.Plc.CommandWritesSince(from);
        writes.Select(w => (w.Address, w.Values)).Should().BeEquivalentTo(new[]
        {
            ((ushort)2, W(0x25A0, 0x0026, 0x86A0, 0x0001, 0, 0)),
            ((ushort)0, W(0x0005, 42)),
            ((ushort)0, W(0x0001, 42)),
        }, o => o.WithStrictOrdering());
        writes.Should().OnlyContain(w => w.Lane == ChannelPriority.Move);

        var info = rig.LogsAt(LogLevel.Information).Select(r => r.Message).ToArray();
        info.Should().Contain(m => m.Contains("TargetPosition") && m.Contains("2500000") && m.Contains("100000"));
        info.Should().Contain(m => m.Contains("Command (C+0 = 0) = 0x0005") && m.Contains("CommandSeq (C+1 = 1) = 42"));
        info.Should().Contain(m => m.Contains("clear edge") && m.Contains("0x0001") && m.Contains("= 42"));
    }

    [Fact(DisplayName = "GA-U-34 A missing ack is NotAcknowledged and clears the edge")]
    public async Task Home_NoAck_CommunicationLostAfter500msAndEdgeCleared()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        var started = rig.Time.GetUtcNow();

        var home = rig.Axis.HomeAsync();
        rig.Plc.Command.Should().Be((ushort)(CommandBits.Enable | CommandBits.Home), "anchor: Home was written");
        for (var i = 0; i < 8 && !home.IsCompleted; i++) await rig.TickAsync();

        var ex = (await home.Invoking(h => h).Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(MotionError.NotAcknowledged);
        MotionErrorClasses.Of(ex).Should().Be(ErrorClass.Protocol);
        ex.Message.Should().Be("carriage: Protocol/NotAcknowledged: Home not accepted. CommandSeq 1 written, "
                               + "CommandAck 0 read after 500 ms, State 1 read.");
        (rig.Time.GetUtcNow() - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500))
            .And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(600));
        var last = rig.Plc.CommandWritesSince(0).Last();
        last.Address.Should().Be(0);
        last.Values.Should().Equal(0x0001, 1);
    }

    [Fact(DisplayName = "GA-U-35 Attach continues the sequence and clears stale edges")]
    public async Task Attach_StaleEdge_ClearedWithoutNewSequence_NextCommandContinues()
    {
        await using var rig = new DriverRig();
        rig.Plc.CommandAck = 900;
        rig.Plc[rig.Plc.Map.Command] = 0x0005;
        rig.Plc[rig.Plc.Map.CommandSeq] = 900;
        await rig.ConnectAsync();

        var attachWrite = rig.Plc.CommandWritesSince(0).Single(w => w.Address == 0);
        attachWrite.Values.Should().Equal(0x0001, 900);
        rig.Device.Engine.Sequence.Should().Be(900);

        rig.BehaveLikePlc();
        var stop = rig.Axis.StopAsync();
        rig.Plc.CommandSeq.Should().Be(901);
        await rig.TickAsync();
        await stop.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-36 Stop always reaches the wire")]
    public async Task Stop_Standstill_WrittenOnStopLane()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var from = rig.Plc.OpCount;

        var stop = rig.Axis.StopAsync();
        await rig.TickAsync();
        await stop.WaitAsync(DriverRig.RealTimeout);

        var writes = rig.Plc.CommandWritesSince(from);
        writes[0].Should().Match<ChannelOp>(w => w.Address == 0 && w.Values[0] == 0x0011 && w.Lane == ChannelPriority.Stop);
        writes[1].Should().Match<ChannelOp>(w => w.Address == 0 && w.Values[0] == 0x0001 && w.Lane == ChannelPriority.Stop);
    }

    [Fact(DisplayName = "GA-U-37 Cancelling a move stops it")]
    public async Task MoveAbsolute_Cancelled_StopWrittenOperationCanceled()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        using var cts = new CancellationTokenSource();

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(5000), new MmPerS(100), cts.Token);
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.DiscreteMotion);
        var from = rig.Plc.OpCount;

        await cts.CancelAsync();
        await DriverRig.Until(() => rig.Plc.CommandWritesSince(from).Any(w => (w.Values[0] & 0x10) != 0));
        rig.Plc.CommandWritesSince(from)[0].Should().Match<ChannelOp>(w =>
            w.Address == 0 && w.Values[0] == 0x0011 && w.Lane == ChannelPriority.Stop);
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.Stopping);
        rig.Plc.State = 1;
        await rig.TickAsync();

        await move.Invoking(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<OperationCanceledException>();
        rig.Axis.State.Should().Be(AxisState.Standstill);
    }

    [Fact(DisplayName = "GA-U-38 Home from Disabled energises first")]
    public async Task Home_Disabled_EnableThenStandstillThenHome()
    {
        await using var rig = new DriverRig();
        rig.Plc.State = 0;
        await rig.ConnectAsync();
        // The PLC acks Enable but reaches Standstill only when the test says so.
        rig.BehaveLikePlc(word => word == (ushort)CommandBits.Enable ? (ushort)0 : null);
        var from = rig.Plc.OpCount;

        var home = rig.Axis.HomeAsync();
        rig.Plc.CommandWritesSince(from).Should().ContainSingle().Which.Values.Should().Equal(0x0001, 1);
        await rig.TickAsync(3);
        rig.Plc.CommandWritesSince(from).Should().NotContain(w => (w.Values[0] & (ushort)CommandBits.Home) != 0,
            "Home waits for Standstill");

        rig.Plc.State = 1;
        await rig.TickAsync();
        rig.Plc.CommandWritesSince(from).Should().Contain(w => w.Address == 0 && w.Values[0] == 0x0003 && w.Values[1] == 2);
        rig.Plc.Set(p => { p.State = 1; p.Flags = StatusFlags.Homed; });
        await rig.TickAsync(2);
        await home.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-39 A move that stops outside the window fails")]
    public async Task MoveAbsolute_StandstillWithoutInPosition_MotionFailed()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(2500), new MmPerS(100));
        await rig.TickAsync();
        rig.Plc.Set(p => { p.State = 1; p.Flags = StatusFlags.Homed; });
        await rig.TickAsync();

        (await move.Invoking(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<MotionException>())
            .Which.Should().Match<MotionException>(e => e.Error == MotionError.MotionFailed
                                                        && e.Message.Contains("outside the in-position window"));
    }

    [Fact(DisplayName = "GA-U-39 control: InPosition completes the move")]
    public async Task MoveAbsolute_StandstillWithInPosition_Completes()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(2500), new MmPerS(100));
        await rig.TickAsync();
        rig.Plc.Set(p => { p.State = 1; p.Flags = StatusFlags.Homed | StatusFlags.InPosition; });
        await rig.TickAsync();

        await move.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-40 Reset clears what it owns")]
    public async Task Reset_OverlayWatchdogAndErrorStop_WatchdogZeroEnableZeroResetEdge()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        rig.Plc.Set(p => { p.State = 7; p.FaultCode = 4; p.WatchdogFault = 1; p.WatchdogTrips = 1; });
        rig.Plc.FailWhen = op => op.IsWrite && op.Address == rig.Plc.Map.Heartbeat;
        await rig.TickAsync();
        rig.Plc.FailWhen = null;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(MotionError.CommunicationLost, "anchor: the overlay is latched");
        var from = rig.Plc.OpCount;

        var reset = rig.Axis.ResetAsync();
        await rig.TickAsync(2);
        await reset.WaitAsync(DriverRig.RealTimeout);

        rig.Plc.CommandWritesSince(from).Select(w => (w.Address, w.Values)).Should().BeEquivalentTo(new[]
        {
            ((ushort)10, W(0)),
            ((ushort)0, W(0x0000, 1)),
            ((ushort)0, W(0x0020, 2)),
            ((ushort)0, W(0x0000, 2)),
        }, o => o.WithStrictOrdering());
        rig.Axis.State.Should().Be(AxisState.Disabled);
        rig.Axis.Status.Error.Should().BeNull("the overlay is gone and the PLC left ErrorStop");
    }

    [Fact(DisplayName = "GA-U-40 control: Reset before the link answers again refuses and keeps the overlay")]
    public async Task Reset_NoTickSinceLoss_Refused()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.FailWhen = op => op.IsWrite && op.Address == rig.Plc.Map.Heartbeat;
        await rig.TickAsync();

        var reset = () => rig.Axis.ResetAsync();
        (await DriverRig.Bounded(reset).Should().ThrowAsync<MotionException>()).Which.Error.Should().Be(MotionError.CommunicationLost);
        rig.Axis.Status.Error.Should().Be(MotionError.CommunicationLost);
    }

    [Fact(DisplayName = "GA-U-58 Power off stops first")]
    public async Task PowerOff_Moving_StopBeforeEnableZero()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        rig.Plc.State = 3;
        await rig.TickAsync();
        var from = rig.Plc.OpCount;

        var off = rig.Axis.PowerAsync(false);
        rig.Plc.CommandWritesSince(from)[0].Should().Match<ChannelOp>(w =>
            w.Values[0] == 0x0011 && w.Lane == ChannelPriority.Stop);
        await rig.TickAsync();
        rig.Plc.State = 1;
        await rig.TickAsync(2);
        await off.WaitAsync(DriverRig.RealTimeout);

        var writes = rig.Plc.CommandWritesSince(from);
        var stopIndex = writes.ToList().FindIndex(w => w.Values[0] == 0x0011);
        var enableZero = writes.ToList().FindIndex(w => w.Address == 0 && w.Values[0] == 0x0000);
        stopIndex.Should().BeLessThan(enableZero);
        rig.Axis.State.Should().Be(AxisState.Disabled);
    }

    [Fact(DisplayName = "GA-U-58 Power off from Standstill writes only Enable 0")]
    public async Task PowerOff_Standstill_OnlyEnableZero()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var from = rig.Plc.OpCount;

        var off = rig.Axis.PowerAsync(false);
        await rig.TickAsync();
        await off.WaitAsync(DriverRig.RealTimeout);

        rig.Plc.CommandWritesSince(from).Should().ContainSingle().Which.Values.Should().Equal(0x0000, 1);
        rig.Axis.State.Should().Be(AxisState.Disabled);
    }

    [Fact(DisplayName = "GA-U-58 Power off that never reaches Disabled is a DriveFault")]
    public async Task PowerOff_PlcStaysStandstill_DriveFaultAfterEnableTimeout()
    {
        await using var rig = await new DriverRig(o => o with { EnableTimeout = TimeSpan.FromSeconds(1) }).ConnectAsync();
        rig.BehaveLikePlc(_ => 1);

        var off = rig.Axis.PowerAsync(false);
        for (var i = 0; i < 15 && !off.IsCompleted; i++) await rig.TickAsync();

        (await off.Invoking(o => o).Should().ThrowAsync<MotionException>()).Which.Error.Should().Be(MotionError.DriveFault);
    }
}
