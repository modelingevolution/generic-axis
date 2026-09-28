using System.Text.Json.Serialization;
using RocketWelder.SDK.Automation;

namespace ModelingEvolution.GenericAxis.Plugin;

// The two DEVICE-level values (design.md § Plugin). Typed ConfigProperty records, so the host's
// ConfigPropertyJsonConverter.ScanAssembly registers them from this assembly; an unregistered name is
// silently dropped by the Add-device dialog. The "GenericAxis" qualifier keeps them clear of Delta's
// PositionerOwnerId in the process-wide, last-write-wins name registry (ADR-16).

/// <summary>
/// This station's 16-bit id for the advisory lease (protocol register <c>LeaseOwner</c>), 1–65535.
/// 0 is the protocol's "unowned" marker. Two commanders of one axis must carry different ids.
/// </summary>
[JsonConverter(typeof(ConfigPropertyJsonConverter))]
public record GenericAxisOwnerIdProperty(int Value)
    : ConfigProperty<int, GenericAxisOwnerIdProperty>(Value), IConfigProperty<GenericAxisOwnerIdProperty>
{
    /// <summary>Used when the hub holds no value.</summary>
    public const int Default = 1;

    /// <inheritdoc cref="IConfigProperty{TSelf}.Name"/>
    public static string Name => "GenericAxisOwnerId";
}

/// <summary>
/// How long <c>ConnectAsync</c> waits for a lease held by a live foreign commander before it gives up
/// with <c>LeaseHeld</c>. 0 waits until the connector cancels.
/// </summary>
[JsonConverter(typeof(ConfigPropertyJsonConverter))]
public record GenericAxisLeaseTimeoutSecondsProperty(int Value)
    : ConfigProperty<int, GenericAxisLeaseTimeoutSecondsProperty>(Value), IConfigProperty<GenericAxisLeaseTimeoutSecondsProperty>
{
    /// <summary>Used when the hub holds no value.</summary>
    public const int Default = 30;

    /// <inheritdoc cref="IConfigProperty{TSelf}.Name"/>
    public static string Name => "GenericAxisLeaseTimeoutSeconds";
}
