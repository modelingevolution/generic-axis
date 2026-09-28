using System.Diagnostics;
using FluentAssertions;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>GA-U-44 — the factory refuses bad values and never touches the network.</summary>
public sealed class FactoryTests
{
    // TEST-NET-1 (RFC 5737): guaranteed unroutable, so any connect attempt would hang or fail.
    private const string UnreachableHost = "192.0.2.1";

    [Fact]
    public void GA_U_44_Owner_Id_Zero_Is_Refused_Naming_The_Key()
    {
        var config = PluginHarness.ConfigFor(PluginHarness.Track,
            (PluginHarness.CarriageKey("Host"), UnreachableHost),
            ("GenericAxisOwnerId", "0"));

        var build = () => PluginHarness.Build(PluginHarness.Track, config);

        build.Should().Throw<ArgumentException>().WithMessage("*'GenericAxisOwnerId'*");
    }

    [Theory]
    [InlineData("GenericAxisOwnerId", "65536")]
    [InlineData("GenericAxisLeaseTimeoutSeconds", "-1")]
    public void GA_U_44_Invalid_Device_Values_Are_Refused_Naming_The_Key(string key, string value)
    {
        var config = PluginHarness.ConfigFor(PluginHarness.Track,
            (PluginHarness.CarriageKey("Host"), UnreachableHost), (key, value));

        var build = () => PluginHarness.Build(PluginHarness.Track, config);

        build.Should().Throw<ArgumentException>().WithMessage($"*'{key}'*");
    }

    [Theory]
    [InlineData("Port", "0")]
    [InlineData("Port", "not-a-port")]
    [InlineData("UnitId", "256")]
    [InlineData("DefaultSpeedPercent", "0")]
    [InlineData("DefaultSpeedPercent", "150")]
    [InlineData("Tolerance", "0")]
    [InlineData("HomingTimeoutSeconds", "99999999999")]
    public void GA_U_44_Invalid_Axis_Values_Are_Refused_Naming_The_Key(string suffix, string value)
    {
        var config = PluginHarness.ConfigFor(PluginHarness.Track,
            (PluginHarness.CarriageKey("Host"), UnreachableHost), (PluginHarness.CarriageKey(suffix), value));

        var build = () => PluginHarness.Build(PluginHarness.Track, config);

        build.Should().Throw<ArgumentException>().WithMessage($"*'{PluginHarness.CarriageKey(suffix)}'*");
    }

    [Fact]
    public void GA_U_44_A_Missing_Host_Is_Refused_Naming_The_Key()
    {
        var config = PluginHarness.ConfigFor(PluginHarness.Track);

        var build = () => PluginHarness.Build(PluginHarness.Track, config);

        build.Should().Throw<ArgumentException>().WithMessage($"*'{PluginHarness.CarriageKey("Host")}'*");
    }

    [Fact]
    public void GA_U_44_An_Unreachable_Host_Still_Builds_Without_Connecting_And_Is_Handed_To_The_Connector()
    {
        var id = DeviceId.New(GenericAxisPlugin.LinearTrackDeviceType);
        var config = PluginHarness.ConfigFor(PluginHarness.Track,
            (PluginHarness.CarriageKey("Host"), UnreachableHost), ("GenericAxisOwnerId", "7"));

        var clock = Stopwatch.StartNew();
        var device = PluginHarness.Build(PluginHarness.Track, config, id);
        clock.Stop();

        // A connect to TEST-NET-1 would take the 1 s connect timeout; the factory returns at once.
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        device.IsConnected.Should().BeFalse();
        device.OwnerId.Should().Be((ushort)7);
        PluginHarness.Connector.IsTracking(id, device).Should().BeTrue();
    }

    [Fact]
    public void The_Linear_Track_Factory_Builds_An_ILinearTrack_With_The_Carriage_Axis()
    {
        var device = PluginHarness.Build(PluginHarness.Track, PluginHarness.ConfigFor(PluginHarness.Track,
            (PluginHarness.CarriageKey("Host"), UnreachableHost),
            (PluginHarness.CarriageKey("Port"), "5020"),
            (PluginHarness.CarriageKey("UnitId"), "3")));

        device.Should().BeOfType<ModbusLinearTrack>().And.BeAssignableTo<ILinearTrack>();
        device.Address.Should().Be($"modbus://{UnreachableHost}:5020/3");
        device["carriage"].Should().BeAssignableTo<ILinearAxis>();
        device["carriage"].DisplayName.Should().Be("Carriage");
    }

    [Fact]
    public void The_Positioner_Factory_Builds_An_IPositioner_With_The_Turntable_Axis()
    {
        var device = PluginHarness.Build(PluginHarness.Positioner, PluginHarness.ConfigFor(PluginHarness.Positioner,
            (PluginHarness.TurntableKey("Host"), UnreachableHost),
            (PluginHarness.TurntableKey("DisplayName"), "Headstock")));

        device.Should().BeOfType<ModbusPositioner>().And.BeAssignableTo<IPositioner>();
        device["turntable"].Should().BeAssignableTo<IRotaryAxis>();
        device["turntable"].DisplayName.Should().Be("Headstock");
        device.OwnerId.Should().Be((ushort)GenericAxisOwnerIdProperty.Default);
    }

    [Fact]
    public void A_Rebuild_Replaces_The_Tracked_Instance()
    {
        var id = DeviceId.New(GenericAxisPlugin.LinearTrackDeviceType);
        var config = PluginHarness.ConfigFor(PluginHarness.Track, (PluginHarness.CarriageKey("Host"), UnreachableHost));

        var first = PluginHarness.Build(PluginHarness.Track, config, id);
        var second = PluginHarness.Build(PluginHarness.Track, config, id);

        PluginHarness.Connector.IsTracking(id, first).Should().BeFalse();
        PluginHarness.Connector.IsTracking(id, second).Should().BeTrue();
    }
}
