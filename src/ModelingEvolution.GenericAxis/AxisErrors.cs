using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>One register read quoted in an error message: <c>Name (S+14 = input 14) = 2[, expected 1]</c>.</summary>
/// <param name="Name">The protocol's register name.</param>
/// <param name="Address">The address, e.g. <c>S+14 = input 14</c> (<see cref="RegisterMap.Describe"/>).</param>
/// <param name="Value">The value read, as text (raw register value or a decoded one).</param>
/// <param name="Expected">What the protocol expects, if one value or range is expected.</param>
internal readonly record struct RegisterRead(string Name, string Address, string Value, string? Expected = null)
{
    public override string ToString() =>
        $"{Name} ({Address}) = {Value}{(Expected is null ? "" : $", expected {Expected}")}";
}

/// <summary>
/// Builds every <see cref="MotionException"/> message in the protocol's shape (§ Errors and debugging, rule 1):
/// <c>&lt;axis&gt;: &lt;MotionError&gt;: &lt;what happened&gt;. Read &lt;Register&gt; (&lt;address&gt;) =
/// &lt;value&gt;[, expected &lt;value&gt;].</c> No message is built anywhere else.
/// </summary>
internal static class AxisErrors
{
    /// <summary><c>&lt;axis&gt;: &lt;MotionError&gt;: &lt;what&gt;[. Read …].</c></summary>
    public static string Message(string axis, MotionError error, string what, params ReadOnlySpan<RegisterRead> reads)
    {
        var text = $"{axis}: {error}: {what.TrimEnd('.')}";
        if (reads.Length == 0) return text + ".";

        var parts = new string[reads.Length];
        for (var i = 0; i < reads.Length; i++) parts[i] = reads[i].ToString();
        return $"{text}. Read {string.Join(", ", parts)}.";
    }

    /// <summary>A <see cref="MotionException"/> in the protocol's shape.</summary>
    public static MotionException Create(string axis, MotionError error, string what,
        params ReadOnlySpan<RegisterRead> reads) =>
        new(error, Message(axis, error, what, reads), axis);

    /// <summary>
    /// A command error: <c>&lt;what&gt;. CommandSeq n written, CommandAck m read[ after …], State s read.</c>
    /// </summary>
    public static MotionException Command(string axis, MotionError error, string what, ushort seq, ushort ack,
        ushort state, string? ackQualifier = null) =>
        new(error,
            $"{axis}: {error}: {what.TrimEnd('.')}. CommandSeq {seq} written, CommandAck {ack} "
            + $"read{(ackQualifier is null ? "" : " " + ackQualifier)}, State {state} read.",
            axis);

    /// <summary>A register read by field, with the map's address text (<c>S+14 = input 14</c>).</summary>
    public static RegisterRead Read(RegisterMap map, RegisterField field, object value, string? expected = null) =>
        new(field.Name, map.Describe(field), Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            expected);
}
