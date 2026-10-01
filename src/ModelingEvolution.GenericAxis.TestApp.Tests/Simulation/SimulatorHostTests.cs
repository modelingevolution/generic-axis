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
    public async Task AWriteToHoldingC11IsIgnored()
    {
        using var sim = new LiveSimulator();
        using var client = Connect(sim.Port);

        client.WriteSingleRegister(1, 11, 99);
        var trips = client.ReadHoldingRegisters<ushort>(1, 11, 1)[0];

        trips.Should().Be(0, "WatchdogTrips (C+11 = holding 11) is PLC-owned");
        (await sim.SettledAsync()).WatchdogTrips.Should().Be(0);
    }

    /// <summary>
    /// GA-I-68 (ADR-36): at the default bases the command block (holding 0…11) and the status block (input 0…14) share
    /// addresses but not data. A holding write to 0…14 lands in the command block, where the PLC acts on it, and the
    /// status block read by FC04 still carries what the PLC published.
    /// </summary>
    [Fact]
    public async Task GA_I_68_HoldingAndInputAreIndependentSpacesAtTheDefaultBases()
    {
        using var sim = new LiveSimulator();
        sim.Host.Options.CommandBase.Should().Be(0);
        sim.Host.Options.StatusBase.Should().Be(0);
        using var client = Connect(sim.Port);

        // Command 0, CommandSeq 5, Target 0x5678_1234, Velocity 0, Acceleration hi 0x4242 at holding 7 (S+7 is
        // CommandAck), Heartbeat 0x0A0A, LeaseOwner 0, WatchdogFault 0, C+11 99 (ignored), holding 12…14 beyond the
        // command block (holding 14 = 2 where input 14 is MapVersion).
        ushort[] written = [0, 5, 0x1234, 0x5678, 0, 0, 0, 0x4242, 0x0A0A, 0, 0, 99, 0xAAAA, 0xBBBB, 2];
        client.WriteMultipleRegisters(1, 0, written);
        var settled = await sim.SettledAsync();

        var holding = client.ReadHoldingRegisters<ushort>(1, 0, 15).ToArray();
        var input = client.ReadInputRegisters<ushort>(1, 0, 15).ToArray();

        holding.Should().Equal([0, 5, 0x1234, 0x5678, 0, 0, 0, 0x4242, 0x0A0A, 0, 0, 0, 0xAAAA, 0xBBBB, 2],
            "every holding word reads back as written, except C+11 which the PLC owns");
        settled.CommandBlock.Should().Equal(holding[..12], "the PLC's command block is the holding array");
        settled.CommandSeq.Should().Be(5);
        input[7].Should().Be(5, "the PLC accepted CommandSeq 5 from holding 1 and published the ack in input 7 (S+7)");
        input[0].Should().Be((ushort)SimAxisState.Disabled, "Command 0 is Enable 0");
        input[14].Should().Be(1, "MapVersion (S+14 = input 14) is the PLC's, not holding 14");
        input[2..4].Should().Equal([0xA120, 0x0007], "ActualPosition 500.000 low word first, not the written target");
        input[8..14].Should().Equal([0, 0, 0x9680, 0x0098, 0xA120, 0x0007],
            "the limits 0 / 10 000 / 500 the PLC publishes, untouched by holding 8…13");
        settled.StatusBlock.Should().Equal(input, "the PLC's status block is the input array");
    }

    /// <summary>ADR-37 fixture: <see cref="SimFaults.RefuseInputRegisters"/> answers every FC04 with exception 02 and
    /// leaves holding registers served.</summary>
    [Fact]
    public void RefuseInputRegistersAnswersFc04WithException02()
    {
        using var sim = new LiveSimulator();
        using var client = Connect(sim.Port);
        client.ReadInputRegisters<ushort>(1, 0, 15).ToArray().Should().HaveCount(15, "served before the fault");

        sim.Host.Faults = new SimFaults { RefuseInputRegisters = true };
        var fc04 = () => client.ReadInputRegisters<ushort>(1, 0, 15).ToArray();

        fc04.Should().Throw<FluentModbus.ModbusException>().Which.ExceptionCode.Should().Be(FluentModbus.ModbusExceptionCode.IllegalDataAddress);
        client.ReadHoldingRegisters<ushort>(1, 0, 12).ToArray().Should().HaveCount(12, "holding registers still answer");
        sim.Host.Faults = SimFaults.None;
        fc04.Should().NotThrow("clearing the fault serves input registers again");
    }

    private static FluentModbus.ModbusTcpClient Connect(int port)
    {
        var client = new FluentModbus.ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, port), FluentModbus.ModbusEndianness.BigEndian);
        return client;
    }
}
