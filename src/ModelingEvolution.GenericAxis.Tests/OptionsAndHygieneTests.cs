using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.Drawing.Units;
using ModelingEvolution.GenericAxis.Tests.Support;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — GA-U-41 (options), GA-U-56 and GA-U-57 (contract hygiene).</summary>
public class OptionsAndHygieneTests
{
    private static readonly GenericAxisOptions Valid = new() { Name = "carriage", Host = "192.168.58.20" };

    public static TheoryData<string, Func<GenericAxisOptions, GenericAxisOptions>> Invalid => new()
    {
        { nameof(GenericAxisOptions.Name), o => o with { Name = "" } },
        { nameof(GenericAxisOptions.Host), o => o with { Host = "" } },
        { nameof(GenericAxisOptions.Port), o => o with { Port = 0 } },
        { nameof(GenericAxisOptions.Port), o => o with { Port = 65536 } },
        { nameof(GenericAxisOptions.UnitId), o => o with { UnitId = 256 } },
        { nameof(GenericAxisOptions.Map), o => o with { Map = new RegisterMap(0, 65_530) } },
        { nameof(GenericAxisOptions.HeartbeatInterval), o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(250) } },
        { nameof(GenericAxisOptions.HeartbeatInterval), o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(10) } },
        { nameof(GenericAxisOptions.LeaseTimeout), o => o with { LeaseTimeout = TimeSpan.FromSeconds(-1) } },
        { nameof(GenericAxisOptions.ConfiguredTravelMin), o => o with { ConfiguredTravelMin = 10, ConfiguredTravelMax = 10 } },
        { nameof(GenericAxisOptions.ConfiguredMaxVelocity), o => o with { ConfiguredMaxVelocity = 0 } },
        { nameof(GenericAxisOptions.ReadMin), o => o with { ReadMin = 5, ConfiguredTravelMin = 0 } },
        { nameof(GenericAxisOptions.ReadMax), o => o with { ReadMax = 90, ConfiguredTravelMax = 100 } },
        { nameof(GenericAxisOptions.DefaultSpeed), o => o with { DefaultSpeed = new Percentage(0f) } },
        { nameof(GenericAxisOptions.Acceleration), o => o with { Acceleration = 0 } },
        { nameof(GenericAxisOptions.Tolerance), o => o with { Tolerance = 0 } },
        { nameof(GenericAxisOptions.EnableTimeout), o => o with { EnableTimeout = TimeSpan.Zero } },
        { nameof(GenericAxisOptions.StopTimeout), o => o with { StopTimeout = TimeSpan.Zero } },
        { nameof(GenericAxisOptions.HomingTimeout), o => o with { HomingTimeout = TimeSpan.Zero } },
        { nameof(GenericAxisOptions.MoveTimeoutMargin), o => o with { MoveTimeoutMargin = TimeSpan.Zero } },
    };

    [Theory(DisplayName = "GA-U-41 Options validate")]
    [MemberData(nameof(Invalid))]
    public void Validate_InvalidValue_ArgumentExceptionNamesField(string field, Func<GenericAxisOptions, GenericAxisOptions> spoil)
    {
        Valid.Invoking(o => o.Validate()).Should().NotThrow("anchor: the baseline is valid");
        spoil(Valid).Invoking(o => o.Validate()).Should().Throw<ArgumentException>().Which.ParamName.Should().Be(field);
    }

    [Fact(DisplayName = "GA-U-41 the defaults are the protocol's and the design's")]
    public void Defaults_AreNamedSources()
    {
        Valid.Port.Should().Be(502);
        Valid.UnitId.Should().Be(1);
        Valid.Map.Should().Be(new RegisterMap(0, 0));
        Valid.HeartbeatInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        Valid.LeaseTimeout.Should().BeNull();
        Valid.ConfiguredTravelMin.Should().BeNull("a machine limit is never defaulted");
        Valid.ConfiguredTravelMax.Should().BeNull();
        Valid.ConfiguredMaxVelocity.Should().BeNull();
        Valid.DefaultSpeed.Value.Should().Be(100f);
        Valid.Acceleration.Should().BeNull();
        Valid.Tolerance.Should().Be(0.001);
        Valid.EnableTimeout.Should().Be(TimeSpan.FromSeconds(5));
        Valid.StopTimeout.Should().Be(TimeSpan.FromSeconds(5));
        Valid.HomingTimeout.Should().Be(TimeSpan.FromSeconds(120));
        Valid.MoveTimeoutMargin.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact(DisplayName = "GA-U-56 The driver depends on the contract and FluentModbus only (NFR-1)")]
    public void DriverAssembly_References_SubsetOfAllowed()
    {
        string[] allowed =
        [
            "RocketWelder.SDK.Devices.Motion", "RocketWelder.SDK.Abstractions", "RocketWelder.SDK.Automation.Abstractions",
            "ModelingEvolution.Drawing", "ModelingEvolution.Signals", "FluentModbus",
            "Microsoft.Extensions.Logging.Abstractions",
        ];
        var names = typeof(ModbusLinearTrack).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        names.Should().Contain("FluentModbus", "control: the query sees the driver's references");
        names.Should().Contain("RocketWelder.SDK.Devices.Motion");
        names.Where(n => !(n == "netstandard" || n == "mscorlib" || n.StartsWith("System", StringComparison.Ordinal)))
            .Should().BeSubsetOf(allowed);
        names.Should().NotContain(n => n.Contains("Fairino", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "GA-U-57 Every state transition is logged at Information (NFR-3)")]
    public async Task Ticks_StateTransitions_InformationWithOldNewAndFault_HeartbeatTraceOnly()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.Logs.Clear();

        rig.Plc.State = 3;
        await rig.TickAsync();
        rig.Plc.State = 1;
        await rig.TickAsync();
        rig.Plc.Set(p => { p.State = 7; p.FaultCode = 4; });
        await rig.TickAsync();

        var info = rig.LogsAt(LogLevel.Information);
        info.Select(r => r.Message).Should().SatisfyRespectively(
            m => m.Should().Contain("Standstill → DiscreteMotion"),
            m => m.Should().Contain("DiscreteMotion → Standstill"),
            m => m.Should().Contain("Standstill → ErrorStop").And.Contain("FaultCode (S+6 = input 6) = 4"));
        rig.LogsAt(LogLevel.Error).Should().ContainSingle(r => r.Message.StartsWith("carriage: WatchdogTripped: "),
            "the PLC's own fault is logged once, in the protocol's shape");
        rig.LogsAt(LogLevel.Trace).Should().Contain(r => r.Message.Contains("Heartbeat (C+8 = holding 8) ="));
        rig.Logs.GetSnapshot().Where(r => r.Message.Contains("Heartbeat (C+8"))
            .Should().OnlyContain(r => r.Level == LogLevel.Trace);
    }

    [Fact(DisplayName = "GA-U-78 DefaultSpeed above 100 % is unreachable: every way to build a Percentage refuses or saturates")]
    public void Percentage_AboveHundred_CannotExist()
    {
        // Review #10, closed as unreachable: GenericAxisOptions.DefaultSpeed is a ModelingEvolution.Drawing
        // Percentage, whose invariant is [0, 100]. This pins that invariant on the referenced assembly; if a Drawing
        // update ever lifts it, this goes red and Validate() needs the upper bound.
        var ctor = () => new Percentage(101f);
        ctor.Should().Throw<ArgumentOutOfRangeException>();
        var parse = () => Percentage.Parse("150", null);
        parse.Should().Throw<FormatException>();
        Percentage.TryParse("100.5%", null, out _).Should().BeFalse();
        Percentage.Clamp(150f).Value.Should().Be(100f);
        (Percentage.Full + Percentage.Full).Value.Should().Be(100f);
        Percentage.FromFraction(1f).Value.Should().Be(100f);
        var fraction = () => Percentage.FromFraction(1.01f);
        fraction.Should().Throw<ArgumentOutOfRangeException>();

        new GenericAxisOptions { Name = "carriage", Host = "plc", DefaultSpeed = Percentage.Full }
            .Invoking(o => o.Validate()).Should().NotThrow("100 % is the design's inclusive upper bound");
    }
}
