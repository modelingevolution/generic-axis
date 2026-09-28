using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Automation;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>GA-U-43 — the per-axis property table round-trips into <see cref="GenericAxisOptions"/>.</summary>
public sealed class PropertyTableTests
{
    /// <summary>A non-default value for every suffix, and where it must land.</summary>
    private static readonly (string Suffix, string Raw, Func<GenericAxisOptions, object?> Read, object Expected)[] Values =
    [
        ("Host", "192.168.58.20", o => o.Host, "192.168.58.20"),
        ("Port", "5020", o => o.Port, 5020),
        ("UnitId", "7", o => o.UnitId, 7),
        ("CommandBase", "200", o => o.Map.CommandBase, 200),
        ("StatusBase", "300", o => o.Map.StatusBase, 300),
        ("DisplayName", "Track", o => o.DisplayName, "Track"),
        ("TravelMin", "-5.5", o => o.ConfiguredTravelMin, -5.5),
        ("TravelMax", "9000", o => o.ConfiguredTravelMax, 9000.0),
        ("MaxVelocity", "400.25", o => o.ConfiguredMaxVelocity, 400.25),
        ("ReadMin", "-20", o => o.ReadMin, -20.0),
        ("ReadMax", "9010", o => o.ReadMax, 9010.0),
        ("DefaultSpeedPercent", "20", o => o.DefaultSpeed, new Percentage(20)),
        ("Acceleration", "1500", o => o.Acceleration, 1500.0),
        ("Tolerance", "0.05", o => o.Tolerance, 0.05),
        ("HomingTimeoutSeconds", "90", o => o.HomingTimeout, TimeSpan.FromSeconds(90)),
    ];

    private static ConfigSet Stored(IEnumerable<(string Suffix, string Raw)> values) =>
        PluginHarness.ConfigFor(PluginHarness.Track,
            [.. values.Select(v => (PluginHarness.CarriageKey(v.Suffix), v.Raw))]);

    [Fact]
    public void GA_U_43_Every_Declared_Schema_Has_A_Binder_And_Every_Binder_A_Schema()
    {
        var declared = PluginHarness.Track.Axes.Single().PropertySchemas
            .Select(s => s.Name["generic.axis.carriage.".Length..]);

        declared.Should().Equal(GenericAxisProperties.All.Select(p => p.Suffix));
        Values.Select(v => v.Suffix).Should().Equal(GenericAxisProperties.All.Select(p => p.Suffix),
            "this test must cover every row of the table");
    }

    [Fact]
    public void GA_U_43_Every_Stored_Value_Reaches_Its_Options_Field()
    {
        var config = Stored(Values.Select(v => (v.Suffix, v.Raw)));

        var options = GenericAxisProperties.Bind(GenericAxisPlugin.CarriageTemplate, config);

        foreach (var (suffix, _, read, expected) in Values)
            read(options).Should().Be(expected, $"'{suffix}' must reach its options field");
    }

    [Fact]
    public void GA_U_43_Each_Value_Differs_From_The_Template_So_The_Round_Trip_Proves_Something()
    {
        var template = GenericAxisPlugin.CarriageTemplate with { Host = "h" };
        foreach (var (suffix, _, read, expected) in Values.Where(v => v.Suffix != "Host"))
            read(template).Should().NotBe(expected, $"'{suffix}' test value must not equal the default");
    }

    [Fact]
    public void An_Absent_Value_Keeps_The_Driver_Default()
    {
        var config = Stored([("Host", "10.0.0.5")]);

        var options = GenericAxisProperties.Bind(GenericAxisPlugin.CarriageTemplate, config);

        options.Should().Be(GenericAxisPlugin.CarriageTemplate with { Host = "10.0.0.5" });
    }

    [Fact]
    public void The_Frozen_Identity_Is_Not_Bindable()
    {
        var options = GenericAxisProperties.Bind(GenericAxisPlugin.TurntableTemplate,
            PluginHarness.ConfigFor(PluginHarness.Positioner,
                (PluginHarness.TurntableKey("Host"), "10.0.0.6"),
                (PluginHarness.TurntableKey("DisplayName"), "Headstock")));

        options.Name.Should().Be("turntable");
        options.Kind.Should().Be(RocketWelder.SDK.Devices.Motion.AxisKind.Rotary);
        options.DisplayName.Should().Be("Headstock");
    }

    [Fact]
    public void Numbers_Are_Parsed_In_The_Invariant_Culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
        try
        {
            var options = GenericAxisProperties.Bind(GenericAxisPlugin.CarriageTemplate,
                Stored([("Host", "h"), ("Tolerance", "0.25")]));
            options.Tolerance.Should().Be(0.25);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Every_Declared_Property_Is_Storable_And_Survives_The_Json_Round_Trip()
    {
        var original = Stored(Values.Select(v => (v.Suffix, v.Raw)));

        var restored = JsonSerializer.Deserialize<ConfigSet>(JsonSerializer.Serialize(original))!;

        restored.Select(p => p.Item1).Should().BeEquivalentTo(original.Select(p => p.Item1));
        GenericAxisProperties.Bind(GenericAxisPlugin.CarriageTemplate, restored)
            .Should().Be(GenericAxisProperties.Bind(GenericAxisPlugin.CarriageTemplate, original));
    }
}
