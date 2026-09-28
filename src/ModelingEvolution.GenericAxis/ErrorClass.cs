using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The four error classes of <c>protocol.md</c> § Errors and debugging (ADR-35). The driver, both checkers, the
/// reports and the logs use the same four names, and nothing translates an error from one class into another.
/// </summary>
public enum ErrorClass
{
    /// <summary>The link failed: connect refused or timed out, socket closed, no answer, Modbus exception.</summary>
    Transport,

    /// <summary>The PLC answered, but not per the protocol (map version, limits, State, missing ack).</summary>
    Protocol,

    /// <summary>The PLC reports a fault, or the machine did not do what the PLC accepted.</summary>
    Machine,

    /// <summary>The driver refused before writing anything.</summary>
    Commander,
}

/// <summary>The protocol's fixed map from <see cref="MotionError"/> to <see cref="ErrorClass"/> — the only place a
/// class is decided.</summary>
public static class MotionErrorClasses
{
    /// <summary>The class of <paramref name="error"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A <see cref="MotionError"/> member the protocol does not map — an
    /// SDK update that must be classified deliberately, never defaulted.</exception>
    public static ErrorClass Of(MotionError error) => error switch
    {
        MotionError.CommunicationLost => ErrorClass.Transport,

        MotionError.ProtocolMismatch or MotionError.NotAcknowledged => ErrorClass.Protocol,

        MotionError.DriveFault or MotionError.LimitTripped or MotionError.MotionFailed or MotionError.WatchdogTripped
            or MotionError.HomeLatchFailed or MotionError.SafetyStop => ErrorClass.Machine,

        MotionError.Busy or MotionError.NotHomed or MotionError.OutOfRange or MotionError.UnreachableSpeed
            or MotionError.UnsupportedSense or MotionError.LeaseHeld or MotionError.UnknownAxis
            or MotionError.WrongAxisKind => ErrorClass.Commander,

        _ => throw new ArgumentOutOfRangeException(nameof(error), error,
            $"MotionError {error} ({(int)error}) has no class in protocol.md § Errors and debugging"),
    };

    /// <summary>The class of a <see cref="MotionException"/>.</summary>
    public static ErrorClass Of(MotionException exception) => Of(exception.Error);
}

/// <summary>One register read quoted in an error message: <c>Name (S+14 = 114) = 2[, expected 1]</c>.</summary>
/// <param name="Name">The protocol's register name.</param>
/// <param name="Address">The address, e.g. <c>S+14 = 114</c> (<see cref="RegisterMap.Describe"/>).</param>
/// <param name="Value">The value read, as text (raw register value or a decoded one).</param>
/// <param name="Expected">What the protocol expects, if one value or range is expected.</param>
internal readonly record struct RegisterRead(string Name, string Address, string Value, string? Expected = null)
{
    public override string ToString() =>
        $"{Name} ({Address}) = {Value}{(Expected is null ? "" : $", expected {Expected}")}";
}

/// <summary>
/// Builds every <see cref="MotionException"/> message in the protocol's shape (§ Errors and debugging, rule 1):
/// <c>&lt;axis&gt;: &lt;Class&gt;/&lt;MotionError&gt;: &lt;what happened&gt;. Read &lt;Register&gt; (&lt;address&gt;) =
/// &lt;value&gt;[, expected &lt;value&gt;].</c> No message is built anywhere else.
/// </summary>
internal static class AxisErrors
{
    /// <summary><c>&lt;axis&gt;: &lt;Class&gt;/&lt;MotionError&gt;: &lt;what&gt;[. Read …].</c></summary>
    public static string Message(string axis, MotionError error, string what, params ReadOnlySpan<RegisterRead> reads)
    {
        var text = $"{axis}: {MotionErrorClasses.Of(error)}/{error}: {what.TrimEnd('.')}";
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
            $"{axis}: {MotionErrorClasses.Of(error)}/{error}: {what.TrimEnd('.')}. CommandSeq {seq} written, CommandAck {ack} "
            + $"read{(ackQualifier is null ? "" : " " + ackQualifier)}, State {state} read.",
            axis);

    /// <summary>A register read by name, with the map's address text.</summary>
    public static RegisterRead Read(RegisterMap map, string name, ushort address, object value, string? expected = null) =>
        new(name, map.Describe(address), Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
            expected);
}
