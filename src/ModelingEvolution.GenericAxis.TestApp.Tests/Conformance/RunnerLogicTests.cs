using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-63.cs — a failed or skipped prerequisite skips its dependants; motion checks are opt-in.</summary>
[Collection(LiveCollection.Name)]
public sealed class RunnerLogicTests
{
    /// <summary>The real catalog's ids and needs with scripted outcomes, so only the runner's logic is under test.</summary>
    private static IReadOnlyList<CheckDefinition> Scripted(Func<string, CheckOutcome> outcome, List<string> ran) =>
    [
        .. CheckCatalog.All.Select(d => d with
        {
            RunAsync = (_, _) =>
            {
                ran.Add(d.Id);
                return Task.FromResult(outcome(d.Id));
            },
        }),
    ];

    private static async Task<ConformanceReport> Run(IReadOnlyList<CheckDefinition> catalog, bool allowMotion)
    {
        using var sim = new LiveSimulator();
        var options = new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = allowMotion };
        return await new ConformanceRunner(NullLoggerFactory.Instance, catalog).RunAsync(options, CancellationToken.None);
    }

    [Fact]
    public async Task GA_U_63_AFailedPrerequisiteSkipsEveryDependant()
    {
        var ran = new List<string>();
        var catalog = Scripted(id => id == "CHK-02" ? CheckOutcome.Fail("MapVersion 2, expected 1") : CheckOutcome.Pass("ok"), ran);

        var report = await Run(catalog, allowMotion: true);

        ran.Should().Equal("CHK-01", "CHK-02");
        report.Checks.Select(c => c.Result).Should().Equal(
            [CheckResultKind.Pass, CheckResultKind.Fail, .. Enumerable.Repeat(CheckResultKind.Skipped, 14)]);
        foreach (var c in report.Checks.Skip(2))
        {
            c.Message.Should().MatchRegex(@"^needs CHK-\d{2}, which (FAILED|was SKIPPED)$");
        }

        report.Checks.Single(c => c.Id == "CHK-03").Message.Should().Be("needs CHK-02, which FAILED");
        report.Checks.Single(c => c.Id == "CHK-09").Message.Should().Be("needs CHK-08, which was SKIPPED");
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_U_63_WithoutAllowMotionTheMotionChecksAreSkipped()
    {
        var ran = new List<string>();
        var report = await Run(Scripted(_ => CheckOutcome.Pass("ok"), ran), allowMotion: false);

        ran.Should().Equal(CheckCatalog.All.Take(11).Select(d => d.Id));
        foreach (var c in report.Checks.Skip(11))
        {
            c.Result.Should().Be(CheckResultKind.Skipped);
            c.Message.Should().Be("needs --allow-motion");
        }

        report.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task AFailedRestoreSkipsEveryLaterCheck()
    {
        var ran = new List<string>();
        using var sim = new LiveSimulator(new() { Faults = new() { SuppressAck = true } });
        // CHK-07 leaves the axis in ErrorStop; with SuppressAck the restore's Reset is never acknowledged.
        var catalog = Scripted(id => CheckOutcome.Pass("ok"), ran).Select(d => d.Id != "CHK-07" ? d : d with
        {
            RunAsync = async (ctx, ct) =>
            {
                ran.Add(d.Id);
                sim.Host.Faults = new() { SuppressAck = true, DriveFault = true };
                await Task.Delay(100, ct);
                sim.Host.Faults = new() { SuppressAck = true };
                return CheckOutcome.Pass("ok");
            },
        }).ToList();

        var options = new CheckerOptions { Host = "127.0.0.1", Port = sim.Port };
        var report = await new ConformanceRunner(NullLoggerFactory.Instance, catalog).RunAsync(options, CancellationToken.None);

        var chk07 = report.Checks.Single(c => c.Id == "CHK-07");
        chk07.Result.Should().Be(CheckResultKind.Fail);
        chk07.Message.Should().Contain("cannot restore");
        report.Checks.SkipWhile(c => c.Id != "CHK-08").Should().OnlyContain(c =>
            c.Result == CheckResultKind.Skipped && c.Message == "CHK-07 could not restore the axis");
    }
}
