using Microsoft.Extensions.DependencyInjection;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Automation;
using RocketWelder.SDK.Automation.Plugins;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>
/// Runs the plugin through the host's startup — <c>ScanAssembly</c>, then <c>ConfigureServices</c> and
/// <c>Configure</c> against a real <see cref="DeviceTypeRegistry"/> and container — once per test
/// assembly. The converter's prefix handlers and name table are process-wide statics, so registering
/// per test would only exercise the idempotency guard, which GA-U-45 tests on its own.
/// </summary>
internal static class PluginHarness
{
    private sealed record Context(DeviceTypeRegistry Devices, IServiceProvider Services) : IPluginContext;

    private sealed record Started(DeviceTypeRegistry Registry, IServiceProvider Services);

    private static readonly Lazy<Started> Registered = new(Run, isThreadSafe: true);

    internal static DeviceTypeRegistry Registry => Registered.Value.Registry;

    /// <summary>The connector instance the plugin's factories hand devices to.</summary>
    internal static GenericAxisConnector Connector =>
        Registered.Value.Services.GetRequiredService<GenericAxisConnector>();

    internal static MotionDeviceTypeInfo Track => TypeInfo(GenericAxisPlugin.LinearTrackDeviceType);

    internal static MotionDeviceTypeInfo Positioner => TypeInfo(GenericAxisPlugin.PositionerDeviceType);

    internal static MotionDeviceTypeInfo TypeInfo(string deviceType)
    {
        var info = Registry.Get(deviceType)
            ?? throw new InvalidOperationException($"'{deviceType}' was not registered.");
        return info as MotionDeviceTypeInfo
            ?? throw new InvalidOperationException(
                $"'{deviceType}' registered as {info.GetType().Name}; the axis roster only reaches the host through MotionDeviceTypeInfo.");
    }

    /// <summary>Every property a device type declares: device-level plus its axis's.</summary>
    internal static IReadOnlyList<ConfigPropertySchema> AllSchemas(MotionDeviceTypeInfo info) =>
        [.. info.PropertySchemas, .. info.Axes.SelectMany(a => a.PropertySchemas)];

    private static Started Run()
    {
        // The host does this for every loaded plugin assembly: an unscanned ConfigProperty type makes
        // TryCreate return false and the dialog silently drops the value.
        ConfigPropertyJsonConverter.ScanAssembly(typeof(GenericAxisPlugin).Assembly);

        var services = new ServiceCollection();
        var plugin = new GenericAxisPlugin();
        plugin.ConfigureServices(services);

        var registry = new DeviceTypeRegistry();
        var provider = services.BuildServiceProvider();
        plugin.Configure(new Context(registry, provider));
        return new Started(registry, provider);
    }

    /// <summary>
    /// The <see cref="ConfigSet"/> the Add-device dialog produces: every declared property at its
    /// default unless overridden, built through the same <c>TryCreate</c> the dialog calls. Blank
    /// values are not stored.
    /// </summary>
    internal static ConfigSet ConfigFor(MotionDeviceTypeInfo info, params (string Key, string Value)[] overrides)
    {
        var byKey = overrides.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);
        var props = new List<IConfigPropertyInstance>();
        foreach (var schema in AllSchemas(info))
        {
            var value = byKey.TryGetValue(schema.Name, out var v) ? v : schema.Default;
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!ConfigPropertyJsonConverter.TryCreate(schema.Name, value, out var prop))
                throw new InvalidOperationException($"The dialog could not store '{schema.Name}' = '{value}'.");
            props.Add(prop!);
        }

        var unknown = byKey.Keys.Except(AllSchemas(info).Select(s => s.Name), StringComparer.OrdinalIgnoreCase).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"Not declared by {info.DeviceType}: {string.Join(", ", unknown)}");

        return new ConfigSet([.. props]);
    }

    /// <summary>Builds the device through the registered factory, exactly as the read model does.</summary>
    internal static ModbusAxisDevice Build(MotionDeviceTypeInfo info, ConfigSet config, DeviceId? id = null) =>
        (ModbusAxisDevice)info.Factory(config, id ?? DeviceId.New(info.DeviceType));

    internal static string CarriageKey(string suffix) =>
        GenericAxisConfigKey.For(GenericAxisPlugin.CarriageAxis, suffix);

    internal static string TurntableKey(string suffix) =>
        GenericAxisConfigKey.For(GenericAxisPlugin.TurntableAxis, suffix);
}
