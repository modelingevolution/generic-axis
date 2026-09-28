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

    private static long Observed(CheckResult c, string key) => c.Observed.Single(kv => kv.Key == key).Value;

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
        Observed(Get(report, "CHK-11"), "cTakenAfterMs").Should().BeInRange(0, 2000);
        Observed(Get(report, "CHK-14"), "haltMs").Should().BeLessThanOrEqualTo(200);
        Observed(Get(report, "CHK-16"), "tripAfterMs").Should().BeInRange(1000, 1500);
        Observed(Get(report, "CHK-16"), "haltAfterTripMs").Should().BeLessThanOrEqualTo(200);
        Observed(Get(report, "CHK-16"), "homed").Should().Be(1);

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
        Get(report, "CHK-02").Message.Should().Be("MapVersion 2, expected 1");
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
        chk03.Message.Should().Contain("all 0");
        chk03.Observed.Select(kv => kv.Value).Should().Equal(0, 0, 0);
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
        Get(report, "CHK-08").Message.Should().Be("no trip within 1.5 s of the last beat");
        ShouldBe(report, CheckResultKind.Skipped, ["CHK-09", "CHK-10"]);
        // protocol.md: CHK-16 needs 15 only, so it runs and fails the same way. GA-I-34 expects it SKIPPED, which needs
        // "08, 15" in the table — raised with the protocol owner; this line follows the table until it changes.
        Get(report, "CHK-16").Result.Should().Be(CheckResultKind.Fail);
        Get(report, "CHK-16").Message.Should().StartWith("no trip");
        (await sim.SettledAsync()).Velocity.Should().Be(0, "the restore after the failed kill test stops the jog");
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_35_APlcThatNeverAcknowledgesIsCaught()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { SuppressAck = true } });

        var report = await Check(sim, allowMotion: true);

        Get(report, "CHK-06").Result.Should().Be(CheckResultKind.Fail);
        Get(report, "CHK-06").Message.Should().StartWith("no CommandAck within 500 ms");
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
        chk03.Message.Should().Contain("is not below TravelMax");
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
        Get(report, "CHK-14").Message.Should().Contain("interrupted");
        ShouldBe(report, CheckResultKind.Skipped, Ids(15, 16));
        var end = await sim.SettledAsync();
        end.State.Should().BeOneOf(SimAxisState.Disabled, SimAxisState.Standstill);
        end.Velocity.Should().Be(0);
        end.LeaseOwner.Should().Be(0);
        report.Cleanup.Should().Contain(l => l.Contains("(Stop)"))
            .And.Contain(l => l.Contains("Enable 0"))
            .And.Contain(l => l.Contains("release lease"));
    }

    private volatile bool _chk14Running;
}
