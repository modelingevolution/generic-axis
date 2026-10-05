using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-I-88 (#70, driver c3069ae): a failed TCP connect names host:port and the socket error, never a "unit 0" the
/// operator did not configure — in the checker's CHK-01 and in one-verb mode, with <c>--unit 7</c>.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class ConnectMessageUnitTests
{
    /// <summary>
    /// The other half of #70: a FRAME carries the unit. A silent PLC (connection accepted, nothing answered) times out the
    /// first frame — the pre-flight read — and that message names "unit 7", in CHK-01 and in one-verb mode alike.
    /// </summary>
    [Fact]
    public async Task GA_I_88_ASilentPlcsFirstFrameTimeoutNamesTheConfiguredUnit()
    {
        using var sim = new LiveSimulator(new ModelingEvolution.GenericAxis.TestApp.Simulation.SimulatedAxisOptions
        {
            UnitId = 7, Faults = new ModelingEvolution.GenericAxis.TestApp.Simulation.SimFaults { Silent = true },
        });

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, Unit = 7 }, CancellationToken.None);
        var chk01 = report.Checks.Single(c => c.Id == "CHK-01").Message;
        chk01.Should().StartWith("Transport/CommunicationLost: pre-flight: Heartbeat, LeaseOwner, WatchdogFault (FC03 read C+8…C+10 = holding 8…10) ")
            .And.Contain($"on 127.0.0.1:{sim.Port} unit 7 failed twice (reconnected once): ")
            .And.NotContain("unit 0");

        var output = new StringWriter();
        var exit = await new CommandRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, Unit = 7, Command = new VerbRequest(Verb.Enable) }, output, CancellationToken.None);
        exit.Should().Be(1);
        output.ToString().Should().Contain($"enable: Transport/CommunicationLost: pre-flight: Heartbeat, LeaseOwner, WatchdogFault (FC03 read C+8…C+10 = holding 8…10) on 127.0.0.1:{sim.Port} unit 7 failed twice")
            .And.NotContain("unit 0");
    }

    /// <summary>A loopback port that was just free and is closed now: nothing listens, a connect is refused.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GA_I_88_Chk01OnAClosedPortNamesTheEndpointNotUnit0()
    {
        var port = ClosedPort();

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = port, Unit = 7 }, CancellationToken.None);

        var chk01 = report.Checks.Single(c => c.Id == "CHK-01");
        chk01.ErrorClass.Should().Be(ErrorClass.Transport);
        chk01.Message.Should().StartWith($"Transport/CommunicationLost: connect on 127.0.0.1:{port} failed twice (reconnected once): ")
            .And.NotContain("unit", "a TCP connect carries no unit (#70): its message names host:port only");
        report.ExitCode.Should().Be(1);
    }

    [Fact]
    public async Task GA_I_88_OneVerbModeOnAClosedPortNamesTheEndpointNotUnit0()
    {
        var port = ClosedPort();
        var output = new StringWriter();

        var exit = await new CommandRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = port, Unit = 7, Command = new VerbRequest(Verb.Enable) }, output, CancellationToken.None);

        exit.Should().Be(1);
        var text = output.ToString();
        var error = text.Split('\n').Single(l => l.StartsWith("enable: ", StringComparison.Ordinal));
        error.Should().StartWith($"enable: Transport/CommunicationLost: connect on 127.0.0.1:{port} failed twice (reconnected once): ")
            .And.NotContain("unit", "a TCP connect carries no unit (#70)");
        text.Should().Contain($"on 127.0.0.1:{port} unit 7 (C = holding 0", "the header names the unit the operator configured");
    }
}
