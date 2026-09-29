using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>
/// GA-I-50 (review #30): a client that dies with a request in flight must not take the simulated PLC off the network.
/// A killed rw2, a Ctrl-C'd checker or a channel reset after a timeout all do exactly this.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class SimulatorResilienceTests
{
    private static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ClientResetMidRequest_SimulatorKeepsAnsweringNewAndExistingClients()
    {
        // Results are captured first and asserted after the simulator is disposed, so a failing Dispose (the
        // synchronous server rethrows its dead processing task there) cannot mask which read went unanswered.
        string before, afterNew, afterExisting;
        TimeSpan newTook, existingTook;
        Exception? disposeFailure = null;
        var sim = new LiveSimulator();
        try
        {
            using var existing = Connect(sim.Port);
            before = TryReadStatus(existing, out _);

            for (var i = 0; i < 5; i++) await ResetWithRequestInFlightAsync(sim.Port);

            using var fresh = Connect(sim.Port);
            afterNew = TryReadStatus(fresh, out newTook);
            afterExisting = TryReadStatus(existing, out existingTook);
        }
        finally
        {
            try { sim.Dispose(); }
            catch (Exception ex) { disposeFailure = ex; }
        }

        before.Should().Be("answered", "the existing client answers before the fault");
        afterNew.Should().Be("answered", "a new client is answered after peers reset mid-request");
        newTook.Should().BeLessThan(AnswerBudget);
        afterExisting.Should().Be("answered", "a client connected before the fault is still answered");
        existingTook.Should().BeLessThan(AnswerBudget);
        disposeFailure.Should().BeNull("no server task died on the dead peers");
    }

    [Fact]
    public async Task CommunicationDownCleared_ModbusAnswersAgain_ThreeCycles()
    {
        using var sim = new LiveSimulator();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            using (var before = Connect(sim.Port)) ReadStatus(before).Should().HaveCount(15);

            sim.Host.Faults = new SimFaults { CommunicationDown = true };
            await Task.Delay(50);
            sim.Host.Faults = SimFaults.None;

            var sw = Stopwatch.StartNew();
            using var after = Connect(sim.Port);
            ReadStatus(after).Should().HaveCount(15, $"cycle {cycle + 1}: a Modbus read answers after the gate re-opens");
            sw.Elapsed.Should().BeLessThan(AnswerBudget);
        }
    }

    /// <summary>One FC03 of S+0…S+14, then an RST (linger 0) before the answer can be read.</summary>
    private static async Task ResetWithRequestInFlightAsync(int port)
    {
        using var raw = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await raw.ConnectAsync(IPAddress.Loopback, port);
        byte[] fc03 = [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 100, 0x00, 15];
        await raw.SendAsync(fc03);
        raw.LingerState = new LingerOption(true, 0);
        raw.Close();
        await Task.Delay(30); // at least one scan, so a synchronous server would try to answer the dead peer
    }

    private static FluentModbus.ModbusTcpClient Connect(int port)
    {
        var client = new FluentModbus.ModbusTcpClient
        {
            ConnectTimeout = (int)AnswerBudget.TotalMilliseconds,
            ReadTimeout = (int)AnswerBudget.TotalMilliseconds,
            WriteTimeout = (int)AnswerBudget.TotalMilliseconds,
        };
        client.Connect(new IPEndPoint(IPAddress.Loopback, port), FluentModbus.ModbusEndianness.BigEndian);
        return client;
    }

    private static string TryReadStatus(FluentModbus.ModbusTcpClient client, out TimeSpan took)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var words = ReadStatus(client);
            took = sw.Elapsed;
            return words.Length == 15 ? "answered" : $"answered {words.Length} words";
        }
        catch (Exception ex)
        {
            took = sw.Elapsed;
            return $"no answer after {took.TotalMilliseconds:F0} ms: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static ushort[] ReadStatus(FluentModbus.ModbusTcpClient client) =>
        client.ReadHoldingRegisters<ushort>(1, 100, 15).ToArray();
}
