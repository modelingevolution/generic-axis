using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// #70 (ADR d8665218): a TCP connect carries no unit, so its messages name host:port only. The unit appears only in a
/// message about a frame that carried it — the configured unit, never a default 0.
/// </summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class ConnectMessageTests
{
    private const byte ConfiguredUnit = 7;

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact(DisplayName = "GA-I-31 A refused TCP connect names host:port and the socket error, never a unit")]
    public async Task Connect_ClosedPort_MessageNamesEndpointNotUnit()
    {
        await using var rig = new LiveRig();
        var port = ClosedPort();
        var track = rig.Track(o => o with { Port = port, UnitId = ConfiguredUnit });

        var ex = (await track.Invoking(t => t.ConnectAsync().WaitAsync(LiveRig.T))
            .Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().Be(
            $"carriage: CommunicationLost: connect on 127.0.0.1:{port} failed twice (reconnected once): Connection refused.");
        ex.Message.Should().NotContain("unit", "a TCP connect carries no unit");
        var warning = rig.Logs.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Warning
            && r.Message.Contains("reconnecting and retrying once")).Which;
        warning.Message.Should().Contain($"connect on 127.0.0.1:{port} failed (Connection refused)")
            .And.NotContain("unit");
    }

    [Fact(DisplayName = "GA-I-32 A failed frame names the configured unit (the connect succeeded, the frame got no answer)")]
    public async Task Frame_SilentPlc_MessageNamesConfiguredUnit()
    {
        await using var rig = new LiveRig();
        rig.Plc.SetSilent(true);
        var track = rig.Track(o => o with { UnitId = ConfiguredUnit });

        var ex = (await track.Invoking(t => t.ConnectAsync().WaitAsync(LiveRig.T))
            .Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.CommunicationLost);
        // The attach's first frame is the FC03 read of C+9…C+11 (then FC04 of the status block).
        ex.Message.Should().Be("carriage: CommunicationLost: read lease and watchdog (FC03 read C+9…C+11 = holding 9…11) "
                               + $"on 127.0.0.1:{rig.Plc.Port} unit {ConfiguredUnit} failed twice (reconnected once): "
                               + "timed out: no complete response within 500 ms.");
        rig.Logs.GetSnapshot().Should().NotContain(r => r.Message.Contains("unit 0"));
    }
}
