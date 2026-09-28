using RocketWelder.SDK.Automation;

namespace ModelingEvolution.GenericAxis.Plugin;

/// <summary>
/// One stored per-axis value. Dynamically keyed (<see cref="GenericAxisConfigKey"/>), so it carries its
/// own full name. The raw string is parsed against the property's declared value type by
/// <see cref="GenericAxisProperties.Bind"/>.
/// </summary>
/// <param name="Name">The full hub key, e.g. <c>generic.axis.carriage.Host</c>.</param>
/// <param name="Value">The raw value as stored.</param>
public sealed record GenericAxisConfigValue(string Name, string Value)
    : IConfigTypePropertyInstance, IConfigPropertyInstance<string>
{
    object IConfigPropertyInstance.Value => Value;

    /// <inheritdoc/>
    public override string ToString() => $"{Name}={Value}";
}
