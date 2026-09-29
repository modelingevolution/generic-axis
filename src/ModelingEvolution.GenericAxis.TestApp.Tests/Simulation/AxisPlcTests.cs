using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>Simulator scenarios GA-U-47…55 (test-scenarios.md § Simulator) plus the protocol's PLC-side rules.</summary>
public sealed class AxisPlcTests
{
    private const ushort Standstill = (ushort)SimAxisState.Standstill;
    private const ushort ErrorStop = (ushort)SimAxisState.ErrorStop;
    private const ushort Disabled = (ushort)SimAxisState.Disabled;

    [Fact]
    public void GA_U_47_AckLandsInTheScanThatEntersTheState()
    {
        using var bench = new PlcBench();
        bench.Energise();
        bench.Flags.Should().HaveFlag(SimStatusFlags.Homed);

        bench.Parameters(target: 1000, velocity: 100);
        var seq = bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);
        bench.Ack.Should().NotBe(seq, "nothing is accepted before a scan");

        bench.Tick();

        bench.Ack.Should().Be(seq);
        bench.State.Should().Be((ushort)SimAxisState.DiscreteMotion, "the ack and the state it caused are published by the same scan");
    }

    [Fact]
    public void GA_U_47_EnableFromDisabledAcksTogetherWithStandstill()
    {
        using var bench = new PlcBench();
        var seq = bench.Command(SimCommandBits.Enable);

        while (bench.Ack != seq)
        {
            bench.State.Should().Be(Disabled, "until the drive is ready neither the ack nor Standstill is published");
            bench.Tick();
        }

        bench.State.Should().Be(Standstill);
    }

    [Fact]
    public void GA_U_48_AMoveRampsAndArrives()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 0 });
        bench.Energise();
        bench.Parameters(target: 100, velocity: 100, acceleration: 1000);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);

        var previous = 0.0;
        var maxStep = 0.0;
        var maxSpeed = 0.0;
        bench.TickUntil(() =>
        {
            var v = bench.Snap.Velocity;
            maxStep = Math.Max(maxStep, Math.Abs(v - previous));
            maxSpeed = Math.Max(maxSpeed, Math.Abs(v));
            previous = v;
            return bench.State == Standstill && bench.Snap.CommandAck == bench.Seq;
        }, TimeSpan.FromSeconds(5));

        maxSpeed.Should().BeGreaterThan(99).And.BeLessThanOrEqualTo(100);
        maxStep.Should().BeLessThanOrEqualTo(1000 * 0.010 + 1e-9, "|Δv| per 10 ms scan is at most a·dt");
        bench.Flags.Should().HaveFlag(SimStatusFlags.InPosition);
        bench.ActualPositionRaw.Should().Be(100_000);
        bench.ActualVelocityRaw.Should().Be(0);
    }

    [Fact]
    public void GA_U_49_NoLeaseNoArming()
    {
        using var bench = new PlcBench();
        bench.Lease(0);
        bench.BeatFor(TimeSpan.FromSeconds(1));

        bench.TickFor(TimeSpan.FromSeconds(2));

        bench.Snap.WatchdogArmed.Should().BeFalse();
        bench.WatchdogTrips.Should().Be(0);
        bench.WatchdogFault.Should().Be(0);
        bench.FaultCode.Should().Be(0);
    }

    [Fact]
    public void GA_U_49_ALeasedStalledBeatTripsAtOneSecond()
    {
        using var bench = new PlcBench();
        bench.Lease(1);
        bench.Energise();
        bench.Parameters(0, velocity: 50);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.BeatFor(TimeSpan.FromMilliseconds(500));
        bench.Snap.WatchdogArmed.Should().BeTrue();
        bench.Snap.Velocity.Should().BeGreaterThan(0);

        bench.Beat();
        bench.Tick();                       // the scan that sees the last change
        bench.Tick(99);                     // 0.99 s without a change
        bench.WatchdogTrips.Should().Be(0, "0.99 s is inside the window");

        bench.Tick();                       // 1.00 s

        bench.WatchdogTrips.Should().Be(1);
        bench.WatchdogFault.Should().Be(1);
        bench.FaultCode.Should().Be((ushort)SimFaultCode.Watchdog);
        bench.State.Should().Be(ErrorStop);
        bench.ActualVelocityRaw.Should().Be(0);
        bench.Flags.Should().HaveFlag(SimStatusFlags.Homed, "a trip never touches Homed");
        bench.Snap.WatchdogArmed.Should().BeFalse("after a trip the network disarms");
    }

    [Fact]
    public void GA_U_49_ALatchedTripDoesNotRearmUntilClearedAndBeaten()
    {
        using var bench = new PlcBench();
        bench.Lease(1);
        bench.BeatFor(TimeSpan.FromMilliseconds(300));
        bench.TickFor(TimeSpan.FromSeconds(1.2));
        bench.WatchdogTrips.Should().Be(1);

        bench.BeatFor(TimeSpan.FromSeconds(1));
        bench.TickFor(TimeSpan.FromSeconds(2));
        bench.WatchdogTrips.Should().Be(1, "latched: beats do not re-arm while WatchdogFault = 1");

        bench.ClearWatchdogFault();
        bench.Tick();
        bench.Snap.WatchdogArmed.Should().BeFalse("clearing alone does not arm; a new beat does");
        bench.BeatFor(TimeSpan.FromMilliseconds(200));
        bench.Snap.WatchdogArmed.Should().BeTrue();
        bench.TickFor(TimeSpan.FromSeconds(1.1));
        bench.WatchdogTrips.Should().Be(2);
    }

    [Fact]
    public void GA_U_50_ACleanReleaseDisarms()
    {
        using var bench = new PlcBench();
        bench.Lease(1);
        bench.BeatFor(TimeSpan.FromMilliseconds(500));
        bench.Snap.WatchdogArmed.Should().BeTrue();

        bench.Lease(0);
        bench.TickFor(TimeSpan.FromSeconds(5));

        bench.WatchdogTrips.Should().Be(0);
        bench.WatchdogFault.Should().Be(0);
        bench.Snap.WatchdogArmed.Should().BeFalse();
    }

    [Fact]
    public void GA_U_51_AfterATripTheAxisNeedsResetAndAFreshEnableEdge()
    {
        using var bench = new PlcBench();
        bench.Lease(1);
        bench.Energise();
        bench.BeatFor(TimeSpan.FromMilliseconds(300));
        bench.TickFor(TimeSpan.FromSeconds(1.2));
        bench.State.Should().Be(ErrorStop);

        // Enable is still 1 in the command word throughout.
        var reset = bench.Command(SimCommandBits.Enable | SimCommandBits.Reset);
        bench.Tick();
        bench.Ack.Should().Be(reset);
        bench.State.Should().Be(Disabled);
        bench.FaultCode.Should().Be(0);
        bench.ClearEdges(SimCommandBits.Enable);

        bench.Command(SimCommandBits.Enable);
        bench.TickFor(TimeSpan.FromMilliseconds(200));
        bench.State.Should().Be(Disabled, "a level 1 that was never 0 after the fault does not re-energise");

        bench.Command(SimCommandBits.None);
        bench.Tick();
        bench.Energise();
        bench.State.Should().Be(Standstill);
    }

    [Fact]
    public void GA_U_52_HomingReReferencesAtTheSensor()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { HomedAtPowerUp = false, InitialPosition = 300 });
        bench.Flags.Should().NotHaveFlag(SimStatusFlags.Homed);
        bench.ActualPositionRaw.Should().Be((int)Math.Round((300 + 1234.567) * 1000), "unhomed, the reading carries the offset");
        bench.Energise();

        var home = bench.Command(SimCommandBits.Enable | SimCommandBits.Home);
        bench.Tick();
        bench.Ack.Should().Be(home);
        bench.State.Should().Be((ushort)SimAxisState.Homing);
        bench.Tick(10);
        bench.Snap.Velocity.Should().BeNegative("homing seeks in the negative direction");

        bench.TickUntil(() => bench.State != (ushort)SimAxisState.Homing, TimeSpan.FromSeconds(10));

        bench.State.Should().Be(Standstill);
        bench.Flags.Should().HaveFlag(SimStatusFlags.Homed);
        bench.ActualPositionRaw.Should().Be(0, "ActualPosition = HomeSensorPosition on the sensor edge");
        bench.FaultCode.Should().Be(0);
    }

    [Fact]
    public void GA_U_52_HomingFromOnTheSensorBacksOffFirst()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 1 });
        bench.Flags.Should().HaveFlag(SimStatusFlags.HomeSensor);
        bench.Energise();
        bench.Command(SimCommandBits.Enable | SimCommandBits.Home);
        bench.Tick();
        bench.State.Should().Be((ushort)SimAxisState.Homing);

        bench.TickUntil(() => bench.State != (ushort)SimAxisState.Homing, TimeSpan.FromSeconds(10));

        bench.State.Should().Be(Standstill);
        bench.ActualPositionRaw.Should().Be(0);
    }

    [Fact]
    public void GA_U_53_HomingFailsAtTheSwitch()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions
        {
            HomedAtPowerUp = false, InitialPosition = 30, Faults = new SimFaults { HomeSensorDead = true },
        });
        bench.Energise();
        bench.Command(SimCommandBits.Enable | SimCommandBits.Home);
        bench.Tick();
        bench.State.Should().Be((ushort)SimAxisState.Homing);

        bench.TickUntil(() => bench.State != (ushort)SimAxisState.Homing, TimeSpan.FromSeconds(10));

        bench.State.Should().Be(ErrorStop);
        bench.FaultCode.Should().Be((ushort)SimFaultCode.HomingFailed);
        bench.Flags.Should().HaveFlag(SimStatusFlags.LimitMin);
    }

    [Fact]
    public void GA_U_54_TheTravelLimitStopsAJog()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 9900 });
        bench.Energise();
        bench.Parameters(0, velocity: 100);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.Tick();
        bench.State.Should().Be((ushort)SimAxisState.ContinuousMotion);

        bench.TickUntil(() => bench.State == Standstill, TimeSpan.FromSeconds(5));

        bench.FaultCode.Should().Be(0, "reaching TravelMax homed is a controlled stop, not a fault");
        bench.ActualPositionRaw.Should().BeInRange(10_000_000 - 5, 10_000_000);
        bench.ActualVelocityRaw.Should().Be(0);
    }

    [Fact]
    public void GA_U_54_ALimitSwitchFaultsAndAllowsOnlyMotionAway()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 5000 });
        bench.Energise();
        bench.Parameters(0, velocity: 100);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.Tick(20);

        bench.Plc.Faults = new SimFaults { ForceLimitMax = true };
        bench.Tick();

        bench.State.Should().Be(ErrorStop);
        bench.FaultCode.Should().Be((ushort)SimFaultCode.LimitSwitch);
        bench.Flags.Should().HaveFlag(SimStatusFlags.LimitMax);
        bench.ActualVelocityRaw.Should().Be(0);

        bench.Command(SimCommandBits.Reset);
        bench.Tick();
        bench.State.Should().Be(Disabled);
        bench.Energise();

        bench.Parameters(0, velocity: 100);
        var toward = bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.Tick();
        bench.Ack.Should().Be(toward, "a refused command is still acknowledged");
        bench.State.Should().Be(Standstill, "motion toward the active switch is refused");

        bench.Parameters(0, velocity: -100);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.Tick();
        bench.State.Should().Be((ushort)SimAxisState.ContinuousMotion, "motion away from the switch is accepted");
    }

    [Fact]
    public void GA_U_55_UnpublishedLimitsReadAsZeros()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { PublishLimits = false });
        bench.Tick();

        bench.Registers.ReadBlock(bench.Options.StatusBase + SimRegisters.TravelMin, 6).Should().OnlyContain(w => w == 0);
        bench.Registers.Read(bench.Options.StatusBase + SimRegisters.MapVersion).Should().Be(1);
    }

    [Fact]
    public void PublishedLimitsAreLowWordFirstAndSwappedOrderIsInjectable()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { TravelMin = -2500, TravelMax = 10_000, MaxVelocity = 500 });
        bench.Tick();
        var s = bench.Options.StatusBase;
        // −2 500 000 = 0xFFD9DA60 → [0xDA60, 0xFFD9] (GA-U-01's vector, read from the PLC side).
        bench.Registers.ReadBlock(s + SimRegisters.TravelMin, 2).Should().Equal(0xDA60, 0xFFD9);
        bench.Registers.ReadBlock(s + SimRegisters.MaxVelocity, 2).Should().Equal(0xA120, 0x0007);

        bench.Plc.Faults = new SimFaults { SwappedWordOrder = true };
        bench.Tick();
        bench.Registers.ReadBlock(s + SimRegisters.TravelMin, 2).Should().Equal(0xFFD9, 0xDA60);
    }

    [Fact]
    public void StopFromStandstillIsAcknowledgedAndChangesNothing()
    {
        using var bench = new PlcBench();
        bench.Energise();
        var seq = bench.Command(SimCommandBits.Enable | SimCommandBits.Stop);
        bench.Tick();
        bench.Ack.Should().Be(seq);
        bench.State.Should().Be(Standstill);
    }

    [Fact]
    public void StopMidMoveHaltsWithinTheQuickStopRamp()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 0 });
        bench.Energise();
        bench.Parameters(target: 9000, velocity: 500);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);
        bench.TickFor(TimeSpan.FromSeconds(1));
        bench.Snap.Velocity.Should().Be(500);

        bench.Command(SimCommandBits.Enable | SimCommandBits.Stop);
        bench.Tick();
        bench.State.Should().Be((ushort)SimAxisState.Stopping);
        var halted = bench.TickUntil(() => bench.State == Standstill, TimeSpan.FromSeconds(1));

        halted.Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(200));
        bench.ActualVelocityRaw.Should().Be(0);
    }

    [Fact]
    public void MoveAbsoluteNeedsHomed()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { HomedAtPowerUp = false });
        bench.Energise();
        bench.Parameters(target: 100, velocity: 100);
        var seq = bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);
        bench.Tick();
        bench.Ack.Should().Be(seq);
        bench.State.Should().Be(Standstill);
    }

    [Fact]
    public void AMoveWithEnableLowIsIgnored()
    {
        using var bench = new PlcBench();
        bench.Parameters(target: 100, velocity: 100);
        var seq = bench.Command(SimCommandBits.MoveAbsolute);
        bench.Tick();
        bench.Ack.Should().Be(seq);
        bench.State.Should().Be(Disabled);
        bench.FaultCode.Should().Be(0);
    }

    [Fact]
    public void SuppressAckNeverAcknowledges()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { Faults = new SimFaults { SuppressAck = true } });
        bench.Command(SimCommandBits.Enable);
        bench.TickFor(TimeSpan.FromSeconds(1));
        bench.Ack.Should().Be(0);
        bench.State.Should().Be(Disabled);
    }

    [Theory]
    [InlineData(nameof(SimFaults.DriveFault), SimFaultCode.DriveFault)]
    [InlineData(nameof(SimFaults.SafetyStop), SimFaultCode.SafetyStop)]
    [InlineData(nameof(SimFaults.DriveLinkLost), SimFaultCode.DriveLinkLost)]
    public void InjectedFaultsStopAMoveWithTheirCode(string fault, SimFaultCode code)
    {
        using var bench = new PlcBench(new SimulatedAxisOptions { InitialPosition = 0 });
        bench.Energise();
        bench.Parameters(target: 9000, velocity: 200);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);
        bench.Tick(50);

        bench.Plc.Faults = fault switch
        {
            nameof(SimFaults.DriveFault) => new SimFaults { DriveFault = true },
            nameof(SimFaults.SafetyStop) => new SimFaults { SafetyStop = true },
            _ => new SimFaults { DriveLinkLost = true },
        };
        bench.Tick();

        bench.State.Should().Be(ErrorStop);
        bench.FaultCode.Should().Be((ushort)code);
        bench.ActualVelocityRaw.Should().Be(0);
    }

    [Fact]
    public void FollowingErrorFiresAtHalfTheMove()
    {
        using var bench = new PlcBench(new SimulatedAxisOptions
        {
            InitialPosition = 0, Faults = new SimFaults { FollowingErrorAtHalfway = true },
        });
        bench.Energise();
        bench.Parameters(target: 1000, velocity: 500);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);

        bench.TickUntil(() => bench.State == ErrorStop, TimeSpan.FromSeconds(5));

        bench.FaultCode.Should().Be((ushort)SimFaultCode.FollowingError);
        bench.Snap.PublishedPosition.Should().BeInRange(500, 510);
    }

    [Fact]
    public void PowerCycleKeepsHomedOnlyWithAnAbsoluteEncoder()
    {
        using var bench = new PlcBench();
        bench.Lease(1);
        bench.BeatFor(TimeSpan.FromMilliseconds(300));
        bench.TickFor(TimeSpan.FromSeconds(1.2));
        bench.WatchdogTrips.Should().Be(1);

        bench.Plc.PowerCycle();

        bench.WatchdogTrips.Should().Be(0);
        bench.State.Should().Be(Disabled);
        bench.Ack.Should().Be(0);
        bench.Flags.Should().HaveFlag(SimStatusFlags.Homed);
        bench.Snap.WatchdogArmed.Should().BeFalse();
    }

    // ---- GA-U-90 (review #21): refuse, never clamp ---------------------------------------------------------------

    [Theory]
    [InlineData(SimCommandBits.MoveAbsolute, 600.0)]
    [InlineData(SimCommandBits.MoveVelocity, 600.0)]
    [InlineData(SimCommandBits.MoveVelocity, -600.0)]
    public void GA_U_90_VelocityAboveMaxVelocity_IsAcknowledgedAndIgnoredWithOneWarning(SimCommandBits move, double velocity)
    {
        using var bench = new PlcBench();
        bench.Energise();
        var before = bench.Snap;
        bench.Log.Clear();

        bench.Parameters(target: 2000, velocity: velocity); // MaxVelocity is 500 mm/s
        var seq = bench.Command(SimCommandBits.Enable | move);
        bench.Tick(20);

        bench.Ack.Should().Be(seq, "a refused command is still acknowledged");
        bench.State.Should().Be(Standstill, "the refused move never started");
        bench.ActualVelocityRaw.Should().Be(0);
        bench.ActualPositionRaw.Should().Be((int)Math.Round(before.PublishedPosition * 1000), "no motion at a clamped speed");
        var warning = bench.Log.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Should().Contain("Velocity (C+4)").And.Contain($"{velocity}").And.Contain("MaxVelocity 500");
    }

    [Theory]
    [InlineData(10000.5)]
    [InlineData(-0.5)]
    public void GA_U_90_TargetOutsideTravel_IsAcknowledgedAndIgnoredWithOneWarning(double target)
    {
        using var bench = new PlcBench();
        bench.Energise();
        bench.Log.Clear();

        bench.Parameters(target: target, velocity: 100);
        var seq = bench.Command(SimCommandBits.Enable | SimCommandBits.MoveAbsolute);
        bench.Tick(20);

        bench.Ack.Should().Be(seq);
        bench.State.Should().Be(Standstill);
        var warning = bench.Log.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Should().Contain("TargetPosition (C+2)").And.Contain($"{target}").And.Contain("TravelMin..TravelMax 0..10000");
    }

    [Fact]
    public void GA_U_90_VelocityAtMaxVelocity_IsAccepted()
    {
        using var bench = new PlcBench();
        bench.Energise();

        bench.Parameters(target: 0, velocity: 500);
        bench.Command(SimCommandBits.Enable | SimCommandBits.MoveVelocity);
        bench.Tick(200);

        bench.State.Should().Be((ushort)SimAxisState.ContinuousMotion);
        bench.ActualVelocityRaw.Should().Be(500_000, "MaxVelocity itself is inside the limit");
    }

    /// <summary>Review #37: the boundaries themselves are inside the limits (MoveAbsolute at MaxVelocity is the driver's default move).</summary>
    [Theory]
    [InlineData(SimCommandBits.MoveVelocity, 0.0, 500.0)]
    [InlineData(SimCommandBits.MoveAbsolute, 10000.0, 500.0)]
    [InlineData(SimCommandBits.MoveAbsolute, 0.0, 100.0)]
    [InlineData(SimCommandBits.MoveAbsolute, 10000.0, 100.0)]
    public void GA_U_90_TheLimitsThemselves_AreAccepted(SimCommandBits move, double target, double velocity)
    {
        using var bench = new PlcBench();
        bench.Energise();
        bench.Log.Clear();

        bench.Parameters(target: target, velocity: velocity);
        bench.Command(SimCommandBits.Enable | move);
        bench.Tick();

        bench.State.Should().Be(move == SimCommandBits.MoveVelocity ? (ushort)SimAxisState.ContinuousMotion : (ushort)SimAxisState.DiscreteMotion,
            $"{move} to {target} at {velocity} is inside TravelMin..TravelMax and MaxVelocity");
        bench.Log.At(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public void GA_U_90_OptionsThatCannotBePublished_AreRefusedAtStartup()
    {
        var act = () => new SimulatedAxisOptions { TravelMax = 3_000_000 }.Validate();

        act.Should().Throw<ArgumentException>().WithMessage("*TravelMax*int32*",
            "a position that does not fit its register would otherwise be published clamped");
    }
}
