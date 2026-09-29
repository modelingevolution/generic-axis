using System.Net;
using System.Net.Sockets;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>The simulator on the wire: port ownership, the comms-drop gate, PLC-owned registers.</summary>
[Collection(LiveCollection.Name)]
public sealed class SimulatorHostTests
{
    [Fact]
    public void ASecondSimulatorCannotShareThePort()
    {
        using var first = new LiveSimulator();
        using var second = new SimulatorHost(new SimulatedAxisOptions { Port = first.Port }, bindAddress: IPAddress.Loopback);

        var act = second.Start;

        act.Should().Throw<SocketException>("two PLCs behind one port would split the clients between them");
    }

    [Fact]
    public async Task CommunicationDownDropsClientsAndRestoringReopensTheSamePort()
    {
        using var sim = new LiveSimulator();
        var port = sim.Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        sim.Host.Faults = new SimFaults { CommunicationDown = true };
        sim.Host.IsListening.Should().BeFalse();
        var refused = async () =>
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
        };
        await refused.Should().ThrowAsync<SocketException>();

        sim.Host.Faults = SimFaults.None;
        sim.Host.IsListening.Should().BeTrue();
        sim.Port.Should().Be(port);
        using var again = new TcpClient();
        await again.ConnectAsync(IPAddress.Loopback, port);
        again.Connected.Should().BeTrue();
    }

    [Fact]
    public async Task WritesToPlcOwnedRegistersAreIgnored()
    {
        using var sim = new LiveSimulator();
        using var client = new FluentModbus.ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, sim.Port), FluentModbus.ModbusEndianness.BigEndian);

        client.WriteMultipleRegisters(1, 100, new ushort[] { 7, 0xFFFF });
        client.WriteSingleRegister(1, 11, 99);
        var status = client.ReadHoldingRegisters<ushort>(1, 100, 15).ToArray();
        var trips = client.ReadHoldingRegisters<ushort>(1, 11, 1)[0];

        status[0].Should().Be(0, "State is PLC-owned");
        status[14].Should().Be(1, "MapVersion is PLC-owned");
        trips.Should().Be(0, "WatchdogTrips is PLC-owned");
        (await sim.SettledAsync()).State.Should().Be(SimAxisState.Disabled);
    }
}
