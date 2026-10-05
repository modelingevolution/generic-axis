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

    /// <summary>
    /// The refusal of a raw 0, exactly the protocol's line (§ One-verb mode step 3, e60d73a), used by both modes: the
    /// one-verb guard prints it after "&lt;verb&gt;: ", and a check SKIPs itself with it (the <c>Commander/</c> prefix stays;
    /// errorClass stays null on the SKIP).
    /// </summary>
    public static string Refusal(double percent, int maxVelocityRaw, string maxVelocityAt) =>
        $"{ErrorClass.Commander}/{RefusalName}: {RefusalLead}{Body(percent, maxVelocityRaw, maxVelocityAt)}";

    /// <summary>The SDK MotionError name of the refusal.</summary>
    public const string RefusalName = "UnreachableSpeed";

    /// <summary>The phrase every Commander refusal of the one-verb mode starts its text with.</summary>
    public const string RefusalLead = "refused before writing anything: ";

    /// <summary>The refusal's text after the lead: the percentage, the arithmetic, and the register read.</summary>
    public static string Body(double percent, int maxVelocityRaw, string maxVelocityAt)
    {
        var pct = percent.ToString("0.######", CultureInfo.InvariantCulture);
        return $"{pct} % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero({pct} × {maxVelocityRaw} ÷ 100) = 0); "
               + $"nothing to move with. Read MaxVelocity ({maxVelocityAt}) = {maxVelocityRaw}.";
    }
}
