using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-I-88 (#70, driver c3069ae): a failed TCP connect names host:port and the socket error, never a "unit 0" the
/// operator did not configure — in the checker's CHK-01 and in one-verb mode, with <c>--unit 7</c>.
/// </summary>
public sealed class ConnectMessageUnitTests
{
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
            .And.NotContain("unit 0", "a TCP connect carries no unit (#70)");
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
        text.Should().Contain($"enable: Transport/CommunicationLost: connect on 127.0.0.1:{port} failed twice (reconnected once): ")
            .And.NotContain("unit 0")
            .And.Contain($"on 127.0.0.1:{port} unit 7 (C = holding 0", "the header names the unit the operator configured");
    }
}
