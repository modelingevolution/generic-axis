using System.Text.RegularExpressions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-U-146 (review #41): the one-verb mode is the protocol's text. Parses docs/protocol.md — the <c>--command</c> row of
/// § Command line and § One-verb mode — and binds the verbs, the verbs that need <c>--allow-motion</c>, and each
/// <c>RESULT</c> word with its exit code to the code the runner prints from. A word drifting on either side is red.
/// </summary>
public sealed partial class OneVerbProtocolParityTests
{
    [GeneratedRegex(@"^\|\s*`--command <verb> \[args\]`.*?\|\s*off\s*\|(?<meaning>[^|]+)\|")]
    private static partial Regex CommandRow();

    [GeneratedRegex(@"`(?<verb>[a-z]+)(?: [^`]*)?`")]
    private static partial Regex Verbs();

    [GeneratedRegex(@"`RESULT: (?<word>[A-Z]+)`\s+\(exit (?<exit>\d)")]
    private static partial Regex ResultWord();

    private static string Protocol()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "protocol.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("docs/protocol.md not found above the test binaries");
    }

    private static string OneVerbSection()
    {
        var text = Protocol();
        var start = text.IndexOf("**One-verb mode (`--command`)**", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "protocol.md has § One-verb mode");
        var end = text.IndexOf("### Rules for every run", start, StringComparison.Ordinal);
        return text[start..end];
    }

    private static (IReadOnlyList<string> All, IReadOnlyList<string> Motion) ProtocolVerbs()
    {
        var row = Protocol().Split('\n').Select(l => CommandRow().Match(l)).Single(m => m.Success).Groups["meaning"].Value;
        var colon = row.IndexOf("):", StringComparison.Ordinal);
        var needs = row.IndexOf(" need `--allow-motion`", StringComparison.Ordinal);
        var list = row[(colon + 2)..row.IndexOf(". ", colon, StringComparison.Ordinal)];
        var motionSentence = row[row.LastIndexOf(". ", needs, StringComparison.Ordinal)..needs];
        return ([.. Verbs().Matches(list).Select(m => m.Groups["verb"].Value)],
            [.. Verbs().Matches(motionSentence).Select(m => m.Groups["verb"].Value)]);
    }

    [Fact]
    public void GA_U_146_TheVerbsAreTheProtocols()
    {
        var (all, motion) = ProtocolVerbs();
        all.Should().NotBeEmpty();
        Enum.GetValues<Verb>().Select(v => new VerbRequest(v).Name).Should().BeEquivalentTo(all, "the --command row lists every verb, and only those");
        Enum.GetValues<Verb>().Where(v => new VerbRequest(v).Moves).Select(v => new VerbRequest(v).Name)
            .Should().BeEquivalentTo(motion, "exactly the protocol's motion verbs need --allow-motion");
    }

    [GeneratedRegex(@"`(?<body><pct> % of MaxVelocity[^`]*)`")]
    private static partial Regex RefusalBody();

    [GeneratedRegex(@"`(?<lead>Commander/UnreachableSpeed:\s+refused\s+before\s+writing\s+anything:\s+)<body>`")]
    private static partial Regex GuardLine();

    [GeneratedRegex(@"`needs CHK-n, which (?<word>[A-Z]+)`")]
    private static partial Regex NeedsPhrase();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>
    /// #66 (e49dd62): the raw-0 refusal body is the protocol's (the check's SKIP message), and the one-verb guard prints
    /// the protocol's lead before it.
    /// </summary>
    [Fact]
    public void GA_U_146_TheSpeedRefusalIsTheProtocolsLine()
    {
        var text = Protocol();
        var body = Spaces().Replace(RefusalBody().Match(text).Groups["body"].Value, " ");
        body.Should().NotBeEmpty("protocol.md states the refusal body once (Speed rounding)");
        var expected = body.Replace("<pct>", "1", StringComparison.Ordinal).Replace("<raw>", "45", StringComparison.Ordinal);
        SpeedRounding.Body(1, 45, "S+12 = input 12").Should().Be(expected);

        var lead = Spaces().Replace(GuardLine().Match(text).Groups["lead"].Value, " ");
        lead.Should().NotBeEmpty("protocol.md states the guard's form");
        SpeedRounding.Refusal(1, 45, "S+12 = input 12").Should().Be(lead + expected);
    }

    /// <summary>#66 (e60d73a): the dependant SKIP phrase of § Rules, Order.</summary>
    [Fact]
    public void GA_U_146_TheDependantPhraseIsTheProtocols()
    {
        var words = NeedsPhrase().Matches(Protocol()).Select(m => m.Groups["word"].Value).ToHashSet();
        words.Should().BeEquivalentTo(["FAILED", "SKIPPED"]);
        ConformanceRunner.Needs("CHK-n", CheckResultKind.Fail).Should().Be("needs CHK-n, which FAILED");
        ConformanceRunner.Needs("CHK-n", CheckResultKind.Skipped).Should().Be("needs CHK-n, which SKIPPED");
    }

    [Fact]
    public void GA_U_146_TheResultWordsAndExitCodesAreTheProtocols()
    {
        var pairs = ResultWord().Matches(OneVerbSection())
            .Select(m => (Exit: int.Parse(m.Groups["exit"].Value, System.Globalization.CultureInfo.InvariantCulture), Word: m.Groups["word"].Value))
            .Distinct().ToList();
        pairs.Should().HaveCount(5, "PASS, FAIL, GUARD, REFUSED, INTERRUPTED, each with its exit code");
        pairs.Select(p => p.Exit).Should().OnlyHaveUniqueItems();
        CommandRunner.ResultWords.Select(kv => (Exit: kv.Key, Word: kv.Value)).Should().BeEquivalentTo(pairs,
            "the runner prints exactly the protocol's last line for each exit code");
    }
}
