using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>
/// Turns a driver <see cref="MotionException"/> into the checker's one-line shape (protocol § Errors rule 1): the class
/// and the MotionError stated once, in front, then the facts (review #33).
/// </summary>
internal static class CheckerText
{
    /// <summary><c>&lt;Class&gt;/&lt;MotionError&gt;: &lt;facts&gt;</c>.</summary>
    public static string Describe(MotionException ex) =>
        $"{MotionErrorClasses.Of(ex.Error)}/{ex.Error}: {Facts(ex)}";

    /// <summary>
    /// The driver's message without its leading <c>&lt;label&gt;: &lt;MotionError&gt;: </c> (the channel's label is the
    /// endpoint, which the facts already name) and with a doubled final full stop collapsed.
    /// </summary>
    public static string Facts(MotionException ex)
    {
        var text = ex.Message;
        var marker = $": {ex.Error}: ";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0) text = text[(at + marker.Length)..];
        else if (text.StartsWith($"{ex.Error}: ", StringComparison.Ordinal)) text = text[($"{ex.Error}: ").Length..];
        while (text.EndsWith("..", StringComparison.Ordinal) && !text.EndsWith("...", StringComparison.Ordinal)) text = text[..^1];
        return text;
    }
}
