using RocketWelder.SDK.Automation;

namespace ModelingEvolution.GenericAxis.Plugin;

/// <summary>
/// How a per-axis value is keyed in the devices hub: <c>generic.axis.&lt;axisName&gt;.&lt;suffix&gt;</c>
/// (design.md § Plugin, ADR-16).
///
/// <para>
/// The <c>generic</c> qualifier is load-bearing. The host's prefix-handler and property registries are
/// process-wide and last-write-wins, and the Delta plugin already owns <c>axis.</c>. A bare
/// <c>axis.</c> here would make one plugin's stored values deserialise as the other's.
/// </para>
///
/// <para>
/// The axis segment is the frozen axis name (<c>carriage</c>, <c>turntable</c>), never the display
/// name, so relabelling an axis cannot orphan its stored values.
/// </para>
/// </summary>
public static class GenericAxisConfigKey
{
    /// <summary>The prefix every per-axis key carries, and the prefix handler's trigger.</summary>
    public const string Prefix = "generic.axis.";

    private static int _registered;

    /// <summary>Composes the hub key for one property of one axis.</summary>
    public static string For(string axisName, string suffix) => $"{Prefix}{axisName}.{suffix}";

    /// <summary>
    /// Reads one per-axis value out of a stored <see cref="ConfigSet"/>, or <see langword="null"/>
    /// when the operator left it blank.
    /// </summary>
    /// <remarks>
    /// A linear scan: the typed <see cref="ConfigSet.Get{TProperty,TValue}"/> keys off a property
    /// type's static <c>Name</c>, which a dynamically-keyed value does not have.
    /// </remarks>
    public static string? Read(ConfigSet config, string axisName, string suffix) =>
        ReadValue(config, For(axisName, suffix));

    /// <summary>Reads the value stored under <paramref name="key"/>, blank reading as absent.</summary>
    internal static string? ReadValue(ConfigSet config, string key)
    {
        foreach (var (name, value) in config)
        {
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = value.Value?.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        return null;
    }

    /// <summary>
    /// Registers the <see cref="Prefix"/> handler with <see cref="ConfigPropertyJsonConverter"/>, so a
    /// persisted <see cref="ConfigSet"/> carrying per-axis values can be deserialised. Idempotent: the
    /// converter's handler bag is append-only, and a second identical handler would only make bag
    /// order something to reason about (GA-U-45).
    /// </summary>
    /// <returns><see langword="true"/> when this call registered the handler; <see langword="false"/>
    /// when an earlier call already had.</returns>
    public static bool RegisterHandler()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return false;
        ConfigPropertyJsonConverter.RegisterPrefixHandler(
            Prefix, (name, rawValue) => (name, new GenericAxisConfigValue(name, rawValue)));
        return true;
    }
}
