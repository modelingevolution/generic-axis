using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Automation;
using RocketWelder.SDK.Automation.Plugins;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Plugin;

/// <summary>
/// Registers the generic external axis over Modbus TCP as two device types (design.md § Plugin,
/// ADR-5): a linear track that carries the robot and a rotary positioner that carries the part. Each
/// has exactly one axis with a frozen name, so a program binds through the marker interface
/// (<see cref="ILinearTrack"/>, <see cref="IPositioner"/>) and the axis name, never through a vendor
/// type. <c>carriage</c> is the Fairino track's frozen axis name, so existing Pamet programs keep
/// binding (FR-9).
///
/// <para>
/// Nothing in the host references this assembly. It is staged into <c>plugins/&lt;id&gt;/&lt;version&gt;/</c>
/// and discovered by rw2's <c>PluginLoader</c>.
/// </para>
/// </summary>
[RocketWelderPlugin("RocketWelder.Motion.Generic")]
public sealed class GenericAxisPlugin : IPlugin
{
    /// <summary>Frozen discriminator of the linear track; persisted inside every <c>DeviceId</c>.</summary>
    public const string LinearTrackDeviceType = "GenericLinearTrack";

    /// <summary>Frozen discriminator of the rotary positioner; persisted inside every <c>DeviceId</c>.</summary>
    public const string PositionerDeviceType = "GenericPositioner";

    /// <summary>Frozen axis name of the linear track.</summary>
    public const string CarriageAxis = "carriage";

    /// <summary>Frozen axis name of the rotary positioner.</summary>
    public const string TurntableAxis = "turntable";

    /// <summary>Hub key of the lease owner id.</summary>
    public static string OwnerIdKey => GenericAxisOwnerIdProperty.Name;

    /// <summary>Hub key of the lease timeout.</summary>
    public static string LeaseTimeoutSecondsKey => GenericAxisLeaseTimeoutSecondsProperty.Name;

    /// <summary>
    /// The linear axis template: the driver's option defaults plus the frozen identity. The host is
    /// blank; the hub must supply it.
    /// </summary>
    internal static GenericAxisOptions CarriageTemplate => new()
    {
        Name = CarriageAxis,
        DisplayName = "Carriage",
        Kind = AxisKind.Linear,
        Host = string.Empty,
    };

    /// <summary>The rotary axis template, as <see cref="CarriageTemplate"/>.</summary>
    internal static GenericAxisOptions TurntableTemplate => new()
    {
        Name = TurntableAxis,
        DisplayName = "Turntable",
        Kind = AxisKind.Rotary,
        Host = string.Empty,
    };

    /// <inheritdoc/>
    public void ConfigureServices(IServiceCollection services)
    {
        // Per-axis values are dynamically keyed, so persisted ConfigSets need the prefix handler
        // before the read model replays anything. Registered once per process (GA-U-45).
        GenericAxisConfigKey.RegisterHandler();

        // One connector, reachable both as itself (the factory hands it devices) and as the hosted
        // service the host runs. TryAdd keeps a second ConfigureServices from adding a second loop.
        services.TryAddSingleton<GenericAxisConnector>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, GenericAxisConnector>(
            sp => sp.GetRequiredService<GenericAxisConnector>()));
    }

    /// <inheritdoc/>
    public void Configure(IPluginContext context)
    {
        var sp = context.Services;
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<GenericAxisPlugin>();
        var connector = sp.GetService<GenericAxisConnector>();

        Register(context.Devices, LinearTrackDeviceType, "ILinearTrack", typeof(ILinearTrack),
            "Linear track (PLC, Modbus TCP)", CarriageTemplate, connector, loggerFactory, logger);
        Register(context.Devices, PositionerDeviceType, "IPositioner", typeof(IPositioner),
            "Rotary positioner (PLC, Modbus TCP)", TurntableTemplate, connector, loggerFactory, logger);
    }

    private static void Register(
        DeviceTypeRegistry registry,
        string deviceType,
        string interfaceType,
        Type interfaceClrType,
        string displayName,
        GenericAxisOptions template,
        GenericAxisConnector? connector,
        ILoggerFactory? loggerFactory,
        ILogger? logger)
    {
        var typeInfo = new MotionDeviceTypeInfo(
            DeviceType: deviceType,
            InterfaceType: interfaceType,
            DisplayName: displayName,
            InterfaceClrType: interfaceClrType,
            // Device-level values only; per-axis values live on the axis that owns them.
            PropertySchemas: DeviceSchemas(),
            Factory: (config, id) =>
            {
                var device = Build(config, id, template, loggerFactory, logger);
                // The only moment the connector can be given the instance it has to attach.
                connector?.Track(id, device);
                return device;
            })
        {
            Axes = [new AxisDeclaration(template.Name, template.Kind, GenericAxisProperties.Declare(template))],
        };

        registry.Register(typeInfo);
        logger?.LogInformation("Registered device type {DeviceType} as {Interface} with axis {Axis} ({Kind})",
            deviceType, interfaceType, template.Name, template.Kind);
    }

    /// <summary>The device-level schemas shared by both device types.</summary>
    internal static ConfigPropertySchema[] DeviceSchemas() =>
    [
        new(OwnerIdKey, "Station id for the axis lease (1–65535)", "int",
            Required: false, Default: Int(GenericAxisOwnerIdProperty.Default), Group: "Lease"),
        new(LeaseTimeoutSecondsKey, "Give up waiting for a held lease after (s; 0 = never)", "int",
            Required: false, Default: Int(GenericAxisLeaseTimeoutSecondsProperty.Default), Group: "Lease"),
    ];

    /// <summary>
    /// Builds the device from the hub's values: bind, validate, construct. No network — connecting is
    /// the connector's job, and the read model rebuilds a device on every reconfigure.
    /// </summary>
    /// <exception cref="ArgumentException">A value is invalid; the message names its hub key.</exception>
    internal static ModbusAxisDevice Build(
        ConfigSet config,
        DeviceId id,
        GenericAxisOptions template,
        ILoggerFactory? loggerFactory,
        ILogger? logger)
    {
        var ownerId = ReadOwnerId(config);
        var options = GenericAxisProperties.Bind(template, config) with { LeaseTimeout = ReadLeaseTimeout(config) };

        logger?.LogInformation(
            "Building generic axis {Device} ({Kind} '{Axis}') at {Host}:{Port} unit {Unit}, bases holding C {CommandBase} / input S {StatusBase}, as owner {Owner}",
            id, options.Kind, options.Name, options.Host, options.Port, options.UnitId,
            options.Map.CommandBase, options.Map.StatusBase, ownerId);

        return options.Kind == AxisKind.Rotary
            ? new ModbusPositioner(id, options, ownerId, loggerFactory)
            : new ModbusLinearTrack(id, options, ownerId, loggerFactory);
    }

    private static ushort ReadOwnerId(ConfigSet config)
    {
        var raw = GenericAxisConfigKey.ReadValue(config, OwnerIdKey);
        if (raw is null) return GenericAxisOwnerIdProperty.Default;

        if (!ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ownerId) || ownerId == 0)
            throw new ArgumentException(
                $"'{OwnerIdKey}' is '{raw}'; it must be a whole number in 1–65535 (0 means 'unowned' in "
                + "register LeaseOwner).", nameof(config));
        return ownerId;
    }

    private static TimeSpan? ReadLeaseTimeout(ConfigSet config)
    {
        var raw = GenericAxisConfigKey.ReadValue(config, LeaseTimeoutSecondsKey);
        var seconds = GenericAxisLeaseTimeoutSecondsProperty.Default;
        if (raw is not null
            && (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds < 0))
            throw new ArgumentException(
                $"'{LeaseTimeoutSecondsKey}' is '{raw}'; it must be a whole number of seconds, 0 to wait "
                + "until the connector cancels.", nameof(config));

        return seconds == 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
