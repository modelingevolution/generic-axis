using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// The fixture-cadence INCONCLUSIVE mechanism (adr.md, lead ruling "Two-vCPU flakes"): the gap recorder, the budget
/// rule (red unless the fixture missed cadence), and the skip the runner reports.
/// </summary>
public class CadenceTests
{
    [Fact(DisplayName = "GA-U-87 MiniPlc records the longest scan gap since the reset, including one still open")]
    public async Task MiniPlc_StalledScan_GapRecordedUntilReset()
    {
        await using var plc = new MiniPlc();
        await plc.NextScanAsync();
        plc.ResetMaxScanGap();

        plc.OnScan(() => Thread.Sleep(150));
        await plc.NextScanAsync();
        await plc.NextScanAsync();
        plc.MaxScanGap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "the stalled scan delayed the next one");

        plc.ResetMaxScanGap();
        await plc.NextScanAsync();
        plc.MaxScanGap.Should().BeLessThan(TimeSpan.FromMilliseconds(150), "the reset forgot the stall");

        var open = new ManualResetEventSlim();
        plc.OnScan(() => open.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        plc.MaxScanGap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "a scan thread stuck right now counts");
        open.Set();
    }

    private static readonly TimeSpan OnCadence = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan Missed = TimeSpan.FromMilliseconds(51);

    [Fact(DisplayName = "GA-U-88 A budget failure is RED on cadence and INCONCLUSIVE only when the fixture missed it")]
    public async Task Budget_FailsOnlyInconclusiveWhenFixtureMissedCadence()
    {
        Action fails = () => 1.2.Should().BeLessThanOrEqualTo(1.05);

        // On cadence (≤ 50 ms): the budget failure stays a failure.
        FluentActions.Invoking(() => Cadence.Budget(OnCadence, fails)).Should().Throw<XunitException>();
        FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromMilliseconds(50), fails)).Should().Throw<XunitException>();

        // Missed: INCONCLUSIVE naming the gap and carrying the budget failure.
        var inconclusive = FluentActions.Invoking(() => Cadence.Budget(Missed, fails)).Should().Throw<InconclusiveException>().Which;
        inconclusive.Message.Should().Contain("fixture missed cadence: max scan gap 51 ms").And.Contain("1.05");
        inconclusive.InnerException.Should().BeOfType<XunitException>();

        // Never skipped without a failure.
        FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromSeconds(5), () => 1.0.Should().BeLessThanOrEqualTo(1.05)))
            .Should().NotThrow();

        // The async form reads the gap when the step fails: on cadence → the step's own exception.
        await using var plc = new MiniPlc();
        await plc.NextScanAsync();
        plc.ResetMaxScanGap();
        await FluentActions.Awaiting(() => Cadence.Budget(plc, () => throw new TimeoutException("step")))
            .Should().ThrowAsync<TimeoutException>();
        plc.OnScan(() => Thread.Sleep(150));
        await plc.NextScanAsync();
        await FluentActions.Awaiting(() => Cadence.Budget(plc, () => throw new TimeoutException("step")))
            .Should().ThrowAsync<InconclusiveException>().WithMessage("*max scan gap*step*");
    }

    [Fact(DisplayName = "GA-U-89 [TimingFact] reports an INCONCLUSIVE failure as skipped and every other failure as failed")]
    public void TimingFact_InconclusiveBecomesSkipOthersStayFailed()
    {
        var inner = new Recorder();
        using var bus = new TimingTestCase.InconclusiveBus(inner);
        var method = new TestMethod(
            new TestClass(new TestCollection(new TestAssembly(Reflector.Wrap(typeof(CadenceTests).Assembly)), null, "cadence"),
                Reflector.Wrap(typeof(CadenceTests))),
            Reflector.Wrap(typeof(CadenceTests).GetMethod(nameof(TimingFact_Runs))!));
        var test = new XunitTest(new XunitTestCase(new NullMessageSink(), TestMethodDisplay.Method,
            TestMethodDisplayOptions.None, method), "stub");

        bus.QueueMessage(new TestFailed(test, 0, "", new InconclusiveException("INCONCLUSIVE: fixture missed cadence: max scan gap 80 ms",
            new XunitException("budget"))));
        bus.QueueMessage(new TestFailed(test, 0, "", new XunitException("budget")));
        bus.QueueMessage(new TestPassed(test, 0, ""));

        bus.Skipped.Should().Be(1);
        inner.Messages.Should().HaveCount(3);
        inner.Messages[0].Should().BeAssignableTo<ITestSkipped>()
            .Which.Reason.Should().Be("INCONCLUSIVE: fixture missed cadence: max scan gap 80 ms");
        inner.Messages[1].Should().BeAssignableTo<ITestFailed>();
        inner.Messages[2].Should().BeAssignableTo<ITestPassed>();
    }

    [TimingFact(DisplayName = "GA-U-89 [TimingFact] runs as a fact")]
    public void TimingFact_Runs() => true.Should().BeTrue();

    private sealed class Recorder : IMessageBus
    {
        public List<IMessageSinkMessage> Messages { get; } = [];

        public bool QueueMessage(IMessageSinkMessage message)
        {
            Messages.Add(message);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
