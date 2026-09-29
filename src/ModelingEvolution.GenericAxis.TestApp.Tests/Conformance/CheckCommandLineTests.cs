using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-61.cs — the command line parses per protocol.md § Command line.</summary>
public sealed class CheckCommandLineTests
{
    [Fact]
    public void GA_U_61_DefaultsApply()
    {
        var (o, error) = CheckCommandLine.Parse(["--check", "plc.local"]);

        error.Should().BeNull();
        o!.Host.Should().Be("plc.local");
        o.Port.Should().Be(502);
        o.Unit.Should().Be(1);
        o.CommandBase.Should().Be(0);
        o.StatusBase.Should().Be(100);
        o.OwnerId.Should().Be(65535);
        o.AllowMotion.Should().BeFalse();
        o.Tolerance.Should().Be(0.1);
        o.Report.Should().BeNull();
        o.Dump.Should().BeFalse();
    }

    [Fact]
    public void DumpAndWatchParse()
    {
        var (o, error) = CheckCommandLine.Parse(["--check", "plc:5020", "--dump", "--watch"]);
        error.Should().BeNull();
        o!.Dump.Should().BeTrue();
        o.Watch.Should().BeTrue();
        CheckCommandLine.Parse(["plc", "--watch"]).Error.Should().Contain("--watch needs --dump");
    }

    [Fact]
    public void GA_U_61_EveryArgumentApplies()
    {
        var (o, error) = CheckCommandLine.Parse(["--check", "10.0.0.5:5020", "--unit", "3", "--allow-motion", "--report", "r.md",
            "--command-base", "200", "--status-base", "300", "--owner-id", "4000", "--tolerance", "0.25"]);

        error.Should().BeNull();
        o!.Host.Should().Be("10.0.0.5");
        o.Port.Should().Be(5020);
        o.Unit.Should().Be(3);
        o.AllowMotion.Should().BeTrue();
        o.Report.Should().Be(new ReportTarget("r.md", "r.json"));
        o.CommandBase.Should().Be(200);
        o.StatusBase.Should().Be(300);
        o.OwnerId.Should().Be(4000);
        o.Tolerance.Should().Be(0.25);
    }

    [Fact]
    public void GA_U_61_AJsonReportWritesJsonOnly()
    {
        var (o, _) = CheckCommandLine.Parse(["plc", "--report", "out/r.json"]);
        o!.Report.Should().Be(new ReportTarget(null, "out/r.json"));
    }

    [Theory]
    [InlineData("plc", "--unit", "abc")]
    [InlineData("plc", "--unit", "256")]
    [InlineData("plc:0")]
    [InlineData("plc:x")]
    [InlineData("plc", "--report", "r.txt")]
    [InlineData("plc", "--owner-id", "0")]
    [InlineData("plc", "--owner-id", "65534")]
    [InlineData("plc", "--tolerance", "0")]
    [InlineData("plc", "--command-base", "95")]
    [InlineData("plc", "--bogus")]
    [InlineData("--check")]
    [InlineData("plc", "--unit")]
    public void GA_U_61_BadArgumentsAreAUsageError(params string[] args)
    {
        var (o, error) = CheckCommandLine.Parse(args);
        o.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GA_U_61_UsageErrorExitsWith2()
    {
        var code = await CheckMode.RunAsync(["--check", "plc", "--unit", "abc"]);
        code.Should().Be(ConformanceExitCodes.Usage).And.Be(2);
    }
}
