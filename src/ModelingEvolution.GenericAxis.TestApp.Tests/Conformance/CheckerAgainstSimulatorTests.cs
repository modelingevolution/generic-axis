using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.Drawing.Units;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using ModelingEvolution.GenericAxis.TestApp.Simulation;
using ModelingEvolution.GenericAxis.TestApp.Tests.Support;
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
        report.SummaryResult.Should().Be("REFUSED", "a refused run is never reported as PASS (review #22)");
        ReportWriter.ToMarkdown(report).TrimEnd().Should().EndWith("RESULT: REFUSED");
        report.Checks.Should().HaveCount(16).And.OnlyContain(c => c.Result == CheckResultKind.Skipped);
        took.Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(700));
        var after = await sim.SettledAsync();
        after.CommandSeq.Should().Be(before.CommandSeq);
        after.LeaseOwner.Should().Be(1, "the commander's lease is untouched");
        after.CommandBlock.Skip(2).Take(6).Should().Equal(before.CommandBlock.Skip(2).Take(6), "the parameters are untouched");
        after.WatchdogTrips.Should().Be(0);
        report.Cleanup.Should().BeEmpty();
    }

    /// <summary>
    /// GA-I-51 (review #29): Ctrl-C inside the 1 s pre-flight watch, with a live commander moving the axis. Before
    /// pre-flight has proved the axis free the checker has written nothing, so it must end INTERRUPTED (exit 4) with a
    /// report and without one write — no Stop, no Enable 0, no CommandSeq taken from the commander.
    /// </summary>
    [Fact]
    public async Task GA_I_51_InterruptingPreflightWritesNothingToALiveCommandersAxis()
    {
        using var sim = new LiveSimulator();
        var options = new GenericAxisOptions { Name = "carriage", Host = "127.0.0.1", Port = sim.Port };
        await using var commander = new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), options, ownerId: 1);
        await commander.ConnectAsync();
        var axis = (ModbusLinearAxis)commander.Axis;
        await axis.PowerAsync(true);
        await axis.MoveVelocityAsync(new Speed<double, MillimetrePerSecond<double>>(20));
        var before = await sim.SettledAsync();
        before.State.Should().Be(SimAxisState.ContinuousMotion);
        before.LeaseOwner.Should().Be(1);

        // Cancel on the runner's first line, which it logs before pre-flight: always inside pre-flight, never a timer.
        using var cts = new CancellationTokenSource();
        var log = new RecordingLoggerProvider();
        log.Logger.OnMessage = m => { if (m.StartsWith("Conformance check of", StringComparison.Ordinal)) cts.Cancel(); };
        using var factory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Information));
        var report = await new ConformanceRunner(factory).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = true }, cts.Token);

        report.ExitCode.Should().Be(4);
        report.SummaryResult.Should().Be("INTERRUPTED");
        report.Checks.Should().HaveCount(16).And.OnlyContain(c =>
            c.Result == CheckResultKind.Skipped && c.Message == "interrupted by the operator during pre-flight");
        report.Cleanup.Should().BeEmpty("nothing was written, so there is nothing to undo");

        var after = await sim.SettledAsync();
        after.CommandSeq.Should().Be(before.CommandSeq, "the checker took no sequence number");
        after.CommandBlock[0].Should().Be(before.CommandBlock[0], "no Stop, no Enable 0");
        after.CommandBlock.Skip(2).Take(6).Should().Equal(before.CommandBlock.Skip(2).Take(6), "the parameters are untouched");
        after.LeaseOwner.Should().Be(1, "the commander's lease is untouched");
        after.State.Should().Be(SimAxisState.ContinuousMotion, "the commander's move is still running");
        after.WatchdogTrips.Should().Be(0);

        await axis.StopAsync();
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
        report.SummaryResult.Should().Be("REFUSED");
        ReportWriter.ToMarkdown(report).TrimEnd().Should().EndWith("RESULT: REFUSED");
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
    /// <summary>
    /// A fault the check did not expect is a Machine error; and (GA-I-58, ruling "Each check restores") a CHK-06
    /// precondition FAIL is a failure to restore: every later check, CHK-11 included, is SKIPPED naming CHK-06, and
    /// nothing Resets an axis the checker did not fault.
    /// </summary>
    public async Task AFaultTheCheckDidNotExpectIsAMachineError()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { DriveFault = true } });
        await Task.Delay(50);

        var report = await Check(sim, allowMotion: false);

        var chk06 = Get(report, "CHK-06");
        chk06.Result.Should().Be(CheckResultKind.Fail);
        chk06.ErrorClass.Should().Be(ErrorClass.Machine);
        chk06.Message.Should().Be("Machine/DriveFault: the PLC reports ErrorStop. Read FaultCode (S+6 = 106) = 1.");
        chk06.LastRead!.Status[0].Should().Be(7);
        foreach (var id in Ids(7, 11)) Get(report, id).Message.Should().Be("restore after CHK-06 failed", id);
        var end = await sim.SettledAsync();
        end.CommandSeq.Should().Be(0, "no command, so no Reset, was written to an axis the checker did not fault");
        end.State.Should().Be(SimAxisState.ErrorStop);
        ((int)end.FaultCode).Should().Be(1);
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

    /// <summary>
    /// GA-I-52 (review #33, lead ruling): a foreign LeaseOwner written mid-run fails the running check
    /// Protocol/ProtocolMismatch naming the register, SKIPs every later check with that reason, and the cleanup writes
    /// nothing to the axis the checker no longer owns.
    /// </summary>
    [Fact]
    public async Task GA_I_52_ALeaseOwnerChangedMidRunFailsTheRunningCheckAndSkipsTheRest()
    {
        using var sim = new LiveSimulator();
        using var intruder = new FluentModbus.ModbusTcpClient();
        intruder.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, sim.Port), FluentModbus.ModbusEndianness.BigEndian);
        var written = false;
        ushort seqAtIntrusion = 0;

        var report = await Check(sim, allowMotion: false, progress: r =>
        {
            if (written || r.Running != "CHK-07") return;
            written = true;
            intruder.WriteSingleRegister(1, 9, 1); // before CHK-07 starts: the checker holds the lease and beats
            seqAtIntrusion = sim.Snapshot.CommandSeq;
        });

        written.Should().BeTrue();
        var chk07 = Get(report, "CHK-07");
        chk07.Result.Should().Be(CheckResultKind.Fail);
        chk07.ErrorClass.Should().Be(ErrorClass.Protocol);
        chk07.Message.Should().StartWith("Protocol/ProtocolMismatch: the lease did not hold. Read LeaseOwner (C+9 = 9) = 1, expected 65535.");
        foreach (var id in Ids(8, 16))
            Get(report, id).Message.Should().Be("CHK-07: the lease did not hold. Read LeaseOwner (C+9 = 9) = 1, expected 65535.", id);
        report.ExitCode.Should().Be(1);
        report.Cleanup.Should().NotContain(l => l.StartsWith("C+0", StringComparison.Ordinal) || l.Contains("release lease"),
            "nothing is written to an axis the checker no longer owns");
        report.Cleanup.Should().Contain(l => l.Contains("stopped beating"), "the checker's own beat is stopped")
            .And.NotContain(l => l.StartsWith("cleanup incomplete", StringComparison.Ordinal), "a lost lease is a clean end, not a cleanup error");

        var end = await sim.SettledAsync();
        end.LeaseOwner.Should().Be(1, "the intruder's lease is untouched");
        end.CommandSeq.Should().BeLessThanOrEqualTo((ushort)(seqAtIntrusion + 1), "at most the command already in flight");
    }

    [Fact]
    public async Task GA_I_53_DumpOnAClosedPort_StatesEachFactOnce()
    {
        var error = new StringWriter();
        var code = await CheckMode.DumpAsync(new CheckerOptions { Host = "127.0.0.1", Port = 1, Dump = true },
            NullLoggerFactory.Instance, CancellationToken.None, TextWriter.Null, error);

        code.Should().Be(1);
        var line = error.ToString().Trim();
        line.Should().StartWith("dump: Transport/CommunicationLost: read C+0…C+11");
        Count(line, "127.0.0.1:1").Should().Be(1, line);
        Count(line, "unit 1").Should().Be(1, line);
        Count(line, "CommunicationLost").Should().Be(1, line);
        line.Should().NotContain("..", line);
    }

    [TimingFact]
    public async Task GA_I_53_DumpWatch_RunsAtFiveHertz()
    {
        using var sim = new LiveSimulator();
        var output = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var code = await CheckMode.DumpAsync(new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, Dump = true, Watch = true },
            NullLoggerFactory.Instance, cts.Token, output, TextWriter.Null);

        code.Should().Be(0);
        Cadence.Budget(sim.MaxScanGap, () =>
            Count(output.ToString(), $"127.0.0.1:{sim.Port} unit 1\n").Should().BeGreaterThanOrEqualTo(10, "5 Hz for 2 s"));
    }

    private static int Count(string text, string what) => (text.Length - text.Replace(what, "").Length) / what.Length;

    /// <summary>
    /// GA-I-54 (review #25 residual): one link drop mid-check is the one reconnect-and-retry — the check still PASSes,
    /// reports <c>retries: 1</c>, and the retry is logged once at Warning.
    /// </summary>
    [Fact]
    public async Task GA_I_54_ALinkDroppedOnceMidCheckIsCountedInRetries()
    {
        using var sim = new LiveSimulator();
        var log = new RecordingLoggerProvider();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information));
        Task? drop = null;
        var armed = false;
        // The drop follows the simulator serving CHK-05's second parameter write (C+2 = 0xFFFE): an event, not a timer.
        // It runs once the served write releases the scan lock, and re-opens the port at once.
        sim.Host.OnClientWrite = addresses =>
        {
            if (!armed || !addresses.Contains(SimRegisters.TargetPosition) || sim.Host.Registers.Read(SimRegisters.TargetPosition) != 0xFFFE) return;
            armed = false;
            drop = Task.Run(() =>
            {
                sim.Host.Faults = new SimFaults { CommunicationDown = true };
                sim.Host.Faults = SimFaults.None;
            });
        };

        var report = await new ConformanceRunner(factory).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port }, CancellationToken.None, r =>
            {
                if (r.Running == "CHK-05" && drop is null) armed = true;
            });
        drop.Should().NotBeNull("setup: CHK-05 wrote its second parameter");
        await drop!;

        var chk05 = Get(report, "CHK-05");
        chk05.Result.Should().Be(CheckResultKind.Pass, chk05.Message);
        Observed(chk05, "retries").Should().Be(1);
        report.Checks.Where(c => c.Id != "CHK-05").Should().OnlyContain(c => c.Observed.All(kv => kv.Key != "retries" || kv.Value == 0),
            "only the check the link dropped in retried");
        log.Logger.At(Microsoft.Extensions.Logging.LogLevel.Warning).Where(m => m.Contains("reconnecting and retrying once"))
            .Should().ContainSingle();
    }

    // ---- Review #35: a held lease is watched to 1.6 s ----------------------------------------------------------------

    /// <summary>A raw commander: takes the lease as owner 1 and beats every 100 ms until told to pause or stop.</summary>
    private sealed class RawCommander : IDisposable
    {
        private readonly FluentModbus.ModbusTcpClient _client = new() { ConnectTimeout = 2000, ReadTimeout = 2000, WriteTimeout = 2000 };
        private readonly Lock _io = new();
        private ushort _beat;

        public RawCommander(int port)
        {
            _client.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), FluentModbus.ModbusEndianness.BigEndian);
            Write(9, 1);
        }

        public void Write(ushort address, ushort value)
        {
            lock (_io) _client.WriteSingleRegister(1, address, value);
        }

        public async Task BeatAsync(TimeSpan duration)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                _beat = (ushort)(_beat % 65535 + 1);
                Write(8, _beat);
                await Task.Delay(100);
            }
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>GA-I-55: the twin of GA-I-37 with a starved commander — its beat pauses 1.1 s, longer than the old 1 s
    /// window, on a PLC that trips late (1.4 s). It is alive, so the tool must still refuse and write nothing.</summary>
    [TimingFact(Timeout = 180_000)]
    public async Task GA_I_55_ACommanderSilentFor1Point1sIsStillRefused()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { WatchdogTimeout = TimeSpan.FromSeconds(1.4) });
        using var commander = new RawCommander(sim.Port);
        await commander.BeatAsync(TimeSpan.FromSeconds(0.5));
        var before = await sim.SettledAsync();

        var paused = Task.Run(async () => { await Task.Delay(1100); await commander.BeatAsync(TimeSpan.FromSeconds(1)); });
        var report = await Check(sim, allowMotion: false); // starts inside the pause
        await paused;

        var after = await sim.SettledAsync();
        after.CommandSeq.Should().Be(before.CommandSeq, "a refused run writes nothing");
        after.CommandBlock.Skip(2).Take(6).Should().Equal(before.CommandBlock.Skip(2).Take(6));
        Cadence.Budget(sim.MaxScanGap, () =>
        {
            // The scenario is a timing one: a 1.1 s pause on a PLC that trips at 1.4 s.
            report.ExitCode.Should().Be(3, report.Preflight);
            report.SummaryResult.Should().Be("REFUSED");
            report.Preflight.Should().StartWith("refused to start: another commander is beating");
            after.LeaseOwner.Should().Be(1);
            after.WatchdogTrips.Should().Be(0, "the commander was slow, not dead");
        });
    }

    /// <summary>GA-I-55: a lease held with no beat and no trip within 1.6 s is refused, with the hand-release advice.</summary>
    [Fact]
    public async Task GA_I_55_AHeldLeaseThatNeitherBeatsNorTripsIsRefused()
    {
        using var sim = new LiveSimulator();
        using var commander = new RawCommander(sim.Port); // LeaseOwner = 1, never beats: the watchdog never arms

        var report = await Check(sim, allowMotion: false);

        report.ExitCode.Should().Be(3);
        report.Preflight.Should().Be("refused to start: LeaseOwner (C+9 = 9) = 1 is held and WatchdogFault (C+10 = 10) = 0: no beat and no trip "
                                     + "within 1.6 s — a live commander, or a PLC without a working watchdog; release LeaseOwner by hand only if no commander runs");
        ReportWriter.ToMarkdown(report).Split('\n')[2].Should().StartWith("Pre-flight: refused to start: LeaseOwner");
        using (var json = System.Text.Json.JsonDocument.Parse(ReportWriter.ToJson(report)))
            json.RootElement.GetProperty("preflight").GetString().Should().Be(report.Preflight);
        (await sim.SettledAsync()).CommandSeq.Should().Be(0);
    }

    /// <summary>GA-I-56: the lease holder died (beat stopped for good); the PLC trips 1.0 s later, inside the 1.6 s watch.
    /// The tool proceeds, says so on the line after the heading, and never clears that trip or the lease.</summary>
    [TimingFact]
    public async Task GA_I_56_ADeadCommandersTripIsSeenAndLeftForItsOperator()
    {
        using var sim = new LiveSimulator();
        using (var commander = new RawCommander(sim.Port)) await commander.BeatAsync(TimeSpan.FromSeconds(0.5)); // then dies

        var report = await Check(sim, allowMotion: false);

        Cadence.Budget(sim.MaxScanGap, () => report.Refused.Should().BeFalse("the trip lands 1.0 s after the last beat, inside the 1.6 s watch"));
        report.Preflight.Should().Be("LeaseOwner (C+9 = 9) = 1 held with no beat and WatchdogFault (C+10 = 10) = 1: "
                                     + "the previous commander is dead; its trip is left for its operator.");
        ReportWriter.ToMarkdown(report).Split('\n')[2].Should().Be("Pre-flight: " + report.Preflight);
        using (var json = System.Text.Json.JsonDocument.Parse(ReportWriter.ToJson(report)))
            json.RootElement.GetProperty("preflight").GetString().Should().Be(report.Preflight, "the same text in JSON");
        ShouldBe(report, CheckResultKind.Pass, Ids(1, 5));
        var chk06 = Get(report, "CHK-06");
        chk06.Result.Should().Be(CheckResultKind.Fail);
        chk06.Message.Should().Be("Machine/WatchdogTripped: the PLC reports ErrorStop. Read FaultCode (S+6 = 106) = 4.");
        foreach (var id in Ids(7, 11)) Get(report, id).Message.Should().Be("restore after CHK-06 failed", id);
        report.Cleanup.Should().BeEmpty("the checker took no lease, beat or command, and does not clear a foreign trip");
        var end = await sim.SettledAsync();
        end.WatchdogFault.Should().Be(1, "the dead commander's trip is left for its operator");
        end.LeaseOwner.Should().Be(1);
        end.State.Should().Be(SimAxisState.ErrorStop);
    }

    /// <summary>
    /// GA-I-57 (review #35 (b), the live repro): the lease and the beat start as soon as pre-flight passes, so a second
    /// tool started during the first's CHK-03 is refused, and the first run is unaffected.
    /// </summary>
    [TimingFact(Timeout = 180_000)]
    public async Task GA_I_57_ASecondToolStartedDuringTheFirstsChk03IsRefused()
    {
        using var sim = new LiveSimulator();
        Task<ConformanceReport>? second = null;
        // The simulator's own write journal, in the order it served the writes, plus a marker when CHK-01 starts. The
        // tool publishes CHK-01 only after its lease write was answered, so the order is causal, not a sampled moment.
        var journal = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sim.Host.OnClientWrite = addresses =>
        {
            foreach (var a in addresses) journal.Enqueue($"C+{a} = {sim.Host.Registers.Read(a)}");
        };

        var first = await Check(sim, allowMotion: false, progress: r =>
        {
            if (r.Running == "CHK-01" && !journal.Contains("CHK-01 running")) journal.Enqueue("CHK-01 running");
            if (r.Running == "CHK-03" && second is null) second = Check(sim, allowMotion: false);
        });
        var refused = await second!;
        sim.Host.OnClientWrite = null;

        var entries = journal.ToList();
        var lease = entries.IndexOf("C+9 = 65535");
        lease.Should().BeGreaterThanOrEqualTo(0, "the tool took the lease");
        entries.Take(lease).Where(e => !e.StartsWith("C+8 = ", StringComparison.Ordinal)).Should().BeEmpty("the lease is the tool's first write");
        lease.Should().BeLessThan(entries.IndexOf("CHK-01 running"), "the lease is taken before CHK-01: " + string.Join(", ", entries.Take(8)));
        refused.ExitCode.Should().Be(3, refused.Preflight);
        refused.SummaryResult.Should().Be("REFUSED");
        refused.Preflight.Should().StartWith("refused to start: another commander is beating")
            .And.Contain("LeaseOwner (C+9 = 9) = 65535");
        Cadence.Budget(sim.MaxScanGap, () => // the first run includes CHK-04's and CHK-08's timing thresholds
            first.ExitCode.Should().Be(0, string.Join("; ", first.Checks.Where(c => c.Result == CheckResultKind.Fail).Select(c => $"{c.Id}: {c.Message}"))));
        (await sim.SettledAsync()).LeaseOwner.Should().Be(0, "the first run released its lease in cleanup");
    }

    /// <summary>
    /// GA-I-59 (Python #4 mirror, lead ruling): a PLC that accepts TCP but answers nothing. The pre-flight read fails, so
    /// the axis was never proved free: CHK-01 FAILs Transport with that read's message, every later check needs CHK-01,
    /// and nothing is written — no lease, no beat. The PLC starts answering the moment pre-flight gives up, so a runner
    /// that went on anyway (the old "CHK-01 will report it") would take the lease and write.
    /// </summary>
    [Fact]
    public async Task GA_I_59_AFailedPreflightReadStopsTheRunAndWritesNothing()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { Faults = new SimFaults { Silent = true } });
        var log = new RecordingLoggerProvider();
        var gaveUp = false;
        log.Logger.OnMessage = m =>
        {
            if (gaveUp || !m.StartsWith("Pre-flight could not read the PLC", StringComparison.Ordinal)) return;
            gaveUp = true;
            sim.Host.Faults = SimFaults.None;
        };
        using var factory = LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Information));

        var report = await new ConformanceRunner(factory).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port }, CancellationToken.None);

        gaveUp.Should().BeTrue("the pre-flight read met a silent PLC");
        report.ExitCode.Should().Be(1);
        var chk01 = Get(report, "CHK-01");
        chk01.Result.Should().Be(CheckResultKind.Fail);
        chk01.ErrorClass.Should().Be(ErrorClass.Transport);
        chk01.Message.Should().StartWith("Transport/CommunicationLost: read C+8…C+10 (pre-flight: Heartbeat, LeaseOwner, WatchdogFault)");
        report.Checks.Skip(1).Should().OnlyContain(c => c.Result == CheckResultKind.Skipped && c.Message == "needs CHK-01, which FAILED");
        report.Cleanup.Should().BeEmpty();
        var end = await sim.SettledAsync();
        end.CommandBlock.Should().OnlyContain(w => w == 0, "no lease, no beat, no write");
    }

    /// <summary>
    /// GA-I-60 (Python review #25, both tools): a commander that tripped and still beats is alive. WatchdogFault = 1 is
    /// evidence of death only together with a Heartbeat silent for the whole 1 s watch, so the tool must refuse and write
    /// nothing, never declare it dead at the first read.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_60_ACommanderStillBeatingAfterATripIsRefused()
    {
        using var sim = new LiveSimulator();
        using var commander = new RawCommander(sim.Port);
        await commander.BeatAsync(TimeSpan.FromSeconds(0.5));
        var tripWait = System.Diagnostics.Stopwatch.StartNew(); // the watchdog trips 1.0 s after the last beat
        while (sim.Snapshot.WatchdogFault == 0 && tripWait.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        sim.Snapshot.WatchdogFault.Should().Be(1, "setup: the commander's axis tripped");
        using var stop = new CancellationTokenSource();
        var beating = Task.Run(async () => { while (!stop.IsCancellationRequested) await commander.BeatAsync(TimeSpan.FromMilliseconds(100)); });
        await Task.Delay(300);
        var before = await sim.SettledAsync();

        var report = await Check(sim, allowMotion: false);
        await stop.CancelAsync();
        await beating;

        report.ExitCode.Should().Be(3, report.Preflight);
        report.Preflight.Should().StartWith("refused to start: another commander is beating");
        var after = await sim.SettledAsync();
        after.CommandBlock.Skip(2).Take(6).Should().Equal(before.CommandBlock.Skip(2).Take(6), "CHK-05 never ran: the parameters are untouched");
        after.CommandSeq.Should().Be(before.CommandSeq);
        after.LeaseOwner.Should().Be(1);
        after.WatchdogFault.Should().Be(1, "the commander's trip is its own");
    }

    /// <summary>
    /// GA-I-61 (Python review #26 mirror): a second Ctrl-C 20 ms after the first, while the axis moves, must not abort
    /// the cleanup. The real process gets two SIGINTs and must end INTERRUPTED (exit 4) with the axis stopped, Enable 0,
    /// the lease released and no WatchdogFault left behind.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_61_ASecondCtrlCDuringCleanupDoesNotAbortIt()
    {
        using var sim = new LiveSimulator();
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll");
        var workDir = Directory.CreateTempSubdirectory("ga-i-61-").FullName;
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--check", $"127.0.0.1:{sim.Port}", "--allow-motion", "--report", Path.Combine(workDir, "r.md") },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var moving = System.Diagnostics.Stopwatch.StartNew();
            // CHK-12 homes for several seconds: interrupt while the axis moves under the checker's command.
            while (sim.Snapshot.State is not (SimAxisState.Homing or SimAxisState.DiscreteMotion or SimAxisState.ContinuousMotion))
            {
                moving.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(90), "setup: the run reaches a motion check");
                if (process.HasExited) throw new InvalidOperationException($"setup: the run ended before any motion: {await stderr}");
                await Task.Delay(10);
            }

            Signal(process.Id);
            await Task.Delay(20);
            Signal(process.Id);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        process.ExitCode.Should().Be(4, await stderr);
        (await stdout).TrimEnd().Should().EndWith("RESULT: INTERRUPTED");
        var end = await sim.SettledAsync();
        end.Velocity.Should().Be(0, "the cleanup's Stop was not cut short");
        end.CommandBlock[0].Should().Be(0, "the cleanup ended with Enable 0 and the edge bits cleared");
        end.LeaseOwner.Should().Be(0, "the cleanup released the lease");
        end.WatchdogFault.Should().Be(0);
        end.State.Should().Be(SimAxisState.Disabled);
        Directory.Delete(workDir, recursive: true);

        static void Signal(int pid)
        {
            using var kill = System.Diagnostics.Process.Start("kill", ["-INT", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])!;
            kill.WaitForExit();
        }
    }

    /// <summary>
    /// GA-I-62 (review #39): the motion budgets are the ones the checks use. A drive that starts every move late
    /// (MotionStartDelay, set just before the check) makes CHK-13 overrun 2 × |target − start| ÷ velocity + 5 s, and
    /// CHK-16 overrun its 2 s to first motion; a longer budget would wait it out and PASS.
    /// </summary>
    [TimingFact(Timeout = 180_000)]
    public Task GA_I_62_Chk13FailsAtItsArrivalBudget() =>
        GA_I_62_ACheckFailsAtItsOwnMotionBudget("CHK-13", 8.0, "Machine/MotionFailed: not arrived within 5.4 s (2 × |target − start| ÷ velocity + 5 s).");

    [TimingFact(Timeout = 180_000)]
    public Task GA_I_62_Chk16FailsAtItsFirstMotionBudget() =>
        GA_I_62_ACheckFailsAtItsOwnMotionBudget("CHK-16", 3.0, "Machine/MotionFailed: the jog never moved within 2 s of the ack.");

    private static async Task GA_I_62_ACheckFailsAtItsOwnMotionBudget(string id, double delaySeconds, string message)
    {
        using var sim = new LiveSimulator();
        var report = await Check(sim, allowMotion: true, progress: r =>
        {
            if (r.Running == id && sim.Host.Faults.MotionStartDelay == TimeSpan.Zero)
                sim.Host.Faults = new SimFaults { MotionStartDelay = TimeSpan.FromSeconds(delaySeconds) };
        });

        var check = Get(report, id);
        Cadence.Budget(sim.MaxScanGap, () =>
        {
            check.Result.Should().Be(CheckResultKind.Fail, check.Message);
            check.Message.Should().StartWith(message);
            check.DurationMs.Should().BeLessThan((long)(delaySeconds * 1000), "the check gave up at its budget, before the late drive moved");
        });
    }

    /// <summary>
    /// GA-I-63 (review #40): the lease-loss guard stops the running check at once. The intruder takes the lease right
    /// after CHK-06's Enable 1; CHK-06 then must not send its second command (Enable 0) to an axis it no longer owns.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_63_ALeaseLostMidCheckStopsThatCheckBeforeItsNextCommand()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { EnableDelay = TimeSpan.FromMilliseconds(300) });
        ushort seqAtIntrusion = 0;
        var armed = false;
        // The intruder answers CHK-06's Enable 1 inside the served write (the command word and CommandSeq arrive in one
        // FC16), so the intrusion precedes the checker's next request deterministically.
        sim.Host.OnClientWrite = addresses =>
        {
            if (!armed || !addresses.Contains(SimRegisters.Command) || (sim.Host.Registers.Read(SimRegisters.Command) & 1) == 0) return;
            armed = false;
            seqAtIntrusion = sim.Host.Registers.Read(SimRegisters.CommandSeq);
            sim.Host.Registers.Write(SimRegisters.LeaseOwner, 1);
        };

        var report = await Check(sim, allowMotion: false, progress: r =>
        {
            if (r.Running == "CHK-06" && seqAtIntrusion == 0) armed = true;
        });
        seqAtIntrusion.Should().NotBe(0, $"setup: CHK-06 sent Enable 1 ({Get(report, "CHK-06").Message})");

        var chk06 = Get(report, "CHK-06");
        chk06.Result.Should().Be(CheckResultKind.Fail);
        chk06.Message.Should().StartWith("Protocol/ProtocolMismatch: the lease did not hold. Read LeaseOwner (C+9 = 9) = 1, expected 65535.");
        var end = await sim.SettledAsync();
        end.CommandSeq.Should().Be(seqAtIntrusion, "no command after the lease was lost: Enable 0 was never sent");
        end.LeaseOwner.Should().Be(1);
    }

    /// <summary>
    /// GA-I-64 (review #41): a commander that takes the lease between CHK-11(a)'s release and its lease client's first read
    /// (deterministically, inside the served release write) is reported Protocol/ProtocolMismatch with the register read,
    /// never Commander/LeaseHeld; (b) does not impersonate over it, and nothing more is written to its axis.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_64_ACommanderTakingTheLeaseInChk11aIsProtocolNotCommander()
    {
        using var sim = new LiveSimulator();
        using var intruder = new RawCommander(sim.Port);
        intruder.Write(9, 0); // RawCommander takes the lease at construction; give it back, it waits for CHK-11
        var armed = false;
        using var stop = new CancellationTokenSource();
        Task? beating = null;
        sim.Host.OnClientWrite = addresses =>
        {
            if (!armed || !addresses.Contains(SimRegisters.LeaseOwner) || sim.Host.Registers.Read(SimRegisters.LeaseOwner) != 0) return;
            armed = false;
            sim.Host.Registers.Write(SimRegisters.LeaseOwner, 1); // the release is answered by another commander
        };

        var report = await Check(sim, allowMotion: false, progress: r =>
        {
            if (r.Running != "CHK-11" || beating is not null) return;
            beating = Task.Run(async () => { while (!stop.IsCancellationRequested) await intruder.BeatAsync(TimeSpan.FromMilliseconds(100)); });
            armed = true;
        });
        await stop.CancelAsync();
        await beating!;

        var chk11 = Get(report, "CHK-11");
        chk11.Result.Should().Be(CheckResultKind.Fail);
        chk11.ErrorClass.Should().Be(ErrorClass.Protocol, chk11.Message);
        chk11.Message.Should().StartWith("Protocol/ProtocolMismatch: (a) another commander took the lease after its release. Read LeaseOwner (C+9 = 9) = 1,");
        report.Cleanup.Should().NotContain(l => l.StartsWith("C+", StringComparison.Ordinal) && !l.Contains("stopped beating"),
            "nothing is written to the axis another commander owns");
        var end = await sim.SettledAsync();
        end.LeaseOwner.Should().Be(1, "neither (b)'s 65534 nor a restore overwrote the other commander's lease");
    }

    /// <summary>
    /// GA-I-65 (2-vCPU runner, GA-I-61's real cause): CHK-11(c)'s incumbent dies and its trip lands 1.0–1.5 s after its
    /// last beat, possibly after the lease client took the lease. CHK-11 settles that trip before restoring, so the
    /// restore always sees and clears it, whatever the PLC's trip time.
    /// </summary>
    [TimingFact(Timeout = 180_000)]
    public Task GA_I_65_TripAt1s() => GA_I_65_Chk11RestoresAfterTheIncumbentsTripWhateverItsTiming(1.0);

    [TimingFact(Timeout = 180_000)]
    public Task GA_I_65_TripAt1Point25s() => GA_I_65_Chk11RestoresAfterTheIncumbentsTripWhateverItsTiming(1.25);

    /// <summary>1.5 s exactly would put the 10 ms-scanned trip at ~1520 ms, a legitimate CHK-08 FAIL.</summary>
    [TimingFact(Timeout = 180_000)]
    public Task GA_I_65_TripAt1Point45s() => GA_I_65_Chk11RestoresAfterTheIncumbentsTripWhateverItsTiming(1.45);

    private static async Task GA_I_65_Chk11RestoresAfterTheIncumbentsTripWhateverItsTiming(double tripSeconds)
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { WatchdogTimeout = TimeSpan.FromSeconds(tripSeconds) });
        int? tripsAtChk11 = null;

        var report = await Check(sim, allowMotion: false, progress: r =>
        {
            if (r.Running == "CHK-11") tripsAtChk11 ??= sim.Snapshot.WatchdogTrips; // CHK-10's restore has settled by now
        });

        // The mechanism: the incumbent's death is always settled into its one trip before the restore, which clears it.
        // Without the settle, a late-tripping PLC is beaten by the restore's first beat and the race stays hidden.
        ((await sim.SettledAsync()).WatchdogTrips - tripsAtChk11).Should().Be(1, "CHK-11 waited for the incumbent's trip");
        var chk11 = Get(report, "CHK-11");
        chk11.Result.Should().Be(CheckResultKind.Pass, chk11.Message);
        Cadence.Budget(sim.MaxScanGap, () => // the whole run includes CHK-04's and CHK-08's timing thresholds
            report.ExitCode.Should().Be(0, string.Join("; ", report.Checks.Where(c => c.Result == CheckResultKind.Fail).Select(c => $"{c.Id}: {c.Message}"))));
        (await sim.SettledAsync()).WatchdogFault.Should().Be(0, "cleanup cleared the trip the checker caused");
    }

    private volatile bool _chk14Running;
}
