using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;
using MmPerS = ModelingEvolution.Drawing.Units.Speed<double, ModelingEvolution.Drawing.Units.MillimetrePerSecond<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Unit — review #7: a PLC that answers outside the protocol mid-run (MapVersion, limit
/// publication) latches <c>ErrorStop / ProtocolMismatch</c> (GA-U-70 … GA-U-72).
/// </summary>
public class ProtocolMismatchTests
{
    private static IReadOnlyList<string> Errors(DriverRig rig) =>
        rig.LogsAt(LogLevel.Error).Select(r => r.Message).ToArray();

    [Fact(DisplayName = "GA-U-70 A mid-run MapVersion change latches ProtocolMismatch and fails the pending verb")]
    public async Task MapVersionFlipsMidMove_OverlayLatchedVerbFailsResetNeedsSaneBlock()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var move = rig.Linear.MoveAbsoluteAsync(new Mm(2500), new MmPerS(100));
        await rig.TickAsync();
        rig.Axis.State.Should().Be(AxisState.DiscreteMotion, "anchor: the move was accepted");
        rig.Logs.Clear();

        rig.Plc.MapVersion = 2;
        await rig.TickAsync();

        var ex = (await move.Awaiting(m => m.WaitAsync(DriverRig.RealTimeout)).Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(MotionError.ProtocolMismatch);
        MotionErrorClasses.Of(ex.Error).Should().Be(ErrorClass.Protocol);
        ex.Message.Should().StartWith("carriage: ProtocolMismatch: the PLC changed its map version mid-run")
            .And.EndWith("Read MapVersion (S+14 = input 14) = 2, expected 1.");
        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);

        await rig.TickAsync(3);
        Errors(rig).Where(m => m.Contains("ProtocolMismatch")).Should().ContainSingle(
            "the overlay is logged once at Error, not once per tick").Which.Should()
            .Contain("Read MapVersion (S+14 = input 14) = 2, expected 1");

        // G1: nothing new is commanded while the overlay stands.
        var from = rig.Plc.OpCount;
        await rig.Linear.Awaiting(a => a.MoveAbsoluteAsync(new Mm(3000), new MmPerS(100)))
            .Should().ThrowAsync<MotionException>().Where(e => e.Error == MotionError.ProtocolMismatch);
        rig.Plc.CommandWritesSince(from).Should().BeEmpty();

        // Reset while the block is still wrong: refused, naming what was read.
        var refused = (await rig.Axis.Awaiting(a => a.ResetAsync().WaitAsync(DriverRig.RealTimeout))
            .Should().ThrowAsync<MotionException>()).Which;
        refused.Error.Should().Be(MotionError.ProtocolMismatch);
        refused.Message.Should().Contain("ResetAsync refused").And.Contain("MapVersion (S+14 = input 14) = 2");
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch);

        // A fresh tick with a sane block lets Reset clear it.
        rig.Plc.MapVersion = 1;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch, "only ResetAsync clears the overlay");
        await rig.Axis.ResetAsync().WaitAsync(DriverRig.RealTimeout);
        rig.Axis.Status.Error.Should().BeNull();
    }

    [Fact(DisplayName = "GA-U-71 A mid-run partial limit publication latches ProtocolMismatch naming the three registers")]
    public async Task LimitsBecomePartial_OverlayNamesRegistersAndExpected()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Logs.Clear();

        rig.Plc.SetLimits(0, 0, 500_000); // published (not all zero) but TravelMin < TravelMax fails
        await rig.TickAsync();

        rig.Axis.State.Should().Be(AxisState.ErrorStop);
        rig.Axis.Status.Error.Should().Be(MotionError.ProtocolMismatch,
            "a garbage publication is the PLC breaking the protocol, not a missing limit source");
        rig.Device.Limits.Source.Should().Be(LimitSource.None, "no garbage limit is ever used");
        Errors(rig).Should().ContainSingle(m => m.Contains("ProtocolMismatch")).Which.Should()
            .Contain("carriage: ProtocolMismatch: the PLC's limit publication became partial or not sane mid-run")
            .And.Contain("Read TravelMin (S+8 = input 8) = 0, TravelMax (S+10 = input 10) = 0, MaxVelocity (S+12 = input 12) = 500000, "
                         + "expected all three 0, or TravelMin < TravelMax and MaxVelocity > 0.");

        rig.LogsAt(LogLevel.Warning).Should().NotContain(r => r.Message.Contains("invalid limit set"),
            "the old quiet 'no limit source' Warning is not a second report of the same fact");

        var ex = (await rig.Axis.Awaiting(a => a.HomeAsync().WaitAsync(DriverRig.RealTimeout))
            .Should().ThrowAsync<MotionException>()).Which;
        ex.Error.Should().Be(MotionError.ProtocolMismatch);
    }

    [Fact(DisplayName = "GA-U-72 control: limits withdrawn to all zero are no source, not a protocol error")]
    public async Task LimitsWithdrawnToZero_NoSourceNoOverlay()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Logs.Clear();

        rig.Plc.SetLimits(0, 0, 0);
        await rig.TickAsync();

        rig.Axis.Status.Error.Should().BeNull();
        rig.Axis.State.Should().Be(AxisState.Standstill);
        rig.Device.Limits.Source.Should().Be(LimitSource.None);
        Errors(rig).Should().BeEmpty();
        rig.LogsAt(LogLevel.Warning).Should().ContainSingle(r => r.Message.Contains("no limit source"));
    }
}
