using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.Drawing.Units;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — State and fault mapping (GA-U-16 … GA-U-19) and Limits (GA-U-20 … GA-U-23).</summary>
public class StateAndLimitTests
{
    [Theory(DisplayName = "GA-U-16 PLC states map one to one")]
    [InlineData((ushort)0, AxisState.Disabled)]
    [InlineData((ushort)1, AxisState.Standstill)]
    [InlineData((ushort)2, AxisState.Homing)]
    [InlineData((ushort)3, AxisState.DiscreteMotion)]
    [InlineData((ushort)4, AxisState.ContinuousMotion)]
    [InlineData((ushort)6, AxisState.Stopping)]
    [InlineData((ushort)7, AxisState.ErrorStop)]
    public async Task Tick_KnownState_MapsToSameSdkState(ushort raw, AxisState expected)
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.State = raw;
        await rig.TickAsync();
        rig.Axis.State.Should().Be(expected);
    }

    [Theory(DisplayName = "GA-U-16 reserved and unknown states read as ErrorStop / ProtocolMismatch, logged with the register")]
    [InlineData((ushort)5)]
    [InlineData((ushort)42)]
    public async Task Tick_UnknownState_ErrorStopDriveFaultLogged(ushort raw)
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.State = raw;
        await rig.TickAsync();

        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);
        rig.LogsAt(LogLevel.Error).Should().Contain(r => r.Message.StartsWith("carriage: ProtocolMismatch: ")
            && r.Message.Contains($"Read State (S+0 = input 100) = {raw}, expected 0–4, 6, 7."));
    }

    [Theory(DisplayName = "GA-U-17 Fault codes map by remedy")]
    [InlineData((ushort)1, MotionError.DriveFault)]
    [InlineData((ushort)2, MotionError.LimitTripped)]
    [InlineData((ushort)3, MotionError.MotionFailed)]
    [InlineData((ushort)4, MotionError.WatchdogTripped)]
    [InlineData((ushort)5, MotionError.HomeLatchFailed)]
    [InlineData((ushort)6, MotionError.DriveFault)]
    [InlineData((ushort)7, MotionError.SafetyStop)]
    [InlineData((ushort)42, MotionError.DriveFault)]
    [InlineData((ushort)150, MotionError.DriveFault)]
    public async Task Tick_ErrorStopWithFaultCode_MapsByRemedy(ushort code, MotionError expected)
    {
        var snapshot = new PlcSnapshot(new StatusBlock(7, StatusFlags.None, 0, 0, code, 0, 0, 0, 0, 1), 1, 1, 3, 0);
        var ex = AxisEngine.FaultException("carriage", RegisterMap.Default, snapshot);
        ex.Error.Should().Be(expected);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Machine);
        ex.Message.Should().StartWith($"carriage: {expected}: ").And.Contain($"Read FaultCode (S+6 = input 106) = {code}");

        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.State = 7;
        rig.Plc.FaultCode = code;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(expected);
    }

    [Fact(DisplayName = "GA-U-17 ErrorStop without a fault code is the PLC breaking the protocol")]
    public async Task Tick_ErrorStopWithFaultCodeZero_ProtocolMismatch()
    {
        var ex = AxisEngine.FaultException("carriage", RegisterMap.Default,
            new PlcSnapshot(new StatusBlock(7, StatusFlags.None, 0, 0, 0, 0, 0, 0, 0, 1), 1, 0, 0, 0));
        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Protocol);
        ex.Message.Should().Contain("Read State (S+0 = input 100) = 7, FaultCode (S+6 = input 106) = 0, expected 1–7 or ≥ 100.");

        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.State = 7;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);
    }

    [Fact(DisplayName = "GA-U-17 the watchdog message quotes all three registers")]
    public void FaultException_Watchdog_QuotesWatchdogRegisters()
    {
        var ex = AxisEngine.FaultException("carriage", RegisterMap.Default,
            new PlcSnapshot(new StatusBlock(7, StatusFlags.Homed, 0, 0, 4, 0, 0, 0, 0, 1), 1, 1, 3, 0));
        ex.Message.Should().EndWith(
            "Read FaultCode (S+6 = input 106) = 4, WatchdogFault (C+10 = holding 10) = 1, WatchdogTrips (C+11 = holding 11) = 3.");
    }

    [Fact(DisplayName = "GA-U-17 no error outside ErrorStop")]
    public async Task Tick_StandstillWithFaultCode_NoError()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.FaultCode = 1;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().BeNull();
    }

    [Fact(DisplayName = "GA-U-18 An unhomed position is null")]
    public async Task Status_Unhomed_PositionNull()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.ActualPosition = 1_234_567;
        rig.Plc.Flags = StatusFlags.DriveReady;
        await rig.TickAsync();
        rig.Axis.Status.Position.Should().BeNull();
        rig.Linear.Offset.Should().BeNull();

        rig.Plc.Flags = StatusFlags.Homed;
        await rig.TickAsync();
        rig.Axis.Status.Position.Should().Be(1234.567);
        rig.Linear.Offset!.Value.Value.Should().Be(1234.567);

        await using var rotary = await new DriverRig(kind: AxisKind.Rotary).ConnectAsync();
        rotary.Plc.Flags = StatusFlags.None;
        await rotary.TickAsync();
        rotary.Rotary.Angle.Should().BeNull();
        rotary.Plc.Flags = StatusFlags.Homed;
        await rotary.TickAsync();
        ((double)rotary.Rotary.Angle!.Value).Should().Be(1000.0);
    }

    [Fact(DisplayName = "GA-U-19 Limit switches come from flags")]
    public async Task Status_LimitFlags_MapToLimitSwitchState()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Plc.Flags = StatusFlags.Homed | StatusFlags.LimitMin | StatusFlags.LimitMax;
        await rig.TickAsync();
        rig.Axis.Status.Limits.Should().Be(LimitSwitchState.Min | LimitSwitchState.Max);

        rig.Plc.Flags = StatusFlags.Homed | StatusFlags.LimitMax;
        await rig.TickAsync();
        rig.Axis.Status.Limits.Should().Be(LimitSwitchState.Max);
    }

    [Fact(DisplayName = "GA-U-20 Published limits win and a mismatch warns")]
    public async Task Limits_PublishedDifferFromConfigured_PlcWinsOneWarning()
    {
        await using var rig = await new DriverRig(o => o with { ConfiguredTravelMax = 9000 }).ConnectAsync();
        await rig.TickAsync(3);

        rig.Device.Limits.Source.Should().Be(LimitSource.Plc);
        rig.Linear.Max.Value.Should().Be(10_000);
        rig.Linear.MaxSpeed.Value.Should().Be(500);
        var warnings = rig.LogsAt(LogLevel.Warning).Where(r => r.Message.Contains("differ")).ToArray();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("10000").And.Contain("9000");
    }

    [Fact(DisplayName = "GA-U-21 Unpublished limits fall back to configuration")]
    public async Task Limits_Unpublished_ConfigurationUsedOneInformation()
    {
        await using var rig = new DriverRig(o => o with
        {
            ConfiguredTravelMin = 0, ConfiguredTravelMax = 8000, ConfiguredMaxVelocity = 300,
        });
        rig.Plc.SetLimits(0, 0, 0);
        await rig.ConnectAsync();
        await rig.TickAsync(3);

        rig.Device.Limits.Should().Be(new AxisLimits(0, 8000, 300, LimitSource.Configuration));
        rig.LogsAt(LogLevel.Information).Where(r => r.Message.Contains("publishes no limits"))
            .Should().ContainSingle().Which.Message.Should().Contain("8000").And.Contain("300");
    }

    [Fact(DisplayName = "GA-U-22 No limit source refuses moves only")]
    public async Task Limits_NoSource_MovesRefusedHomeWritten()
    {
        await using var rig = new DriverRig();
        rig.Plc.SetLimits(0, 0, 0);
        await rig.ConnectAsync();
        rig.BehaveLikePlc();
        rig.Device.Limits.Source.Should().Be(LimitSource.None);
        var before = rig.Plc.CommandWritesSince(0).Count;

        var abs = () => rig.Linear.MoveAbsoluteAsync(new(100));
        (await DriverRig.Bounded(abs).Should().ThrowAsync<MotionException>()).Which.Should()
            .Match<MotionException>(e => e.Error == MotionError.OutOfRange && e.Message.Contains("no limit source")
                                                                             && e.Message.Contains("TravelMin"));
        var vel = () => rig.Linear.MoveVelocityAsync(new(10));
        (await DriverRig.Bounded(vel).Should().ThrowAsync<MotionException>()).Which.Error.Should().Be(MotionError.OutOfRange);
        rig.Plc.CommandWritesSince(0).Count.Should().Be(before, "a refusal writes nothing");

        var home = rig.Axis.HomeAsync();
        rig.Plc.Command.Should().Be((ushort)(CommandBits.Enable | CommandBits.Home), "Home is written without limits");
        rig.Plc.Set(p => { p.State = 1; p.Flags = StatusFlags.Homed; });
        await rig.TickAsync(2);
        await home.WaitAsync(DriverRig.RealTimeout);
    }

    [Theory(DisplayName = "GA-U-23 A partial or inverted publication refuses attach")]
    [InlineData(0, 10_000_000, 0)]
    [InlineData(10_000_000, 10_000_000, 500_000)]
    [InlineData(10_000_000, 0, 500_000)]
    [InlineData(0, 0, 500_000)]
    public async Task Connect_InvalidPublication_CommunicationLostNothingWritten(int min, int max, int maxVelocity)
    {
        await using var rig = new DriverRig();
        rig.Plc.SetLimits(min, max, maxVelocity);

        var connect = () => rig.Device.ConnectAsync();

        var ex = (await DriverRig.Bounded(connect).Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        ex.Message.Should().StartWith("carriage: ProtocolMismatch: attach refused")
            .And.Contain($"TravelMin (S+8 = input 108) = {min}").And.Contain($"TravelMax (S+10 = input 110) = {max}")
            .And.Contain($"MaxVelocity (S+12 = input 112) = {maxVelocity}");
        rig.LogsAt(LogLevel.Error).Should().Contain(r => r.Message == ex.Message);
        rig.Plc.Ops.Should().NotBeEmpty("anchor: the status block was read");
        rig.Plc.Writes.Should().BeEmpty();
        rig.Device.IsConnected.Should().BeFalse();
    }

    [Theory(DisplayName = "GA-U-23 a reading range that contradicts the travel refuses attach as OutOfRange")]
    [InlineData(5.0, null)]
    [InlineData(null, 9000.0)]
    public async Task Connect_ReadRangeInsideTravel_OutOfRangeNothingWritten(double? readMin, double? readMax)
    {
        await using var rig = new DriverRig(o => o with { ReadMin = readMin, ReadMax = readMax });

        var ex = (await DriverRig.Bounded(() => rig.Device.ConnectAsync()).Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.OutOfRange);
        ex.Message.Should().StartWith("carriage: OutOfRange: attach refused: configured Read")
            .And.Contain(readMin is null ? "Read TravelMax (S+10 = input 110) = 10000000." : "Read TravelMin (S+8 = input 108) = 0.");
        rig.LogsAt(LogLevel.Error).Should().Contain(r => r.Message == ex.Message);
        rig.Plc.Writes.Should().BeEmpty();
    }
}
