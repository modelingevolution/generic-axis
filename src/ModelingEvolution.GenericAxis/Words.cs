using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The protocol's value codec, in one place so the driver and the C# conformance checker share exactly one
/// implementation (ADR-28). Transport: 32-bit values are two registers, <b>low word first</b>, two's complement.
/// Units: position 0.001 mm or 0.001°, velocity 0.001 unit/s, acceleration 0.001 unit/s².
/// </summary>
internal static class Words
{
    /// <summary>Register quanta per axis unit (protocol § Transport: 0.001 unit).</summary>
    public const double Scale = 1000.0;

    /// <summary>One register quantum in axis units — the finest figure the map can carry (ADR-19).</summary>
    public const double Quantum = 1.0 / Scale;

    /// <summary>Splits an int32 into <c>[low, high]</c>.</summary>
    public static (ushort Low, ushort High) Split(int value)
    {
        var raw = unchecked((uint)value);
        return ((ushort)(raw & 0xFFFF), (ushort)(raw >> 16));
    }

    /// <summary>Writes an int32 into two registers, low word first.</summary>
    public static void Write(Span<ushort> destination, int value)
    {
        var (low, high) = Split(value);
        destination[0] = low;
        destination[1] = high;
    }

    /// <summary>Joins two registers, low word first, into an int32.</summary>
    public static int Join(ushort low, ushort high) => unchecked((int)(((uint)high << 16) | low));

    /// <summary>Reads an int32 from two registers, low word first.</summary>
    public static int Read(ReadOnlySpan<ushort> source) => Join(source[0], source[1]);

    /// <summary>
    /// Axis units → register value: ×1000, rounded half away from zero, checked against the int32 range.
    /// </summary>
    /// <param name="units">The value in mm, °, unit/s or unit/s².</param>
    /// <param name="what">What the value is, for the refusal message.</param>
    /// <param name="axis">The axis name, for the refusal.</param>
    /// <exception cref="MotionException"><see cref="MotionError.OutOfRange"/> — the value does not fit a register pair.</exception>
    public static int ToRaw(double units, string what, string? axis = null)
    {
        var scaled = Math.Round(units * Scale, MidpointRounding.AwayFromZero);
        if (double.IsNaN(scaled) || scaled < int.MinValue || scaled > int.MaxValue)
            throw AxisErrors.Create(axis ?? what, MotionError.OutOfRange,
                $"{what} {units.ToString(System.Globalization.CultureInfo.InvariantCulture)} does not fit the protocol's "
                + $"int32 register pair ({int.MinValue / Scale}…{int.MaxValue / Scale}); refused, not clamped");
        return (int)scaled;
    }

    /// <summary>Register value → axis units (÷1000).</summary>
    public static double FromRaw(int raw) => raw / Scale;

    /// <summary>
    /// The successor of a <c>Heartbeat</c> or <c>CommandSeq</c> value: 1…65535, never 0 (65535 wraps to 1).
    /// 0 is the power-up value, so a beat of 0 is no change and a sequence of 0 is indistinguishable from "none".
    /// </summary>
    public static ushort NextNonZero(ushort current)
    {
        var next = (ushort)(current + 1);
        return next == 0 ? (ushort)1 : next;
    }
}
