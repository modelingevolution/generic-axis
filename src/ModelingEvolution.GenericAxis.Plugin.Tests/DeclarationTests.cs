using FluentAssertions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>GA-U-42 — the plugin declares two device types, one frozen axis each (design.md § Plugin).</summary>
public sealed class DeclarationTests
{
    [Fact]
    public void GA_U_42_The_Linear_Track_Is_An_ILinearTrack_With_One_Linear_Carriage_Axis()
    {
        var info = PluginHarness.Track;

        info.InterfaceClrType.Should().Be(typeof(ILinearTrack));
        info.InterfaceType.Should().Be("ILinearTrack");
        info.DisplayName.Should().Be("Linear track (PLC, Modbus TCP)");
        info.Axes.Should().ContainSingle()
            .Which.Should().Match<AxisDeclaration>(a => a.Name == "carriage" && a.Kind == AxisKind.Linear);
    }

    [Fact]
    public void GA_U_42_The_Positioner_Is_An_IPositioner_With_One_Rotary_Turntable_Axis()
    {
        var info = PluginHarness.Positioner;

        info.InterfaceClrType.Should().Be(typeof(IPositioner));
        info.InterfaceType.Should().Be("IPositioner");
        info.DisplayName.Should().Be("Rotary positioner (PLC, Modbus TCP)");
        info.Axes.Should().ContainSingle()
            .Which.Should().Match<AxisDeclaration>(a => a.Name == "turntable" && a.Kind == AxisKind.Rotary);
    }

    [Fact]
    public void GA_U_42_The_Plugin_Registers_Exactly_Its_Two_Device_Types()
    {
        PluginHarness.Registry.Items.Select(i => i.DeviceType)
            .Should().BeEquivalentTo("GenericLinearTrack", "GenericPositioner");
    }

    [Fact]
    public void Per_Axis_Keys_Carry_The_Generic_Qualifier_And_Never_Delta_Prefix()
    {
        // ADR-16: the prefix registry is process-wide and last-write-wins; Delta owns "axis.".
        foreach (var info in new[] { PluginHarness.Track, PluginHarness.Positioner })
        foreach (var schema in info.Axes.Single().PropertySchemas)
        {
            schema.Name.Should().StartWith($"generic.axis.{info.Axes.Single().Name}.");
            schema.Name.Should().NotStartWith("axis.");
        }
    }

    [Fact]
    public void Device_Level_Keys_Are_The_Owner_Id_And_Lease_Timeout_With_Their_Defaults()
    {
        foreach (var info in new[] { PluginHarness.Track, PluginHarness.Positioner })
        {
            info.PropertySchemas.Select(s => (s.Name, s.ValueType, s.Required, s.Default)).Should().Equal(
                ("GenericAxisOwnerId", "int", false, "1"),
                ("GenericAxisLeaseTimeoutSeconds", "int", false, "30"));
        }
    }

    [Fact]
    public void Only_The_Plc_Host_Is_Required()
    {
        PluginHarness.AllSchemas(PluginHarness.Track).Where(s => s.Required).Select(s => s.Name)
            .Should().Equal("generic.axis.carriage.Host");
        PluginHarness.AllSchemas(PluginHarness.Positioner).Where(s => s.Required).Select(s => s.Name)
            .Should().Equal("generic.axis.turntable.Host");
    }

    [Fact]
    public void The_Axis_Table_Declares_The_Designed_Suffixes_Types_And_Defaults()
    {
        var schemas = PluginHarness.Track.Axes.Single().PropertySchemas
            .Select(s => (s.Name["generic.axis.carriage.".Length..], s.ValueType, s.Default));

        schemas.Should().Equal(
            ("Host", "string", (string?)null),
            ("Port", "int", "502"),
            ("UnitId", "int", "1"),
            ("CommandBase", "int", "0"),
            ("StatusBase", "int", "100"),
            ("DisplayName", "string", "Carriage"),
            ("TravelMin", "double", null),
            ("TravelMax", "double", null),
            ("MaxVelocity", "double", null),
            ("ReadMin", "double", null),
            ("ReadMax", "double", null),
            ("DefaultSpeedPercent", "double", "100"),
            ("Acceleration", "double", null),
            ("Tolerance", "double", "0.001"),
            ("HomingTimeoutSeconds", "int", "120"));
    }

    [Fact]
    public void Labels_Carry_The_Units_Of_The_Axis_Kind_And_Headings_Carry_The_Axis_Label()
    {
        var carriage = PluginHarness.Track.Axes.Single().PropertySchemas
            .Single(s => s.Name == PluginHarness.CarriageKey("TravelMax"));
        var turntable = PluginHarness.Positioner.Axes.Single().PropertySchemas
            .Single(s => s.Name == PluginHarness.TurntableKey("TravelMax"));

        carriage.DisplayName.Should().Contain("(mm)").And.Contain("only if the PLC publishes none");
        turntable.DisplayName.Should().Contain("(°)");
        carriage.Group.Should().Be("Carriage — Machine limits");
        turntable.Group.Should().Be("Turntable — Machine limits");
    }
}
