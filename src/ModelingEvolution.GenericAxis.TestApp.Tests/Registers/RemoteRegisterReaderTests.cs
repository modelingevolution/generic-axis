using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Registers;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Registers;

/// <summary>GA-I-42 (review #20): <c>/registers</c> pointed at any host:port/unit reads that PLC at 5 Hz and writes nothing.</summary>
[Collection(LiveCollection.Name)]
public sealed class RemoteRegisterReaderTests
{
    [Fact]
    public async Task GA_I_42_PointedAtASecondSimulator_ShowsItsBlocksAndWritesNothing()
    {
        using var inApp = new LiveSimulator();
        using var second = new LiveSimulator(new SimulatedAxisOptions { TravelMax = 8_000, CommandBase = 200, StatusBase = 300 });
        var before = await second.SettledAsync();

        await using var reader = new RemoteRegisterReader(new RegisterEndpoint("127.0.0.1", second.Port, 1, 200, 300), NullLoggerFactory.Instance);
        var sw = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(2));
        var reads = reader.Reads;
        var reading = reader.Latest;
        var elapsed = sw.Elapsed;

        reads.Should().BeGreaterThanOrEqualTo((long)(elapsed / RemoteRegisterReader.Period) - 1, "it reads at 5 Hz");
        reading!.Error.Should().BeNull();
        var rows = reading.Rows.ToDictionary(r => r.Name);
        rows["TravelMax"].Address.Should().Be("S+10…S+11 = 310…311", "the second simulator's bases are used");
        rows["TravelMax"].Value.Should().StartWith("8000.000", "the second simulator's limits, not the in-app one's 10000");
        rows["MapVersion"].Value.Should().Be("1");

        var after = await second.SettledAsync();
        after.CommandBlock.Should().Equal(before.CommandBlock, "read-only: no command, no lease, no beat");
        after.LeaseOwner.Should().Be(0);
        inApp.Snapshot.CommandBlock.Should().OnlyContain(w => w == 0, "the in-app simulator is not touched either");
    }

    [Fact]
    public async Task GA_I_42_AClosedPort_ShowsTheTransportErrorInTheProtocolShape()
    {
        await using var reader = new RemoteRegisterReader(new RegisterEndpoint("127.0.0.1", 1, 1, 0, 100), NullLoggerFactory.Instance);
        var sw = Stopwatch.StartNew();
        while (reader.Latest is null && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);

        reader.Latest!.Error.Should().StartWith("Transport/CommunicationLost: read C+0…C+11 (/registers)")
            .And.Contain("on 127.0.0.1:1 unit 1 failed twice");
        reader.Reads.Should().Be(0);
    }
}
