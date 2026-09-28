using FluentAssertions;
using ModelingEvolution.Drawing;
using ModelingEvolution.Drawing.Units;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — Guards (GA-U-24 … GA-U-32). Each refusal writes nothing.</summary>
public class GuardTests
{
    private static async Task<MotionException> Refused(DriverRig rig, Func<Task> act, MotionError error)
    {
        var before = rig.Plc.CommandWritesSince(0).Count;
        var ex = (await DriverRig.Bounded(act).Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(error, ex.Message);
        rig.Plc.CommandWritesSince(0).Count.Should().Be(before, "a refusal writes nothing");
        return ex;
    }

    [Fact(DisplayName = "GA-U-24 Busy while another verb runs")]
    public async Task Move_WhileMoveRuns_BusyNamesRunningVerb()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var first = rig.Linear.MoveAbsoluteAsync(new Mm(2000), new MmPerS(100));
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.DiscreteMotion, "anchor: the first move is running");

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(3000)), MotionError.Busy);
        ex.Message.Should().Contain("MoveAbsoluteAsync is still running");

        rig.Plc.Flags |= StatusFlags.InPosition;
        rig.Plc.State = 1;
        await rig.TickAsync();
        await first.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-25 ErrorStop names the remedy")]
    public async Task Move_InErrorStop_BusySaysCallReset()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.State = 7;
        rig.Plc.FaultCode = 1;
        await rig.TickAsync();

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000)), MotionError.Busy);
        ex.Message.Should().Contain("call ResetAsync");
    }

    [Fact(DisplayName = "GA-U-26 Disabled refuses moves and names the remedy")]
    public async Task Move_Disabled_BusySaysPowerOnOrHome()
    {
        await using var rig = new DriverRig();
        rig.Plc.State = 0;
        await rig.ConnectAsync();

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000)), MotionError.Busy);
        ex.Message.Should().Contain("power on or home first");
    }

    [Fact(DisplayName = "GA-U-27 Absolute and relative moves need Homed")]
    public async Task Move_Unhomed_AbsoluteRelativeRefusedVelocityWritten()
    {
        await using var rig = new DriverRig();
        rig.Plc.Flags = StatusFlags.DriveReady;
        await rig.ConnectAsync();
        rig.BehaveLikePlc();

        await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000)), MotionError.NotHomed);
        await Refused(rig, () => rig.Linear.MoveRelativeAsync(new Mm(10)), MotionError.NotHomed);

        var jog = rig.Linear.MoveVelocityAsync(new MmPerS(-20));
        rig.Plc.Command.Should().Be((ushort)(CommandBits.Enable | CommandBits.MoveVelocity));
        rig.Plc.GetInt(rig.Plc.Map.Velocity).Should().Be(-20_000);
        await rig.TickAsync();
        await jog.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-28 The reading range gates motion")]
    public async Task Move_ReadingOutsideReadRange_NotHomed_InsideAccepted()
    {
        await using var rig = new DriverRig(o => o with { ReadMin = -20 });
        rig.Plc.ActualPosition = -25_000;
        await rig.ConnectAsync();
        rig.BehaveLikePlc();

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(100)), MotionError.NotHomed);
        ex.Message.Should().Contain("outside the reading range").And.Contain("home the axis first");
        await Refused(rig, () => rig.Linear.MoveVelocityAsync(new MmPerS(10)), MotionError.NotHomed);

        rig.Plc.ActualPosition = -2_980;
        await rig.TickAsync();
        var move = rig.Linear.MoveAbsoluteAsync(new Mm(100));
        rig.Plc.Command.Should().Be((ushort)(CommandBits.Enable | CommandBits.MoveAbsolute), "−2.98 mm is inside −20..10000");
        rig.Plc.Set(p => { p.State = 1; p.Flags |= StatusFlags.InPosition; });
        await rig.TickAsync(2);
        await move.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-29 Targets outside travel are refused, not clamped")]
    public async Task Move_TargetOutsideTravel_OutOfRangeNamesLimitsAndSource()
    {
        await using var rig = await new DriverRig().ConnectAsync();

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(10_000.001)), MotionError.OutOfRange);
        ex.Message.Should().Contain("0..10000").And.Contain("published by the PLC");
        await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(-0.001)), MotionError.OutOfRange);

        rig.Plc.ActualPosition = 9_990_000;
        await rig.TickAsync();
        await Refused(rig, () => rig.Linear.MoveRelativeAsync(new Mm(20)), MotionError.OutOfRange);
    }

    [Fact(DisplayName = "GA-U-30 Speeds outside the machine range are refused")]
    public async Task Move_SpeedOutsideRange_UnreachableSpeed()
    {
        await using var rig = await new DriverRig().ConnectAsync();

        var ex = await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000), new MmPerS(500.001)),
            MotionError.UnreachableSpeed);
        ex.Message.Should().Contain("500").And.Contain("published by the PLC");
        await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000), new MmPerS(0.0004)), MotionError.UnreachableSpeed);
        await Refused(rig, () => rig.Linear.MoveVelocityAsync(new MmPerS(-600)), MotionError.UnreachableSpeed);
        // Percentage(101) cannot be constructed (ModelingEvolution.Drawing enforces 0..100); 0 % is the reachable
        // out-of-range percentage — it resolves below MinSpeed.
        await Refused(rig, () => rig.Linear.MoveAbsoluteAsync(new Mm(2000), new Percentage(0f)), MotionError.UnreachableSpeed);
        var ctor = () => new Percentage(101f);
        ctor.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "GA-U-31 The default speed is a percentage of the maximum")]
    public async Task Move_NoSpeed_DefaultPercentOfMaxWritten()
    {
        await using var rig = await new DriverRig(o => o with { DefaultSpeed = new Percentage(20f) }).ConnectAsync();
        rig.BehaveLikePlc();

        var move = rig.Linear.MoveAbsoluteAsync(new Mm(2000));

        rig.Plc.GetInt(rig.Plc.Map.Velocity).Should().Be(100_000);
        rig.Plc.GetInt(rig.Plc.Map.TargetPosition).Should().Be(2_000_000);
        rig.Plc.Set(p => { p.State = 1; p.Flags |= StatusFlags.InPosition; });
        await rig.TickAsync(2);
        await move.WaitAsync(DriverRig.RealTimeout);
    }

    [Fact(DisplayName = "GA-U-32 A rotary axis accepts only Shortest")]
    public async Task RotaryMove_NonShortestSense_UnsupportedSense()
    {
        await using var rig = await new DriverRig(kind: AxisKind.Rotary).ConnectAsync();

        await Refused(rig, () => rig.Rotary.MoveAbsoluteAsync(Degree<double>.Create(90), null, RotationSense.Positive),
            MotionError.UnsupportedSense);
        await Refused(rig, () => rig.Rotary.MoveAbsoluteAsync(Degree<double>.Create(90), new Percentage(10f),
            RotationSense.Negative), MotionError.UnsupportedSense);
    }
}
