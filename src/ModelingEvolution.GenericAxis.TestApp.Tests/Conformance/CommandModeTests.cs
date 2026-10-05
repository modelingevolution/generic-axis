using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using ModelingEvolution.GenericAxis.TestApp.Simulation;
using ModelingEvolution.GenericAxis.TestApp.Tests.Support;
using RocketWelder.SDK.Abstractions;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// One-verb mode, <c>--command &lt;verb&gt;</c> (protocol § Conformance checks, One-verb mode; ADR-38), in process against
/// the simulator: GA-I-71…GA-I-80. Every run must leave the axis no more energised than it found it, with the lease
/// released.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class CommandModeTests
{
    private static async Task<(int Exit, string Out)> Command(LiveSimulator sim, VerbRequest request, CancellationToken ct = default,
        TextWriter? output = null)
    {
        var text = new StringWriter();
        var writer = output ?? TextWriter.Synchronized(text);
        var options = new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = true, Command = request };
        var exit = await new CommandRunner(NullLoggerFactory.Instance).RunAsync(options, writer, ct);
        return (exit, text.ToString());
    }

    private static string Last(string output) => output.TrimEnd().Split('\n')[^1];

    /// <summary>
    /// The run's invariant (protocol § One-verb mode step 6, #61): the lease released, no edge bit set, at rest, and the
    /// axis no more energised than it was FOUND — an axis found Disabled ends not energised; one found powered may stay so.
    /// </summary>
    private static async Task ShouldEndReleased(LiveSimulator sim, SimSnapshot found)
    {
        var end = await sim.SettledAsync();
        end.LeaseOwner.Should().Be(0, "the lease is released");
        (end.CommandWord & ~(ushort)SimCommandBits.Enable).Should().Be(0, "no edge bit is left set");
        end.Velocity.Should().Be(0);
        if (!found.Energised) end.Energised.Should().BeFalse($"the axis was found {found.State}, not energised: a run leaves it no more energised than it found it");
    }

    [Fact]
    public async Task GA_I_71_EnableProvesTheHandshakeAndEndsDisabled()
    {
        using var sim = new LiveSimulator();

        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        exit.Should().Be(0, output);
        output.Should().Contain("enable: done — Standstill after").And.Contain("State 1 Standstill");
        Last(output).Should().Be("RESULT: PASS");
        output.Should().Contain("Cleanup: C+0 = 0x0000 (clear edge bits, Enable 0)", "this run set Enable 1, so it writes Enable 0");
        var end = await sim.SettledAsync();
        end.State.Should().Be(SimAxisState.Disabled, "a run leaves the axis no more energised than it found it");
        end.CommandWord.Should().Be(0);
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_72_DisableAndStopAndResetOnAnIdleAxis()
    {
        using var sim = new LiveSimulator();
        foreach (var verb in new[] { Verb.Disable, Verb.Stop, Verb.Reset })
        {
            var found = await sim.SettledAsync();
            var (exit, output) = await Command(sim, new VerbRequest(verb));
            exit.Should().Be(0, output);
            Last(output).Should().Be("RESULT: PASS");
            output.Should().NotContain("Enable 0)", $"{verb} did not set Enable 1, so cleanup writes no Enable 0");
            await ShouldEndReleased(sim, found);
        }
    }

    [Fact]
    public async Task GA_I_73_HomeFromDisabledEnablesFirstAndEndsHomed()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { HomedAtPowerUp = false, InitialPosition = 20 });

        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Home));

        exit.Should().Be(0, output);
        output.Should().Contain("write Command Enable (0x0001)").And.Contain("write Command Enable|Home (0x0003)")
            .And.Contain("home: done — Standstill + Homed after");
        var end = await sim.SettledAsync();
        end.Homed.Should().BeTrue();
        end.PublishedPosition.Should().Be(0, "re-referenced at the home sensor");
        end.State.Should().Be(SimAxisState.Disabled, "the run enabled the drive, so it disables it");
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_74_MoveArrivesAtTheTargetAtThePercentOfMaxVelocity()
    {
        using var sim = new LiveSimulator();

        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Move, Target: 600, SpeedPercent: 20));

        exit.Should().Be(0, output);
        output.Should().Contain("move: done — Standstill + InPosition after");
        output.Should().MatchRegex(@"ActualVelocity\s+100\.000\s", "20 % of MaxVelocity 500 is reached");
        (await sim.SettledAsync()).PublishedPosition.Should().BeApproximately(600, 0.005);
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_75_JogRunsForItsDurationThenStops()
    {
        using var sim = new LiveSimulator();
        var start = sim.Snapshot.PublishedPosition;

        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Jog, Velocity: -50, For: TimeSpan.FromSeconds(1)));

        exit.Should().Be(0, output);
        output.Should().Contain("State 4 ContinuousMotion").And.Contain("write Command Enable|Stop (0x0011)")
            .And.Contain("jog: done — ContinuousMotion for 1 s (--for), then Stop: halted.");
        var end = await sim.SettledAsync();
        end.PublishedPosition.Should().BeLessThan(start - 30, "about 1 s at −50");
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_76_ResetClearsAFaultAndLeavesTheAxisDisabled()
    {
        using var sim = new LiveSimulator();
        sim.Host.Faults = new SimFaults { DriveFault = true };
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.ErrorStop, "the drive fault");
        sim.Host.Faults = SimFaults.None;

        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Reset));

        exit.Should().Be(0, output);
        output.Should().Contain("write Command Reset (0x0020)").And.Contain("reset: done — not ErrorStop after");
        var end = await sim.SettledAsync();
        end.State.Should().Be(SimAxisState.Disabled);
        end.FaultCode.Should().Be(SimFaultCode.None);
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_77_ANeverAcknowledgedVerbIsProtocolNotAcknowledged()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { SuppressAck = true } });

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        exit.Should().Be(1, output);
        output.Should().Contain("enable: Protocol/NotAcknowledged: Enable 1 not accepted. CommandSeq 1 written, CommandAck 0 read after 500 ms, State 0 read.");
        Last(output).Should().Be("RESULT: FAIL");
        (await sim.SettledAsync()).LeaseOwner.Should().Be(0);
    }

    /// <summary>GA-I-78: every guard refuses before any write — the lease included — with exit 2 and the register named.</summary>
    [Fact]
    public async Task GA_I_78_GuardsRefuseBeforeAnyWrite()
    {
        var cases = new (SimulatedAxisOptions Options, VerbRequest Request, string Line)[]
        {
            (new(), new VerbRequest(Verb.Move, Target: 10_000.5),
                "move: Commander/OutOfRange: refused before writing anything: target 10000.500 is outside TravelMin..TravelMax. Read TravelMin (S+8 = input 8) = 0, TravelMax (S+10 = input 10) = 10000000."),
            (new(), new VerbRequest(Verb.Move, Target: -0.001),
                "move: Commander/OutOfRange: refused before writing anything: target -0.001 is outside TravelMin..TravelMax."),
            (new() { HomedAtPowerUp = false }, new VerbRequest(Verb.Move, Target: 100),
                "move: Commander/NotHomed: refused before writing anything: move needs Homed. Read Flags (S+1 = input 1) = 0x0000, expected bit 0 (Homed) set."),
            (new(), new VerbRequest(Verb.Jog, Velocity: 500.001),
                "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got 500.001. Read MaxVelocity (S+12 = input 12) = 500000."),
            (new(), new VerbRequest(Verb.Jog, Velocity: -500.001), "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got -500.001."),
            (new(), new VerbRequest(Verb.Jog, Velocity: 0), "jog: Commander/UnreachableSpeed:"),
            // #37: the guard is on the RAW value written — 0.0004 unit/s rounds to Velocity 0.
            (new(), new VerbRequest(Verb.Jog, Velocity: 0.0004), "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity"),
            (new(), new VerbRequest(Verb.Jog, Velocity: -0.0004), "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity"),
            // #39: --speed out of 0 < pct ≤ 100 is a guard (exit 2, RESULT: GUARD), not a usage error.
            (new(), new VerbRequest(Verb.Move, Target: 600, SpeedPercent: 150), "move: Commander/UnreachableSpeed: refused before writing anything: speed 150 % outside 0 < pct ≤ 100."),
            (new(), new VerbRequest(Verb.Move, Target: 600, SpeedPercent: 0), "move: Commander/UnreachableSpeed: refused before writing anything: speed 0 % outside 0 < pct ≤ 100."),
            (new(), new VerbRequest(Verb.Move, Target: 600, SpeedPercent: 100.001), "move: Commander/UnreachableSpeed: refused before writing anything: speed 100.001 % outside 0 < pct ≤ 100."),
            (new(), new VerbRequest(Verb.Move, Target: 600, SpeedPercent: 0.00005),
                "move: Commander/UnreachableSpeed: refused before writing anything: --speed 0 % of MaxVelocity rounds to raw Velocity 0"),
            (new() { PublishLimits = false }, new VerbRequest(Verb.Jog, Velocity: 10),
                "jog: Commander/OutOfRange: refused before writing anything: the PLC publishes no limits; jog needs them."),
        };
        foreach (var (options, request, line) in cases)
        {
            using var sim = new LiveSimulator(options);
            var before = await sim.SettledAsync();
            var writes = new System.Collections.Concurrent.ConcurrentQueue<int>();
            sim.Host.OnClientWrite = addresses => { foreach (var a in addresses) writes.Enqueue(a); };

            var (exit, output) = await Command(sim, request);

            exit.Should().Be(2, output);
            writes.Should().BeEmpty($"{request}: a guard refuses before any write — a lease taken and released again is still a write");
            output.Should().Contain(line);
            Last(output).Should().Be("RESULT: GUARD");
            (await sim.SettledAsync()).CommandBlock.Should().Equal(before.CommandBlock, $"{request}: nothing is written, not even the lease");
        }

        using (var at = new LiveSimulator())
        {
            var (exit, output) = await Command(at, new VerbRequest(Verb.Jog, Velocity: -500));
            exit.Should().Be(0, $"|v| = MaxVelocity is allowed (the boundary): {output}");
        }

        using (var at = new LiveSimulator())
        {
            var (exit, output) = await Command(at, new VerbRequest(Verb.Move, Target: -0.0004));
            exit.Should().Be(0, $"-0.0004 is written as raw 0 = TravelMin, inside the travel (#37): {output}");
        }
    }

    [Fact]
    public async Task GA_I_79_CancelMidJogStopsReleasesAndCountsAsDone()
    {
        using var sim = new LiveSimulator();
        using var cts = new CancellationTokenSource();
        var found = await sim.SettledAsync();
        var run = Command(sim, new VerbRequest(Verb.Jog, Velocity: 50), cts.Token);
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.ContinuousMotion, "the jog runs");
        await Task.Delay(200);

        await cts.CancelAsync();
        var (exit, output) = await run;

        exit.Should().Be(0, $"Ctrl-C after ContinuousMotion was observed ends a jog: {output}");
        Last(output).Should().Be("RESULT: PASS");
        // The verb sends the Stop (protocol step 5, lead ruling: same as Python), before its result line; cleanup has
        // nothing left to stop.
        var stopAt = output.IndexOf("write Command Enable|Stop (0x0011)", StringComparison.Ordinal);
        var endedAt = output.IndexOf("jog: ended by the operator after ContinuousMotion was observed; Stop sent: halted.", StringComparison.Ordinal);
        stopAt.Should().BeGreaterThan(0, output);
        endedAt.Should().BeGreaterThan(stopAt, output);
        output.Split('\n').Where(l => l.StartsWith("Cleanup:", StringComparison.Ordinal)).Should().NotContain(l => l.Contains("(Stop)"), output)
            .And.Contain("Cleanup: C+9 = 0 (release lease 65535)");
        await ShouldEndReleased(sim, found);
        (await sim.SettledAsync()).State.Should().Be(SimAxisState.Disabled, "the run enabled the drive, so it disables it");
    }

    [Fact]
    public async Task GA_I_79_CancelMidMoveIsInterruptedAndStopsTheAxis()
    {
        using var sim = new LiveSimulator();
        using var cts = new CancellationTokenSource();
        var found = await sim.SettledAsync();
        var run = Command(sim, new VerbRequest(Verb.Move, Target: 3000, SpeedPercent: 20), cts.Token);
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.DiscreteMotion, "the move runs");

        await cts.CancelAsync();
        var (exit, output) = await run;

        exit.Should().Be(4, output);
        output.Should().Contain("move: interrupted by the operator before the verb completed.").And.Contain("(Stop)");
        Last(output).Should().Be("RESULT: INTERRUPTED");
        await ShouldEndReleased(sim, found);
    }

    [Fact]
    public async Task GA_I_80_ALiveCommanderRefusesTheVerbAndNothingIsWritten()
    {
        using var sim = new LiveSimulator();
        var options = new GenericAxisOptions { Name = "carriage", Host = "127.0.0.1", Port = sim.Port };
        await using var commander = new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), options, ownerId: 1);
        await commander.ConnectAsync();
        var beat = sim.Snapshot.Heartbeat;
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.LeaseOwner == 1 && sim.Snapshot.Heartbeat != beat, "the commander beats");
        var seq = sim.Snapshot.CommandSeq;

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        exit.Should().Be(3, output);
        output.Should().Contain("Pre-flight: refused to start: another commander is beating");
        Last(output).Should().Be("RESULT: REFUSED");
        sim.Snapshot.LeaseOwner.Should().Be(1);
        sim.Snapshot.CommandSeq.Should().Be(seq, "nothing was written");
    }

    [Fact]
    public async Task GA_I_80_AWrongMapVersionIsProtocolAndNothingIsWritten()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { MapVersion = 2 });
        var before = await sim.SettledAsync();
        var writes = new System.Collections.Concurrent.ConcurrentQueue<int>();
        sim.Host.OnClientWrite = addresses => { foreach (var a in addresses) writes.Enqueue(a); };

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        exit.Should().Be(1, output);
        output.Should().Contain("enable: Protocol/ProtocolMismatch: wrong map version. Read MapVersion (S+14 = input 14) = 2, expected 1; nothing written.");
        (await sim.SettledAsync()).CommandBlock.Should().Equal(before.CommandBlock);
        writes.Should().BeEmpty("a PLC on another map version is never written, not even the lease");
    }

    /// <summary>
    /// GA-I-81 (ADR-38 step 1): after a dead holder's trip the mode proceeds as the driver does at attach — it takes the
    /// lease and writes WatchdogFault = 0 — and leaves ErrorStop with FaultCode 4 for <c>reset</c>.
    /// </summary>
    [Fact]
    public async Task GA_I_81_ADeadHoldersTripIsTakenOverAsAtAttachAndLeftForReset()
    {
        using var sim = new LiveSimulator();
        using (var commander = new CheckerAgainstSimulatorTests.RawCommander(sim.Port)) await commander.BeatAsync(TimeSpan.FromSeconds(0.5)); // then dies

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        Cadence.Budget(sim.MaxScanGap, () => exit.Should().Be(1, output));
        output.Should().Contain("Pre-flight: LeaseOwner (C+9 = holding 9) = 1 held with no beat and WatchdogFault (C+10 = holding 10) = 1")
            .And.Contain("the previous commander is dead; its trip is cleared at attach as the driver does; FaultCode 4 is left for reset.")
            .And.Contain("C+10 = holding 10 = 0 written at attach, as the driver does")
            .And.Contain("enable: Machine/WatchdogTripped: the PLC reports ErrorStop. Read FaultCode (S+6 = input 6) = 4.");
        var after = await sim.SettledAsync();
        after.WatchdogFault.Should().Be(0, "cleared at attach, as the driver does");
        after.State.Should().Be(SimAxisState.ErrorStop, "ErrorStop and FaultCode 4 stay for reset");
        after.LeaseOwner.Should().Be(0);

        var (resetExit, resetOutput) = await Command(sim, new VerbRequest(Verb.Reset));
        resetExit.Should().Be(0, resetOutput);
        (await sim.SettledAsync()).State.Should().Be(SimAxisState.Disabled);
    }

    /// <summary>
    /// GA-I-78 with a dead holder's trip (protocol 00f2499 step 3): a refused guard writes nothing at all — not the lease,
    /// not the beat, and not the attach-time WatchdogFault = 0, which comes only after the guards and the lease.
    /// </summary>
    [Fact]
    public async Task GA_I_78_ADeadHoldersTripAndARefusedGuardWriteNothing()
    {
        using var sim = new LiveSimulator();
        using (var commander = new CheckerAgainstSimulatorTests.RawCommander(sim.Port)) await commander.BeatAsync(TimeSpan.FromSeconds(0.5)); // then dies
        var writes = new System.Collections.Concurrent.ConcurrentQueue<int>();
        sim.Host.OnClientWrite = addresses => { foreach (var a in addresses) writes.Enqueue(a); };

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Move, Target: 20_000));

        Cadence.Budget(sim.MaxScanGap, () => output.Should().Contain("held with no beat and WatchdogFault (C+10 = holding 10) = 1"));
        exit.Should().Be(2, output);
        output.Should().Contain("move: Commander/OutOfRange:");
        Last(output).Should().Be("RESULT: GUARD");
        writes.Should().BeEmpty("a refused guard writes nothing: no lease, no beat, no WatchdogFault = 0");
        var end = await sim.SettledAsync();
        end.WatchdogFault.Should().Be(1, "the dead holder's trip is untouched by a refused run");
        end.LeaseOwner.Should().Be(1);
    }

    // ---- #61: Enable is a fresh 0→1 edge; "found energised" comes from State ------------------------------------------

    /// <summary>
    /// A pendant Reset after a fault: the axis ends Disabled with <c>Command</c> bit 0 still 1 — the PLC energises only
    /// on a fresh 0→1 edge (protocol § Enable). Written by a raw client that holds no lease and does not beat.
    /// </summary>
    private static async Task DisabledWithEnableBitSetAsync(LiveSimulator sim)
    {
        using var raw = new FluentModbus.ModbusTcpClient();
        raw.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sim.Port), FluentModbus.ModbusEndianness.BigEndian);
        raw.WriteMultipleRegisters(1, 0, new ushort[] { (ushort)SimCommandBits.Enable, 1 });
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.Standstill, "powered");
        sim.Host.Faults = new SimFaults { DriveFault = true };
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.ErrorStop, "faulted");
        sim.Host.Faults = SimFaults.None;
        raw.WriteMultipleRegisters(1, 0, new ushort[] { (ushort)(SimCommandBits.Enable | SimCommandBits.Reset), 2 });
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.CommandAck == 2, "the Reset acknowledged");
        raw.WriteMultipleRegisters(1, 0, new ushort[] { (ushort)SimCommandBits.Enable, 2 }); // the edge cleared, Enable left at 1
        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.Disabled && sim.Snapshot.CommandWord == 1, "Disabled with bit 0 = 1");
        await Task.Delay(200);
        sim.Snapshot.State.Should().Be(SimAxisState.Disabled, "setup: bit 0 left at 1 does not re-energise the axis by itself");
    }

    /// <summary>GA-I-82 (#61): Disabled with bit 0 = 1 — <c>enable</c> writes Enable 0 first, then Enable 1, passes,
    /// and ends Disabled (it was found not energised).</summary>
    [Fact]
    public async Task GA_I_82_EnableFromDisabledWithTheBitAlreadySetIsAFreshEdgeAndEndsDisabled()
    {
        using var sim = new LiveSimulator();
        await DisabledWithEnableBitSetAsync(sim);
        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Enable));

        exit.Should().Be(0, output);
        var zero = output.IndexOf("write Command none (0x0000)", StringComparison.Ordinal);
        var one = output.IndexOf("write Command Enable (0x0001)", StringComparison.Ordinal);
        zero.Should().BeGreaterThan(0, output);
        one.Should().BeGreaterThan(zero, "Enable 0 first, acked, then the fresh 0→1 edge");
        output.Should().Contain("enable: done — Standstill after");
        var end = await sim.SettledAsync();
        end.State.Should().Be(SimAxisState.Disabled, "found not energised and this run wrote Enable 1: cleanup writes Enable 0");
        end.CommandWord.Should().Be(0);
        await ShouldEndReleased(sim, found);
    }

    /// <summary>GA-I-83 (#61): found powered (Standstill) — <c>home</c> ends Standstill, and cleanup writes no Enable 0.</summary>
    [Fact]
    public async Task GA_I_83_HomeOnAnAxisFoundPoweredLeavesItPowered()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { HomedAtPowerUp = false, InitialPosition = 20 });
        using (var raw = new FluentModbus.ModbusTcpClient())
        {
            raw.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sim.Port), FluentModbus.ModbusEndianness.BigEndian);
            raw.WriteMultipleRegisters(1, 0, new ushort[] { (ushort)SimCommandBits.Enable, 1 });
        }

        await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.Standstill, "found powered");
        var found = await sim.SettledAsync();

        var (exit, output) = await Command(sim, new VerbRequest(Verb.Home));

        exit.Should().Be(0, output);
        output.Should().NotContain("write Command none", "an axis found Standstill needs no Enable 0 edge")
            .And.NotContain("Enable 0)", "found energised: cleanup writes no Enable 0");
        var end = await sim.SettledAsync();
        end.Homed.Should().BeTrue();
        end.State.Should().Be(SimAxisState.Standstill, "a run leaves the axis as energised as it found it");
        await ShouldEndReleased(sim, found);
    }

    /// <summary>GA-I-84 (#61, f52c9e0): CHK-06 from Disabled with bit 0 = 1 writes [0, seq+1] first and passes.</summary>
    [Fact]
    public async Task GA_I_84_Chk06FromDisabledWithTheBitAlreadySetPasses()
    {
        using var sim = new LiveSimulator();
        await DisabledWithEnableBitSetAsync(sim);
        var seq = sim.Snapshot.CommandSeq;

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port }, CancellationToken.None);

        var chk06 = report.Checks.Single(c => c.Id == "CHK-06");
        chk06.Result.Should().Be(CheckResultKind.Pass, chk06.Message);
        report.Checks.Take(11).Should().OnlyContain(c => c.Result == CheckResultKind.Pass, string.Join("\n", report.Checks.Select(c => $"{c.Id} {c.Result} {c.Message}")));
        report.ExitCode.Should().Be(0);
        (await sim.SettledAsync()).CommandSeq.Should().BeGreaterThan((ushort)(seq + 3), "Enable 0, Enable 1 and Enable 0 again at least");
    }

    // ---- the command line (GA-U-145) -------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "--command", "enable", "plc" }, Verb.Enable)]
    [InlineData(new[] { "--check", "plc", "--command", "stop" }, Verb.Stop)]
    [InlineData(new[] { "--command", "home", "plc:5020", "--allow-motion" }, Verb.Home)]
    public void GA_U_145_VerbsParse(string[] args, Verb verb)
    {
        var (o, error) = CheckCommandLine.Parse(args);
        error.Should().BeNull();
        o!.Command!.Verb.Should().Be(verb);
    }

    [Fact]
    public void GA_U_145_MoveAndJogTakeNumbersIncludingNegativeOnes()
    {
        CheckCommandLine.Parse(["--command", "move", "100", "plc", "--speed", "150", "--allow-motion"]).Options!.Command!.SpeedPercent
            .Should().Be(150, "the range is the guard's (exit 2, RESULT: GUARD), not a usage error (#39)");
        var move = CheckCommandLine.Parse(["--command", "move", "-12.5", "plc", "--speed", "20", "--allow-motion"]).Options!.Command!;
        move.Should().Be(new VerbRequest(Verb.Move, Target: -12.5, SpeedPercent: 20));
        CheckCommandLine.Parse(["--command", "move", "100", "plc", "--allow-motion"]).Options!.Command!.SpeedPercent.Should().Be(10);
        var jog = CheckCommandLine.Parse(["--command", "jog", "-50", "plc", "--for", "2", "--allow-motion"]).Options!.Command!;
        jog.Should().Be(new VerbRequest(Verb.Jog, Velocity: -50, For: TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("--command", "home", "plc")]
    [InlineData("--command", "move", "100", "plc")]
    [InlineData("--command", "jog", "5", "plc")]
    [InlineData("--command", "Enable", "plc")]
    [InlineData("--command", "3", "plc")]
    [InlineData("--command", "fly", "plc")]
    [InlineData("--command", "move", "plc", "--allow-motion")]
    [InlineData("--command", "move", "100", "plc", "--allow-motion", "--speed", "fast")]
    [InlineData("--command", "enable", "plc", "--speed", "20")]
    [InlineData("--command", "enable", "plc", "--for", "2")]
    [InlineData("--command", "jog", "5", "plc", "--allow-motion", "--for", "0")]
    [InlineData("--command", "enable", "plc", "--dump")]
    [InlineData("--command", "enable", "plc", "--report", "r.md")]
    [InlineData("--command", "enable", "--command", "stop", "plc")]
    [InlineData("--check", "plc", "--speed", "20")]
    [InlineData("--command")]
    public void GA_U_145_BadCommandLinesAreUsageErrors(params string[] args)
    {
        var (o, error) = CheckCommandLine.Parse(args);
        o.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// #32 holds for <c>--command</c>: through Program.cs it never starts the UI. A bad verb exits 2 at once, and a valid
    /// one against a closed port exits 1 (Transport) — both without a web host.
    /// </summary>
    [Theory]
    [InlineData(2, "--command", "fly", "127.0.0.1:1")]
    [InlineData(1, "--command", "enable", "127.0.0.1:1")]
    public async Task GA_U_145_CommandWithoutCheckNeverStartsTheUi(int expected, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["Simulator__Port"] = "0";
        psi.Environment["urls"] = "http://127.0.0.1:0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var all = await stdout + await stderr;
        process.ExitCode.Should().Be(expected, all);
        all.Should().NotContain("UI on", "the web host never starts");
        if (expected == 1) (await stdout).TrimEnd().Should().EndWith("RESULT: FAIL").And.Contain("enable: Transport/CommunicationLost:");
    }
}
