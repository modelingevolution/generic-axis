using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using ModelingEvolution.GenericAxis.TestApp.Simulation;
using RocketWelder.SDK.Abstractions;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-I-30.cs … GA-I-38.cs — the C# checker, in-process, against a live simulator over Modbus TCP
/// (test-scenarios.md § Conformance checker against the simulator).
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class CheckerAgainstSimulatorTests
{
    private static async Task<ConformanceReport> Check(LiveSimulator sim, bool allowMotion, CancellationToken ct = default,
        Action<ConformanceReport>? progress = null) =>
        await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = allowMotion }, ct, progress);

    private static CheckResult Get(ConformanceReport r, string id) => r.Checks.Single(c => c.Id == id);

    private static long Observed(CheckResult c, string key) =>
        c.Observed.Single(kv => kv.Key == key).Value ?? throw new InvalidOperationException($"{c.Id}.{key} is null");

    private static IEnumerable<string> Ids(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => $"CHK-{i:D2}");

    private static void ShouldBe(ConformanceReport r, CheckResultKind kind, IEnumerable<string> ids)
    {
        foreach (var id in ids) Get(r, id).Result.Should().Be(kind, $"{id}: {Get(r, id).Message}");
    }

    [Fact]
    public async Task GA_I_30_TheSimulatorPassesTheWholeChecklist()
    {
        using var sim = new LiveSimulator();

        var report = await Check(sim, allowMotion: true);

        ShouldBe(report, CheckResultKind.Pass, Ids(1, 16));
        report.ExitCode.Should().Be(0);
        Observed(Get(report, "CHK-08"), "tripAfterMs").Should().BeInRange(1000, 1500);
        Observed(Get(report, "CHK-09"), "secondTripAfterMs").Should().BeInRange(1000, 1500);
        Observed(Get(report, "CHK-11"), "takenAfterMs").Should().BeInRange(0, 2000);
        Observed(Get(report, "CHK-14"), "haltMs").Should().BeLessThanOrEqualTo(200);
        Observed(Get(report, "CHK-16"), "tripAfterMs").Should().BeInRange(1000, 1500);
        Observed(Get(report, "CHK-16"), "haltAfterTripMs").Should().BeLessThanOrEqualTo(200);
        Observed(Get(report, "CHK-16"), "homedAfterTrip").Should().Be(1);

        foreach (var c in report.Checks)
            c.Observed.Select(kv => kv.Key).Should().Equal(CheckCatalog.All.Single(d => d.Id == c.Id).ReportedKeys, $"{c.Id} reports the protocol's keys");
        report.Checks.Should().OnlyContain(c => c.Observed.All(kv => kv.Value != null), "a full PASS observes every value");
        var end = await sim.SettledAsync();
        end.State.Should().Be(SimAxisState.Disabled);
        end.LeaseOwner.Should().Be(0);
        end.WatchdogFault.Should().Be(0);
        end.Velocity.Should().Be(0);
        report.Cleanup.Should().Contain(l => l.Contains("release lease"));
    }

    [Fact]
    public async Task GA_I_31_MotionChecksAreOptIn()
    {
        using var sim = new LiveSimulator();
        var before = sim.Snapshot.TruePosition;
        var moved = false;
        using var watch = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watch.IsCancellationRequested)
            {
                if (sim.Snapshot.Velocity != 0 || sim.Snapshot.TruePosition != before) moved = true;
                await Task.Delay(5);
            }
        });

        var report = await Check(sim, allowMotion: false);
        await watch.CancelAsync();
        await watcher;

        ShouldBe(report, CheckResultKind.Pass, Ids(1, 11));
        foreach (var id in Ids(12, 16)) Get(report, id).Message.Should().Be("needs --allow-motion");
        ShouldBe(report, CheckResultKind.Skipped, Ids(12, 16));
        report.ExitCode.Should().Be(0);
        moved.Should().BeFalse("without --allow-motion the position never changes");
        sim.Snapshot.TruePosition.Should().Be(before);
    }

    [Fact]
    public async Task GA_I_32_AWrongMapVersionStopsTheRunAndWritesNothing()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { MapVersion = 2 });

        var report = await Check(sim, allowMotion: true);

        Get(report, "CHK-01").Result.Should().Be(CheckResultKind.Pass);
        Get(report, "CHK-02").Result.Should().Be(CheckResultKind.Fail);
        Get(report, "CHK-02").Message.Should().Be("Protocol/ProtocolMismatch: wrong map version. Read MapVersion (S+14 = 114) = 2, expected 1.");
        Get(report, "CHK-02").ErrorClass.Should().Be(ErrorClass.Protocol);
        Get(report, "CHK-02").LastRead!.Status[14].Should().Be(2);
        ShouldBe(report, CheckResultKind.Skipped, Ids(3, 16));
        report.ExitCode.Should().Be(1);
        (await sim.SettledAsync()).CommandBlock.Should().OnlyContain(w => w == 0, "nothing is written when the map version is wrong");
        report.Cleanup.Should().BeEmpty();
    }

    [Fact]
    public async Task GA_I_33_UnpublishedLimitsFailChk03Only()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { PublishLimits = false });

        var report = await Check(sim, allowMotion: true);

        var chk03 = Get(report, "CHK-03");
        chk03.Result.Should().Be(CheckResultKind.Fail);
        chk03.Message.Should().StartWith("Protocol/ProtocolMismatch: limits not published (all 0).");
        chk03.Observed.Select(kv => kv.Value).Should().Equal(0, 0, 0, 0);
        ShouldBe(report, CheckResultKind.Pass, Ids(4, 12));
        ShouldBe(report, CheckResultKind.Skipped, Ids(13, 16));
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_34_APlcWithoutTheWatchdogIsCaught()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { WatchdogDisabled = true } });

        var report = await Check(sim, allowMotion: true);

        Get(report, "CHK-08").Result.Should().Be(CheckResultKind.Fail);
        Get(report, "CHK-08").Message.Should().StartWith("Protocol/ProtocolMismatch: no trip within 1.5 s of the last beat.");
        Get(report, "CHK-08").ErrorClass.Should().Be(ErrorClass.Protocol);
        ShouldBe(report, CheckResultKind.Skipped, ["CHK-09", "CHK-10", "CHK-16"]);
        Get(report, "CHK-16").Message.Should().Be("needs CHK-08, which FAILED", "the kill test never runs without a proven watchdog");
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_35_APlcThatNeverAcknowledgesIsCaught()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { SuppressAck = true } });

        var report = await Check(sim, allowMotion: true);

        Get(report, "CHK-06").Result.Should().Be(CheckResultKind.Fail);
        Get(report, "CHK-06").Message.Should().StartWith("Protocol/NotAcknowledged: Enable 1 not accepted. CommandSeq 1 written, CommandAck 0 read after 500 ms, State 0 read.");
        Get(report, "CHK-06").ErrorClass.Should().Be(ErrorClass.Protocol);
        ShouldBe(report, CheckResultKind.Skipped, ["CHK-07", "CHK-08", "CHK-09", "CHK-10", "CHK-12", "CHK-13", "CHK-14", "CHK-15", "CHK-16"]);
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_36_APlcWithTheWordsSwappedIsCaught()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { SwappedWordOrder = true } });

        var report = await Check(sim, allowMotion: false);

        var chk03 = Get(report, "CHK-03");
        chk03.Result.Should().Be(CheckResultKind.Fail);
        chk03.Message.Should().StartWith("Protocol/ProtocolMismatch: limits not sane.");
        // 10 000 000 = 0x00989680 served high word first reads back as 0x96800098.
        Observed(chk03, "travelMax").Should().Be(unchecked((int)0x96800098));
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_37_TheCheckerNeverFightsALiveCommander()
    {
        using var sim = new LiveSimulator();
        var options = new GenericAxisOptions { Name = "carriage", Host = "127.0.0.1", Port = sim.Port };
        await using var commander = new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), options, ownerId: 1);
        await commander.ConnectAsync();
        await Task.Delay(300);
        var before = sim.Snapshot;
        before.LeaseOwner.Should().Be(1);

        var started = DateTime.UtcNow;
        var report = await Check(sim, allowMotion: true);
        var took = DateTime.UtcNow - started;

        report.ExitCode.Should().Be(3);
        report.Refused.Should().BeTrue();
        report.Checks.Should().HaveCount(16).And.OnlyContain(c => c.Result == CheckResultKind.Skipped);
        took.Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(700));
        var after = await sim.SettledAsync();
        after.CommandSeq.Should().Be(before.CommandSeq);
        after.LeaseOwner.Should().Be(1, "the commander's lease is untouched");
        after.CommandBlock.Skip(2).Take(6).Should().Equal(before.CommandBlock.Skip(2).Take(6), "the parameters are untouched");
        after.WatchdogTrips.Should().Be(0);
        report.Cleanup.Should().BeEmpty();
    }

    [Fact]
    public async Task PreflightRefusesOnAnyBeatEvenWithLeaseOwnerZero()
    {
        using var sim = new LiveSimulator();
        using var other = new FluentModbus.ModbusTcpClient();
        other.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sim.Port), FluentModbus.ModbusEndianness.BigEndian);
        using var stop = new CancellationTokenSource();
        var beating = Task.Run(async () =>
        {
            for (ushort beat = 1; !stop.IsCancellationRequested; beat++)
            {
                other.WriteSingleRegister(1, 8, beat); // a second tool that never takes the lease
                await Task.Delay(100);
            }
        });

        var report = await Check(sim, allowMotion: false);
        await stop.CancelAsync();
        await beating;

        report.ExitCode.Should().Be(3);
        report.Checks.Should().OnlyContain(c => c.Result == CheckResultKind.Skipped && c.Message.StartsWith("refused to start: another commander is beating"));
        report.Checks[0].Message.Should().Contain("LeaseOwner (C+9 = 9) = 0");
        (await sim.SettledAsync()).CommandSeq.Should().Be(0, "a refused run writes nothing");
    }

    [Fact]
    public async Task GA_I_38_InterruptingARunCleansUp()
    {
        using var sim = new LiveSimulator();
        using var cts = new CancellationTokenSource();
        var interrupted = Task.Run(async () =>
        {
            // Wait for CHK-14's move, then Ctrl-C.
            while (!cts.IsCancellationRequested)
            {
                if (_chk14Running && sim.Snapshot.State == SimAxisState.DiscreteMotion && sim.Snapshot.Velocity > 0)
                {
                    await cts.CancelAsync();
                    return;
                }

                await Task.Delay(5);
            }
        });

        var report = await Check(sim, allowMotion: true, cts.Token, r => _chk14Running |= r.Running == "CHK-14");
        await interrupted;

        cts.IsCancellationRequested.Should().BeTrue("the run was interrupted during CHK-14");
        ShouldBe(report, CheckResultKind.Skipped, Ids(14, 16));
        foreach (var id in Ids(14, 16)) Get(report, id).Message.Should().Be("interrupted by the operator during CHK-14");
        report.Interrupted.Should().BeTrue();
        report.SummaryResult.Should().Be("INTERRUPTED");
        report.ExitCode.Should().Be(4, "an interruption is not a FAIL");
        var end = await sim.SettledAsync();
        end.State.Should().BeOneOf(SimAxisState.Disabled, SimAxisState.Standstill);
        end.Velocity.Should().Be(0);
        end.LeaseOwner.Should().Be(0);
        report.Cleanup.Should().Contain(l => l.Contains("(Stop)"))
            .And.Contain(l => l.Contains("Enable 0"))
            .And.Contain(l => l.Contains("release lease"));
    }

    [Fact]
    public async Task AFaultTheCheckDidNotExpectIsAMachineError()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { DriveFault = true } });
        await Task.Delay(50);

        var report = await Check(sim, allowMotion: false);

        var chk06 = Get(report, "CHK-06");
        chk06.Result.Should().Be(CheckResultKind.Fail);
        chk06.ErrorClass.Should().Be(ErrorClass.Machine);
        chk06.Message.Should().StartWith("Machine/DriveFault: the PLC reports ErrorStop. Read FaultCode (S+6 = 106) = 1. also Machine/DriveFault: cannot restore");
        chk06.LastRead!.Status[0].Should().Be(7);
    }

    [Fact]
    public async Task DumpReadsBothBlocksAndWritesNothing()
    {
        using var sim = new LiveSimulator();
        var options = new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, Dump = true };
        var before = await sim.SettledAsync();

        var code = await CheckMode.DumpAsync(options, NullLoggerFactory.Instance, CancellationToken.None);

        code.Should().Be(0);
        var after = await sim.SettledAsync();
        after.CommandBlock.Should().Equal(before.CommandBlock, "--dump writes nothing and takes no lease");

        var dead = await CheckMode.DumpAsync(options with { Port = 1 }, NullLoggerFactory.Instance, CancellationToken.None);
        dead.Should().Be(1, "a Transport error is exit 1");
    }

    private volatile bool _chk14Running;
}
