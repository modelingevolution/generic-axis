using ModelingEvolution.GenericAxis.TestApp.Simulation;
using ModelingEvolution.GenericAxis.TestApp.Tests.Support;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>
/// GA-U-97 — the simulator-cadence INCONCLUSIVE mechanism (lead ruling "Two-vCPU flakes", copied from the driver tests):
/// the loop's gap recorder, the budget rule (red unless the simulator missed cadence) and the skip the runner reports.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class CadenceTests
{
    [Fact]
    public async Task GA_U_97_SimulatorLoopRecordsTheLongestScanGapUntilReset()
    {
        using var sim = new LiveSimulator();
        using var client = Connect(sim.Port);
        await Task.Delay(50);
        sim.ResetMaxScanGap();

        // A served write holds the scan lock: the stalled scan delays the next one.
        sim.Host.OnClientWrite = _ => Thread.Sleep(200);
        client.WriteSingleRegister(1, 2, 1);
        sim.Host.OnClientWrite = null;
        await Task.Delay(50);
        sim.MaxScanGap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "the stalled scan delayed the next one");

        sim.ResetMaxScanGap();
        await Task.Delay(300); // longer than the bar: only scans recorded as they complete keep the gap small
        sim.MaxScanGap.Should().BeLessThan(TimeSpan.FromMilliseconds(150), "the reset forgot the stall, and normal scans are 10 ms apart");

        using var open = new ManualResetEventSlim();
        sim.Host.OnClientWrite = _ => open.Wait(TimeSpan.FromSeconds(5));
        var stuck = Task.Run(() => client.WriteSingleRegister(1, 2, 2));
        await Task.Delay(250);
        sim.MaxScanGap.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150), "a scan stuck right now counts");
        open.Set();
        await stuck;
        sim.Host.OnClientWrite = null;
    }

    [Fact]
    public void GA_U_97_ABudgetFailureIsRedOnCadenceAndInconclusiveOnlyWhenTheSimulatorMissedIt()
    {
        Action fails = () => 1.2.Should().BeLessThanOrEqualTo(1.05);

        FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromMilliseconds(12), fails)).Should().Throw<XunitException>();
        FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromMilliseconds(50), fails)).Should().Throw<XunitException>();

        var inconclusive = FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromMilliseconds(51), fails)).Should().Throw<InconclusiveException>().Which;
        inconclusive.Message.Should().Contain("fixture missed cadence: max scan gap 51 ms").And.Contain("1.05");

        FluentActions.Invoking(() => Cadence.Budget(TimeSpan.FromSeconds(5), () => 1.0.Should().BeLessThanOrEqualTo(1.05))).Should().NotThrow();
    }

    [Fact]
    public void GA_U_97_TimingFactReportsInconclusiveAsSkippedAndOtherFailuresAsFailed()
    {
        var inner = new Recorder();
        using var bus = new TimingTestCase.InconclusiveBus(inner);
        var method = new TestMethod(
            new TestClass(new TestCollection(new TestAssembly(Reflector.Wrap(typeof(CadenceTests).Assembly)), null, "cadence"),
                Reflector.Wrap(typeof(CadenceTests))),
            Reflector.Wrap(typeof(CadenceTests).GetMethod(nameof(GA_U_97_TimingFactRuns))!));
        var test = new XunitTest(new XunitTestCase(new NullMessageSink(), TestMethodDisplay.Method, TestMethodDisplayOptions.None, method), "stub");

        bus.QueueMessage(new TestFailed(test, 0, "", new InconclusiveException("INCONCLUSIVE: fixture missed cadence: max scan gap 80 ms", new XunitException("budget"))));
        bus.QueueMessage(new TestFailed(test, 0, "", new XunitException("budget")));

        bus.Skipped.Should().Be(1);
        inner.Messages[0].Should().BeAssignableTo<ITestSkipped>().Which.Reason.Should().Be("INCONCLUSIVE: fixture missed cadence: max scan gap 80 ms");
        inner.Messages[1].Should().BeAssignableTo<ITestFailed>();
    }

    [TimingFact]
    public void GA_U_97_TimingFactRuns() => true.Should().BeTrue();

    private static FluentModbus.ModbusTcpClient Connect(int port)
    {
        var client = new FluentModbus.ModbusTcpClient { ReadTimeout = 10_000, WriteTimeout = 10_000 };
        client.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), FluentModbus.ModbusEndianness.BigEndian);
        return client;
    }

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
