using System.Globalization;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// protocol § One-verb mode, Speed rounding (352314e, review #65), the ONE rounding of both modes: a percentage of
/// <c>MaxVelocity</c> becomes raw = round-half-away-from-zero(pct × MaxVelocity raw ÷ 100) — the rounding of
/// <see cref="Words.ToRaw"/> — and a raw 0 is refused, never floored. Used by <c>--command move --speed</c> and by
/// CHK-13…16; there is no other speed computation in the test app.
/// </summary>
public static class SpeedRounding
{
    /// <summary>The raw Velocity for <paramref name="percent"/> % of <paramref name="maxVelocityRaw"/>.</summary>
    public static int Raw(double percent, int maxVelocityRaw) =>
        (int)(Math.Round(maxVelocityRaw * percent / 100.0, MidpointRounding.AwayFromZero) + 0.0);

    /// <summary>The refusal of a raw 0, naming MaxVelocity, the percentage and the raw result.</summary>
    public static string Refusal(double percent, int maxVelocityRaw, string maxVelocityAt)
    {
        var pct = percent.ToString("0.######", CultureInfo.InvariantCulture);
        return $"speed {pct} % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero({pct} × {maxVelocityRaw} ÷ 100) = 0); "
               + $"refused, never floored. Read MaxVelocity ({maxVelocityAt}) = {maxVelocityRaw}.";
    }
}
