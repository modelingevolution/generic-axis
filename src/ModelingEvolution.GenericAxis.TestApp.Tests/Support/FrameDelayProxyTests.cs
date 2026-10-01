using System.Net;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Support;

[Collection(LiveCollection.Name)]
public sealed class FrameDelayProxyTests
{
    [Fact]
    public void ForwardsReadsAndWrites()
    {
        using var sim = new LiveSimulator();
        using var proxy = new FrameDelayProxy(sim.Port);
        using var client = new FluentModbus.ModbusTcpClient { ReadTimeout = 2000 };
        client.Connect(new IPEndPoint(IPAddress.Loopback, proxy.Port), FluentModbus.ModbusEndianness.BigEndian);
        for (var i = 0; i < 5; i++) client.ReadInputRegisters<ushort>(1, 0, 15).ToArray().Should().HaveCount(15);
        client.WriteSingleRegister(1, 8, 7);
        client.ReadHoldingRegisters<ushort>(1, 8, 1)[0].Should().Be(7);
    }
}
