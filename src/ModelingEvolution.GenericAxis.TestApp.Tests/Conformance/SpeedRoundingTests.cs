using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// protocol § One-verb mode, Speed rounding (352314e, review #65): one rounding for the checks and the one-verb mode,
/// raw = round-half-away-from-zero(pct × MaxVelocity raw ÷ 100), and a raw 0 refused, never floored.
/// </summary>
public sealed class SpeedRoundingTests
{
    /// <summary>GA-U-147: half away from zero (not banker's, not truncation), and no floor.</summary>
    [Theory]
    [InlineData(10, 500_000, 50_000)]
    [InlineData(10, 45, 5)]      // 4.5 → 5 (banker's: 4)
    [InlineData(10, 25, 3)]      // 2.5 → 3 (banker's: 2)
    [InlineData(1, 50, 1)]       // 0.5 → 1 (banker's: 0, truncation: 0)
    [InlineData(1, 150, 2)]      // 1.5 → 2 (truncation: 1)
    [InlineData(1, 149, 1)]      // 1.49 → 1 (reviewer-python-2 #43)
    [InlineData(1, 250, 3)]      // 2.5 → 3 (banker's: 2)
    [InlineData(1, 49, 0)]       // 0.49 → 0, never floored to 1
    [InlineData(1, 45, 0)]
    [InlineData(10, 4, 0)]       // 0.4 → 0
    public void GA_U_147_RoundsHalfAwayFromZeroWithoutAFloor(double percent, int maxVelocityRaw, int expected) =>
        SpeedRounding.Raw(percent, maxVelocityRaw).Should().Be(expected);

    [Fact]
    public void GA_U_147_TheBodyIsTheCheckSkipAndPrintsThePercentInFull()
    {
        SpeedRounding.Body("1", 45, "S+12 = input 12").Should().Be(
            "1 % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero(1 × 45 ÷ 100) = 0); nothing to move with. Read MaxVelocity (S+12 = input 12) = 45.");
        SpeedRounding.Body("0.00005", 500_000, "S+12 = input 12").Should().StartWith("0.00005 % of MaxVelocity", "the percentage as given, never rounded or in exponent form");
        SpeedRounding.Body("0.0000001", 100, "S+12 = input 12").Should().StartWith("0.0000001 % of MaxVelocity");
        // #68: the percentage is the text as given, never re-rendered from a double (which drops 1e-30 to "0" and cuts
        // 33.3333333333333333 at 15 significant digits).
        SpeedRounding.Body("1e-30", 500_000, "S+12 = input 12").Should().StartWith("1e-30 % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero(1e-30 × 500000 ÷ 100) = 0)");
        SpeedRounding.Body("33.3333333333333333", 1, "S+12 = input 12").Should().StartWith("33.3333333333333333 % of MaxVelocity");
    }

    [Fact]
    public void GA_U_147_ARawZeroAt49IsRefusedNamingAllThree() =>
        SpeedRounding.Refusal("1", 49, "S+12 = input 12").Should().Be(
            "Commander/UnreachableSpeed: refused before writing anything: 1 % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero(1 × 49 ÷ 100) = 0); nothing to move with. Read MaxVelocity (S+12 = input 12) = 49.");

    [Fact]
    public void GA_U_147_TheRefusalNamesMaxVelocityThePercentageAndTheRawResult() =>
        SpeedRounding.Refusal("1", 45, "S+12 = input 12").Should().Be(
            "Commander/UnreachableSpeed: refused before writing anything: 1 % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero(1 × 45 ÷ 100) = 0); nothing to move with. Read MaxVelocity (S+12 = input 12) = 45.");

    /// <summary>
    /// GA-U-148: the helper is the one both call sites use — CHK-13…16 and <c>--command move --speed</c> go through
    /// <see cref="SpeedRounding.Raw"/>, and no hand-rolled speed (the old <c>Math.Max(1, max / 10)</c>) remains.
    /// </summary>
    [Fact]
    public void GA_U_148_EverySpeedGoesThroughTheOneHelper()
    {
        var catalog = Source("CheckCatalog.cs");
        foreach (var check in new[] { "Chk13", "Chk14", "Chk15", "Chk16" })
        {
            var body = Method(catalog, check);
            body.Should().Contain("var speed = SpeedRounding.Raw(", check).And.Contain("CheckOutcome.Skipped(SpeedRounding.Body(", check);
            body.Should().NotContainAny(["Math.Max(1", "max / 10", "max / 100", "MidpointRounding"], check);
        }

        var runner = Source("CommandRunner.cs");
        runner.Should().Contain("var velocity = SpeedRounding.Raw(request.SpeedPercent, s.MaxVelocity);")
            .And.Contain("SpeedRounding.Raw(pct, s.MaxVelocity) == 0")
            .And.NotContain("MaxVelocity * request.SpeedPercent");
    }

    private static string Method(string source, string name)
    {
        var start = source.IndexOf($"Task<CheckOutcome> {name}(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, name);
        var end = source.IndexOf("private static", start + 10, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string Source(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ModelingEvolution.GenericAxis.TestApp", "Conformance", file);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(file);
    }
}

/// <summary>GA-I-85 (#65): the checker's speeds follow Speed rounding against a live simulator.</summary>
[Collection(LiveCollection.Name)]
public sealed class SpeedRoundingCheckerTests
{
    /// <summary>
    /// MaxVelocity raw 45 (homed at 9.98, so CHK-13's move to TravelMin + 10 is 20 counts, 4 s at raw 5): 1 % = 0.45 → raw 0, so CHK-15 SKIPs with the refusal and no MoveVelocity is ever written
    /// (CHK-16 is SKIPPED needing it); 10 % = 4.5 → raw 5 (half away from zero), so CHK-13/14 still run at 5.
    /// </summary>
    [Fact]
    public async Task GA_I_85_ASpeedRoundingToRawZeroSkipsItsCheckAndWritesNothing()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { MaxVelocity = 0.045, InitialPosition = 9.98, HomeSensorPosition = 9.98 });
        var moveVelocityWrites = 0;
        sim.Host.OnClientWrite = addresses =>
        {
            if (addresses.Contains(SimRegisters.Command)
                && (sim.Host.Registers.Holding.Read(SimRegisters.Command) & (ushort)SimCommandBits.MoveVelocity) != 0)
                Interlocked.Increment(ref moveVelocityWrites);
        };

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = true }, CancellationToken.None);

        string Dump() => string.Join("\n", report.Checks.Select(c => $"{c.Id} {c.Result} {c.Message}"));
        report.Checks.Take(14).Should().OnlyContain(c => c.Result == CheckResultKind.Pass, Dump());
        report.Checks.Single(c => c.Id == "CHK-14").Observed.Single(kv => kv.Key == "commandedVelocity").Value
            .Should().Be(5, "10 % of 45 = 4.5 rounds half away from zero to 5");
        var chk15 = report.Checks.Single(c => c.Id == "CHK-15");
        chk15.Result.Should().Be(CheckResultKind.Skipped, Dump());
        chk15.Message.Should().Be(SpeedRounding.Body("1", 45, "S+12 = input 12"), "the check SKIPs with the body alone, no class prefix (e49dd62)");
        chk15.Observed.Should().BeEmpty("a SKIPPED check has observed {}");
        // A machine-property skip, like an unmet precondition — never INCONCLUSIVE, so the release gate (#43) does not count it.
        report.Checks.Should().NotContain(c => c.Message.Contains("INCONCLUSIVE", StringComparison.OrdinalIgnoreCase));
        ReportWriter.ToJson(report).Should().NotContain("INCONCLUSIVE");
        report.Checks.Single(c => c.Id == "CHK-16").Message.Should().Be("needs CHK-15, which SKIPPED");
        moveVelocityWrites.Should().Be(0, "a speed that rounds to raw 0 is refused, never written");
        report.ExitCode.Should().Be(0, "SKIPPED is allowed");
    }

    /// <summary>
    /// GA-I-86 (#67): the raw-0 skip writes nothing even when the check is entered from Disabled — where a skip placed
    /// after EnsureEnabled would write Enable. Path: a drive fault injected at CHK-14's MoveAbsolute fails CHK-14
    /// (Machine), the restore Resets the axis to Disabled (the fault clears as that Reset lands), and CHK-15 starts from
    /// Disabled. The sim's write journal records every client write while CHK-15 runs; the beat loop's Heartbeat (C+8)
    /// is not the check's write and is excluded.
    /// </summary>
    [Fact]
    public async Task GA_I_86_ASelfSkippingCheckEnteredFromDisabledWritesNothing()
    {
        using var sim = new LiveSimulator(new SimulatedAxisOptions { MaxVelocity = 0.045, InitialPosition = 9.98, HomeSensorPosition = 9.98 });
        string? running = null;
        var injected = false;
        SimAxisState? stateAtChk15 = null;
        var writesDuringChk15 = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sim.Host.OnClientWrite = addresses =>
        {
            if (Volatile.Read(ref running) == "CHK-15")
                foreach (var a in addresses.Where(a => a != SimRegisters.Heartbeat))
                    writesDuringChk15.Enqueue($"holding {a} = {sim.Host.Registers.Holding.Read(a)}");
            if (!addresses.Contains(SimRegisters.Command)) return;
            var command = (SimCommandBits)sim.Host.Registers.Holding.Read(SimRegisters.Command);
            if (Volatile.Read(ref running) == "CHK-14" && !injected && command.HasFlag(SimCommandBits.MoveAbsolute))
            {
                injected = true;
                sim.Host.Faults = new SimFaults { DriveFault = true };
            }
            else if (injected && command.HasFlag(SimCommandBits.Reset))
            {
                sim.Host.Faults = SimFaults.None;
            }
        };

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = true }, CancellationToken.None,
            r =>
            {
                if (r.Running == "CHK-15") stateAtChk15 = sim.Snapshot.State;
                Volatile.Write(ref running, r.Running);
            });

        string Dump() => string.Join("\n", report.Checks.Select(c => $"{c.Id} {c.Result} {c.Message}"));
        injected.Should().BeTrue(Dump());
        report.Checks.Single(c => c.Id == "CHK-14").Message.Should().StartWith("Machine/DriveFault", Dump());
        stateAtChk15.Should().Be(SimAxisState.Disabled, "the restore after CHK-14 had to Reset: CHK-15 is entered from Disabled");
        var chk15 = report.Checks.Single(c => c.Id == "CHK-15");
        chk15.Result.Should().Be(CheckResultKind.Skipped, Dump());
        chk15.Message.Should().Be(SpeedRounding.Body("1", 45, "S+12 = input 12"), "the check SKIPs with the body alone, no class prefix (e49dd62)");
        writesDuringChk15.Should().BeEmpty("a check that skips itself on a raw-0 speed writes nothing, Enable included");
    }
}
