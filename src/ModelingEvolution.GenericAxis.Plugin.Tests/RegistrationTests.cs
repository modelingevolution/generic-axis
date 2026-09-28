using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RocketWelder.SDK.Automation;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>GA-U-45 — the prefix handler and the connector register once.</summary>
public sealed class RegistrationTests
{
    [Fact]
    public void GA_U_45_ConfigureServices_Twice_Registers_The_Prefix_Handler_Once()
    {
        _ = PluginHarness.Registry; // the harness's own ConfigureServices already ran once
        var plugin = new GenericAxisPlugin();

        plugin.ConfigureServices(new ServiceCollection());
        plugin.ConfigureServices(new ServiceCollection());

        GenericAxisConfigKey.RegisterHandler().Should().BeFalse("an earlier call already registered it");
        ConfigPropertyJsonConverter.TryCreate(PluginHarness.CarriageKey("Host"), "10.0.0.5", out var value)
            .Should().BeTrue();
        value.Should().BeOfType<GenericAxisConfigValue>()
            .Which.Name.Should().Be("generic.axis.carriage.Host");
    }

    [Fact]
    public void GA_U_45_ConfigureServices_Twice_Adds_One_Connector_And_One_Hosted_Service()
    {
        var services = new ServiceCollection();
        var plugin = new GenericAxisPlugin();

        plugin.ConfigureServices(services);
        plugin.ConfigureServices(services);

        services.Count(d => d.ServiceType == typeof(GenericAxisConnector)).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().Should().ContainSingle()
            .Which.Should().BeSameAs(provider.GetRequiredService<GenericAxisConnector>());
    }

    [Fact]
    public void The_Device_Level_Properties_Are_Storable_After_The_Assembly_Scan()
    {
        _ = PluginHarness.Registry;

        ConfigPropertyJsonConverter.TryCreate("GenericAxisOwnerId", "9", out var owner).Should().BeTrue();
        owner.Should().BeOfType<GenericAxisOwnerIdProperty>().Which.Value.Should().Be(9);
        ConfigPropertyJsonConverter.TryCreate("GenericAxisLeaseTimeoutSeconds", "0", out var lease).Should().BeTrue();
        lease.Should().BeOfType<GenericAxisLeaseTimeoutSecondsProperty>().Which.Value.Should().Be(0);
    }
}
