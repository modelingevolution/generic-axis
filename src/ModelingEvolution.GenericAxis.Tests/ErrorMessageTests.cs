using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Mm = ModelingEvolution.Drawing.Units.Length<double, ModelingEvolution.Drawing.Units.Millimetre<double>>;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — messages and the register dump (GA-U-67, GA-U-69). GA-U-66 (the class map) is
/// <see cref="ErrorClassTests"/>.</summary>
public class ErrorMessageTests
{
    private static readonly Regex Shape = new(
        @"^carriage: [A-Za-z]+: .+\. Read [A-Za-z]+ \((C\+[0-9]+ = holding|S\+[0-9]+ = input) [0-9]+\) = [^,]+",
        RegexOptions.Compiled);

    [Fact(DisplayName = "GA-U-67 Messages state what was seen (Machine, Commander, Protocol)")]
    public async Task Messages_OneExamplePerClass_ProtocolShape()
    {
        var machine = AxisEngine.FaultException("carriage", RegisterMap.Default,
            new PlcSnapshot(new StatusBlock(7, StatusFlags.Homed, 0, 0, 4, 0, 0, 0, 0, 1), 1, 1, 3, 0));
        machine.Message.Should().MatchRegex(Shape.ToString())
            .And.EndWith("Read FaultCode (S+6 = input 6) = 4, WatchdogFault (C+10 = holding 10) = 1, "
                         + "WatchdogTrips (C+11 = holding 11) = 3.", "protocol § Errors and debugging, example 3");

        await using var rig = await new DriverRig().ConnectAsync();
        var commander = (await DriverRig.Bounded(() => rig.Linear.MoveAbsoluteAsync(new Mm(10_500)))
            .Should().ThrowAsync<MotionException>()).Which;
        commander.Message.Should().MatchRegex(Shape.ToString())
            .And.Contain("Read TravelMin (S+8 = input 8) = 0, TravelMax (S+10 = input 10) = 10000000, MaxVelocity (S+12 = input 12) = 500000.");

        var protocol = rig.Axis.HomeAsync();
        for (var i = 0; i < 8 && !protocol.IsCompleted; i++) await rig.TickAsync();
        var notAck = (await protocol.Invoking(p => p).Should().ThrowAsync<MotionException>()).Which;
        notAck.Message.Should().StartWith("carriage: NotAcknowledged: ")
            .And.MatchRegex("CommandSeq [0-9]+ written, CommandAck [0-9]+ read after 500 ms, State [0-9]+ read\\.$");

        foreach (var m in new[] { machine.Message, commander.Message, notAck.Message })
            m.Should().NotContainEquivalentOf("communication error");
    }

    [Fact(DisplayName = "GA-U-69 The register dump decodes names and units")]
    public void Decode_ProtocolVector_NamesUnitsAndRawHex()
    {
        ushort[] command = [1, 7, 0, 0, 0, 0, 0, 0, 12, 65535, 0, 2];
        ushort[] status = [0, 32, 0, 0, 0, 0, 0, 6, 0, 0, 38528, 152, 41248, 7, 1];

        var rows = RegisterDump.Decode(RegisterMap.Default, command, status);
        var byName = rows.ToDictionary(r => r.Name);

        rows.Should().HaveCount(19, "one row per protocol field: 9 in the command block, 10 in the status block");
        byName["Command"].Should().Be(new RegisterRow("C+0 = holding 0", "Command", "0x0001", "Enable", RegisterSpace.Holding, 0));
        byName["CommandSeq"].Value.Should().Be("7");
        byName["LeaseOwner"].Should().Be(new RegisterRow("C+9 = holding 9", "LeaseOwner", "0xFFFF", "65535", RegisterSpace.Holding, 9));
        byName["WatchdogTrips"].Value.Should().Be("2");
        byName["State"].Should().Be(new RegisterRow("S+0 = input 0", "State", "0x0000", "Disabled", RegisterSpace.Input, 0));
        byName["Flags"].Value.Should().Be("DriveReady");
        byName["CommandAck"].Value.Should().Be("6");
        byName["TravelMax"].Should().Be(new RegisterRow("S+10…S+11 = input 10…11", "TravelMax", "0x9680 0x0098", "10000.000", RegisterSpace.Input, 10));
        byName["MaxVelocity"].Value.Should().Be("500.000");
        byName["MapVersion"].Value.Should().Be("1");
        byName["FaultCode"].Value.Should().Be("None");

        var text = RegisterDump.Format(rows);
        text.Should().Contain("Command").And.Contain("0xA120 0x0007").And.Contain("10000.000");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(20, "a header and one line per row");
    }

    [Fact(DisplayName = "GA-U-69 names for faults, undefined states and combined bits")]
    public void Decode_NamesEveryEncoding()
    {
        ushort[] command = [(ushort)(CommandBits.Enable | CommandBits.Stop), 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0];
        ushort[] status = [42, (ushort)(StatusFlags.Homed | StatusFlags.LimitMax), 0xFFFF, 0xFFFF, 0, 0, 150, 0, 0, 0, 0, 0, 0, 0, 1];

        var byName = RegisterDump.Decode(new RegisterMap(200, 300), command, status).ToDictionary(r => r.Name);

        byName["Command"].Value.Should().Be("Enable | Stop");
        byName["Command"].Address.Should().Be("C+0 = holding 200");
        byName["WatchdogFault"].Value.Should().Be("1 (tripped)");
        byName["State"].Value.Should().Be("42 (undefined)");
        byName["Flags"].Value.Should().Be("Homed | LimitMax");
        byName["ActualPosition"].Value.Should().Be("-0.001");
        byName["FaultCode"].Value.Should().Be("150 (vendor)");
        byName["MapVersion"].Address.Should().Be("S+14 = input 314");
    }

    [Fact(DisplayName = "GA-U-140 Every dump row carries its block's space and absolute address (ADR-36)")]
    public void Decode_EveryRow_SpaceAndAbsoluteAddressFromItsBlock()
    {
        ushort[] command = new ushort[RegisterMap.CommandLength];
        ushort[] status = new ushort[RegisterMap.StatusLength];
        status[14] = 1;

        foreach (var map in new[] { RegisterMap.Default, new RegisterMap(200, 300) })
        {
            var rows = RegisterDump.Decode(map, command, status);
            var fields = typeof(RegisterField).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(f => (RegisterField)f.GetValue(null)!).ToDictionary(f => f.Name);
            rows.Select(r => r.Name).Should().BeEquivalentTo(fields.Keys, "one row per declared field");
            foreach (var row in rows)
            {
                var field = fields[row.Name];
                row.Space.Should().Be(field.Space, row.Name);
                row.AbsoluteAddress.Should().Be(map.Address(field), row.Name);
                row.Address.Should().StartWith(map.Describe(field)[..map.Describe(field).IndexOf(' ')], row.Name)
                    .And.Contain(field.Space == RegisterSpace.Holding ? "= holding " : "= input ", row.Name);
            }

            rows.Where(r => r.Space == RegisterSpace.Holding).Should().HaveCount(9);
            rows.Where(r => r.Space == RegisterSpace.Input).Should().HaveCount(10);
        }

        var zero = RegisterDump.Decode(RegisterMap.Default, command, status).ToDictionary(r => r.Name);
        zero["Acceleration"].Address.Should().Be("C+6…C+7 = holding 6…7");
        zero["FaultCode"].Address.Should().Be("S+6 = input 6", "same absolute address, other space");
        zero["FaultCode"].AbsoluteAddress.Should().Be(zero["Acceleration"].AbsoluteAddress);
    }
}

/// <summary>GA-U-68 — No silent retry. It needs a real socket to fail, so it runs with the live tests.</summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class ChannelRetryTests
{
    private static (ModbusChannel Channel, FakeLogCollector Logs, ILoggerFactory Factory) Channel(MiniPlc plc)
    {
        var logs = new FakeLogCollector();
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(logs)));
        var channel = new ModbusChannel("127.0.0.1", plc.Port, factory.CreateLogger("channel"), null, "carriage",
            RegisterMap.Default);
        return (channel, logs, factory);
    }

    [Fact(DisplayName = "GA-U-68 No silent retry: one failure is one Warning and exactly two attempts")]
    public async Task Read_FirstAttemptFails_OneWarningTwoAttempts()
    {
        await using var plc = new MiniPlc();
        var (channel, logs, factory) = Channel(plc);
        using var _ = factory;
        using var __ = channel;
        plc.DropNextConnections(1);

        var words = await channel.ReadInputAsync(1, RegisterMap.Default.Status, RegisterMap.StatusLength, "read status block", ChannelPriority.Move)
            .WaitAsync(LiveRig.T);

        words.Should().HaveCount(15);
        words[14].Should().Be(1, "anchor: the second attempt read the real block (MapVersion)");
        plc.AcceptedConnections.Should().Be(2, "exactly two attempts");
        channel.Retries.Should().Be(1, "review #25: the one retry is counted for the checker's `retries`");
        var warnings = logs.GetSnapshot().Where(r => r.Level == LogLevel.Warning).ToArray();
        warnings.Should().ContainSingle().Which.Exception.Should().NotBeNull("the retry is logged with the exception");
        warnings[0].Message.Should().Contain("read status block (FC04 read S+0…S+14 = input 0…14)")
            .And.Contain($"127.0.0.1:{plc.Port} unit 1");
    }

    [Fact(DisplayName = "GA-U-68 two failures are CommunicationLost after exactly two attempts")]
    public async Task Read_BothAttemptsFail_CommunicationLostNamesEverything()
    {
        await using var plc = new MiniPlc();
        var (channel, logs, factory) = Channel(plc);
        using var _ = factory;
        using var __ = channel;
        plc.DropNextConnections(2);

        var ex = (await channel.Invoking(c => c.ReadInputAsync(1, RegisterMap.Default.Status, RegisterMap.StatusLength, "read status block").WaitAsync(LiveRig.T))
            .Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().StartWith("carriage: CommunicationLost: read status block (FC04 read S+0…S+14 = input 0…14) "
                                      + $"on 127.0.0.1:{plc.Port} unit 1 failed twice (reconnected once): ");
        ex.Message.Should().NotEndWith("(reconnected once): .", "the exception's own message is quoted");
        plc.AcceptedConnections.Should().Be(2);
        logs.GetSnapshot().Count(r => r.Level == LogLevel.Warning).Should().Be(1);
        channel.Retries.Should().Be(1, "review #25: the second failure is not a second retry");
    }

    [Fact(DisplayName = "GA-U-68 a refused connection is CommunicationLost naming the endpoint")]
    public async Task Connect_ClosedPort_CommunicationLost()
    {
        int port;
        await using (var plc = new MiniPlc()) port = plc.Port;
        using var channel = new ModbusChannel("127.0.0.1", port, null, null, "carriage", RegisterMap.Default);

        var ex = (await channel.Invoking(c => c.ConnectAsync(CancellationToken.None).WaitAsync(LiveRig.T))
            .Should().ThrowAsync<MotionException>()).Which;

        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().StartWith($"carriage: CommunicationLost: connect on 127.0.0.1:{port} unit 0 failed twice");
    }
}
