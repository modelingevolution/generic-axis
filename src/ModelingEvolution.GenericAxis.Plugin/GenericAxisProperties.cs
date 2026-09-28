using System.Globalization;
using ModelingEvolution.Drawing.Units;
using RocketWelder.SDK.Automation;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Plugin;

/// <summary>
/// The per-axis values one axis exposes to the devices hub (design.md § Plugin) — <b>one table</b> that
/// both declares them (<see cref="Declare"/>) and folds stored values into a
/// <see cref="GenericAxisOptions"/> (<see cref="Bind"/>). A property offered but never read, or read
/// but never offered, is impossible by construction (GA-U-43).
///
/// <para>
/// Every default is rendered from the axis template, which is the driver's own option defaults, so the
/// dialog's pre-fill and the driver's behaviour for an absent value are one number. The machine
/// limits default to blank: they come from the PLC (FR-6) and are only a fallback when the PLC
/// publishes none.
/// </para>
/// </summary>
internal static class GenericAxisProperties
{
    private const string GroupConnection = "Connection";
    private const string GroupIdentity = "Identity";
    private const string GroupLimits = "Machine limits";
    private const string GroupMotion = "Motion";
    private const string GroupSupervision = "Supervision";

    /// <summary>
    /// One per-installation value.
    /// </summary>
    /// <param name="Suffix">The last segment of the hub key.</param>
    /// <param name="Field">The <see cref="GenericAxisOptions"/> member it lands in; used to name the
    /// hub key when the options' own validation refuses the value.</param>
    /// <param name="Label">The dialog label for an axis of the given kind (units differ).</param>
    /// <param name="ValueType">Parse hint the dialog validates against.</param>
    /// <param name="Group">Sub-heading inside the axis's section.</param>
    /// <param name="Required">Whether the hub must hold a value before the device can be created.</param>
    /// <param name="Default">Renders the template's value as the pre-fill; <see langword="null"/> is blank.</param>
    /// <param name="Apply">Folds one stored value into the options under construction.</param>
    internal sealed record AxisProperty(
        string Suffix,
        string Field,
        Func<AxisKind, string> Label,
        string ValueType,
        string Group,
        bool Required,
        Func<GenericAxisOptions, string?> Default,
        Func<GenericAxisOptions, string, GenericAxisOptions> Apply);

    /// <summary>The table, in the dialog's field order.</summary>
    internal static readonly IReadOnlyList<AxisProperty> All =
    [
        new("Host", nameof(GenericAxisOptions.Host),
            _ => "PLC host", "string", GroupConnection, Required: true,
            o => string.IsNullOrEmpty(o.Host) ? null : o.Host,
            (o, v) => o with { Host = v.Trim() }),

        new("Port", nameof(GenericAxisOptions.Port),
            _ => "Modbus TCP port", "int", GroupConnection, Required: false,
            o => Int(o.Port),
            (o, v) => o with { Port = ParseInt(v) }),

        new("UnitId", nameof(GenericAxisOptions.UnitId),
            _ => "Unit id", "int", GroupConnection, Required: false,
            o => Int(o.UnitId),
            (o, v) => o with { UnitId = ParseInt(v) }),

        new("CommandBase", nameof(GenericAxisOptions.Map),
            _ => "Command block base register", "int", GroupConnection, Required: false,
            o => Int(o.Map.CommandBase),
            (o, v) => o with { Map = o.Map with { CommandBase = ParseInt(v) } }),

        new("StatusBase", nameof(GenericAxisOptions.Map),
            _ => "Status block base register", "int", GroupConnection, Required: false,
            o => Int(o.Map.StatusBase),
            (o, v) => o with { Map = o.Map with { StatusBase = ParseInt(v) } }),

        new("DisplayName", nameof(GenericAxisOptions.DisplayName),
            _ => "Axis label", "string", GroupIdentity, Required: false,
            o => o.DisplayName,
            // The frozen Name is never editable from a station; relabelling lands here instead.
            (o, v) => o with { DisplayName = v.Trim() }),

        new("TravelMin", nameof(GenericAxisOptions.ConfiguredTravelMin),
            k => $"Lower travel limit ({Unit(k)}) — only if the PLC publishes none; record the source",
            "double", GroupLimits, Required: false,
            o => Num(o.ConfiguredTravelMin),
            (o, v) => o with { ConfiguredTravelMin = ParseDouble(v) }),

        new("TravelMax", nameof(GenericAxisOptions.ConfiguredTravelMax),
            k => $"Upper travel limit ({Unit(k)}) — only if the PLC publishes none; record the source",
            "double", GroupLimits, Required: false,
            o => Num(o.ConfiguredTravelMax),
            (o, v) => o with { ConfiguredTravelMax = ParseDouble(v) }),

        new("MaxVelocity", nameof(GenericAxisOptions.ConfiguredMaxVelocity),
            k => $"Maximum velocity ({Unit(k)}/s) — only if the PLC publishes none; record the source",
            "double", GroupLimits, Required: false,
            o => Num(o.ConfiguredMaxVelocity),
            (o, v) => o with { ConfiguredMaxVelocity = ParseDouble(v) }),

        new("ReadMin", nameof(GenericAxisOptions.ReadMin),
            k => $"Lowest accepted reading ({Unit(k)}; blank = travel)", "double", GroupLimits, Required: false,
            o => Num(o.ReadMin),
            (o, v) => o with { ReadMin = ParseDouble(v) }),

        new("ReadMax", nameof(GenericAxisOptions.ReadMax),
            k => $"Highest accepted reading ({Unit(k)}; blank = travel)", "double", GroupLimits, Required: false,
            o => Num(o.ReadMax),
            (o, v) => o with { ReadMax = ParseDouble(v) }),

        new("DefaultSpeedPercent", nameof(GenericAxisOptions.DefaultSpeed),
            _ => "Speed when a move names none (% of max)", "double", GroupMotion, Required: false,
            o => Num(o.DefaultSpeed.Value),
            (o, v) => o with { DefaultSpeed = new Percentage((float)ParseDouble(v)) }),

        new("Acceleration", nameof(GenericAxisOptions.Acceleration),
            k => $"Acceleration ({Unit(k)}/s²; blank = PLC default)", "double", GroupMotion, Required: false,
            o => Num(o.Acceleration),
            (o, v) => o with { Acceleration = ParseDouble(v) }),

        new("Tolerance", nameof(GenericAxisOptions.Tolerance),
            k => $"Reported in-position tolerance ({Unit(k)})", "double", GroupMotion, Required: false,
            o => Num(o.Tolerance),
            (o, v) => o with { Tolerance = ParseDouble(v) }),

        new("HomingTimeoutSeconds", nameof(GenericAxisOptions.HomingTimeout),
            _ => "Homing budget (s)", "int", GroupSupervision, Required: false,
            o => Int((int)o.HomingTimeout.TotalSeconds),
            (o, v) => o with { HomingTimeout = TimeSpan.FromSeconds(ParseInt(v)) }),
    ];

    /// <summary>
    /// Declares the table against one axis template: fully scoped key, label in the axis's units,
    /// parse hint, the template's value as pre-fill, and a heading qualified by the axis label.
    /// </summary>
    internal static ConfigPropertySchema[] Declare(GenericAxisOptions template) =>
    [
        .. All.Select(p => new ConfigPropertySchema(
            Name: GenericAxisConfigKey.For(template.Name, p.Suffix),
            DisplayName: p.Label(template.Kind),
            ValueType: p.ValueType,
            Required: p.Required,
            Default: p.Default(template),
            Group: $"{template.DisplayName} — {p.Group}"))
    ];

    /// <summary>
    /// Folds every value the hub holds for <paramref name="template"/>'s axis onto the template, then
    /// runs <see cref="GenericAxisOptions.Validate"/>. An absent value keeps the template's.
    /// </summary>
    /// <exception cref="ArgumentException">A stored value does not parse, or the options refuse it.
    /// The message names the hub key so the operator can find the field.</exception>
    internal static GenericAxisOptions Bind(GenericAxisOptions template, ConfigSet config)
    {
        var options = template;
        foreach (var p in All)
        {
            var raw = GenericAxisConfigKey.Read(config, template.Name, p.Suffix);
            if (raw is null) continue;
            try
            {
                options = p.Apply(options, raw);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ArgumentException(
                    $"'{GenericAxisConfigKey.For(template.Name, p.Suffix)}' = '{raw}' (declared {p.ValueType}) was "
                    + $"refused: {ex.GetType().Name}: {ex.Message}", nameof(config), ex);
            }
        }

        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"{KeysFor(template.Name, ex.ParamName)} refused by GenericAxisOptions.Validate: {ex.Message}",
                nameof(config), ex);
        }

        return options;
    }

    /// <summary>
    /// The hub key(s) behind an options field the driver's validation named, or the axis's key prefix
    /// when the field has no hub entry.
    /// </summary>
    internal static string KeysFor(string axisName, string? field)
    {
        var keys = All.Where(p => p.Field == field)
            .Select(p => $"'{GenericAxisConfigKey.For(axisName, p.Suffix)}'")
            .ToArray();
        return keys.Length > 0
            ? string.Join(" / ", keys)
            : $"'{GenericAxisConfigKey.Prefix}{axisName}.*'";
    }

    private static string Unit(AxisKind kind) => kind == AxisKind.Rotary ? "°" : "mm";

    // Invariant culture on both sides: the hub is a machine-readable store, and a station set to a
    // comma-decimal locale must not change what is persisted.
    private static string? Num(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);
    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    internal static double ParseDouble(string raw) => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
    internal static int ParseInt(string raw) => int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
