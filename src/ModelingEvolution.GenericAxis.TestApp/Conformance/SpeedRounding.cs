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
    /// The one-verb guard's refusal of a raw 0 (protocol § One-verb mode step 3, e49dd62):
    /// <c>Commander/UnreachableSpeed: refused before writing anything: &lt;body&gt;</c>. A check SKIPs itself with
    /// <see cref="Body"/> alone, no class prefix.
    /// </summary>
    public static string Refusal(string percent, int maxVelocityRaw, string maxVelocityAt) =>
        $"{ErrorClass.Commander}/{RefusalName}: {RefusalLead}{Body(percent, maxVelocityRaw, maxVelocityAt)}";

    /// <summary>The SDK MotionError name of the refusal.</summary>
    public const string RefusalName = "UnreachableSpeed";

    /// <summary>The phrase every Commander refusal of the one-verb mode starts its text with.</summary>
    public const string RefusalLead = "refused before writing anything: ";

    /// <summary>
    /// The refusal body, exactly the protocol's (e49dd62): the percentage as given, printed in full and never rounded for
    /// display, the arithmetic, and the register read. Both modes: the check's SKIP message, and the guard after its lead.
    /// </summary>
    /// <param name="percent">The percentage as given — the <c>--speed</c> argument as typed, or the check's "10" / "1" —
    /// printed verbatim, never re-rendered from a number (#68: 1e-30 stays 1e-30, 33.3333333333333333 stays whole).</param>
    /// <param name="maxVelocityRaw">MaxVelocity as read, raw.</param>
    /// <param name="maxVelocityAt">MaxVelocity's address in the protocol's form, e.g. <c>S+12 = input 12</c>.</param>
    public static string Body(string percent, int maxVelocityRaw, string maxVelocityAt)
    {
        var pct = percent;
        return $"{pct} % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero({pct} × {maxVelocityRaw} ÷ 100) = 0); "
               + $"nothing to move with. Read MaxVelocity ({maxVelocityAt}) = {maxVelocityRaw}.";
    }
}
