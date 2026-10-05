using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-U-149 (#48, protocol dcae45b): one number grammar for every --command numeric argument — the move target, the jog
/// velocity, --speed and --for. The probe list is the one agreed with the Python tool; every probe is tried in every slot.
/// </summary>
public sealed partial class NumberGrammarTests
{
    public static readonly string[] Accepted = ["+5", "5", "5.", ".5", "5e-1", "0.00005", "-12.5", "1E3"];

    public static readonly string[] Refused =
        [" 5", "5 ", "1_0", "0x5", "inf", "-inf", "nan", "NaN", "Infinity", "1e400", "fast", "", ".", "e5", "5e", "+-5", "1,5",
         "\u22125", "5,5", // U+2212 minus and the de-DE decimal comma: the grammar is culture-invariant ASCII
         "5\n", "\n5", "5\r\n", "5\r", // #71: no whitespace — a regex `$` would let a final "\n" through
         "\uFF15", "\u0661\u0660\u0660", "1\u0665", "\u096B"]; // Python #51: fullwidth 5, Arabic-Indic 100, mixed, Devanagari 5 — ASCII digits only

    /// <summary>Each numeric slot: the argument name the error names, and the command line around the probe.</summary>
    private static readonly (string Arg, Func<string, string[]> Line)[] Slots =
    [
        ("move", p => ["--command", "move", p, "plc", "--allow-motion"]),
        ("jog", p => ["--command", "jog", p, "plc", "--allow-motion"]),
        ("--speed", p => ["--command", "move", "100", "plc", "--allow-motion", "--speed", p]),
        ("--for", p => ["--command", "jog", "5", "plc", "--allow-motion", "--for", p]),
    ];

    public static TheoryData<string> AcceptedProbes() => [.. Accepted];

    public static TheoryData<string> RefusedProbes() => [.. Refused];

    [Theory]
    [MemberData(nameof(AcceptedProbes))]
    public void GA_U_149_ANumberByTheGrammarParsesInEverySlot(string probe)
    {
        foreach (var (arg, line) in Slots)
        {
            var (options, error) = CheckCommandLine.Parse(line(probe));
            error.Should().BeNull($"{arg} {probe}");
            options!.Command.Should().NotBeNull();
        }
    }

    [Theory]
    [MemberData(nameof(RefusedProbes))]
    public void GA_U_149_AnythingElseIsAUsageErrorNamingTheArgumentAndTheTextAsTyped(string probe)
    {
        foreach (var (arg, line) in Slots)
        {
            var (options, error) = CheckCommandLine.Parse(line(probe));
            options.Should().BeNull($"{arg} '{probe}'");
            error.Should().Be($"{arg} {probe}: not a number");
        }
    }

    /// <summary>
    /// The grammar itself — not the parser behind it — refuses every non-number but the out-of-range 1e400 (finiteness).
    /// .NET's TryParse also rejects Unicode digits, so without this pin the regex's ASCII-only <c>[0-9]</c> (vs
    /// <c>\d</c>, which matches Unicode Nd) would be unobserved (Python #51: its \d accepted "١٠٠").
    /// </summary>
    [Fact]
    public void GA_U_149_TheGrammarAloneRefusesEveryNonNumber()
    {
        Accepted.Should().OnlyContain(p => CheckCommandLine.NumberGrammar.IsMatch(p));
        Refused.Where(p => p != "1e400").Should().NotContain(p => CheckCommandLine.NumberGrammar.IsMatch(p),
            "the protocol's grammar is ASCII digits, sign, point and exponent only");
    }

    [GeneratedRegex(@"Every numeric argument \(the `move` target, the `jog` velocity,\s+`--speed`, `--for`\) matches `\[\+-\]\?` then decimal digits with an optional fraction \((?<frac>[^)]*)\) and an\s+optional exponent \((?<exp>[^)]*)\), with no (?<no>[^,]+(?:,[^,]+)*?), and its value is finite; anything else\s+is a usage error, `(?<msg>[^`]+)`")]
    private static partial Regex Sentence();

    [GeneratedRegex(@"`(?<example>[^`]+)`")]
    private static partial Regex Backticked();

    /// <summary>The probe list and the error shape are bound to the protocol's sentence (dcae45b).</summary>
    [Fact]
    public void GA_U_149_TheGrammarIsTheProtocolsSentence()
    {
        var m = Sentence().Match(Protocol());
        m.Success.Should().BeTrue("protocol.md § One-verb mode states the number grammar");
        m.Groups["msg"].Value.Should().Be("<arg> <text>: not a number");
        var examples = Backticked().Matches(m.Groups["frac"].Value + " " + m.Groups["exp"].Value).Select(x => x.Groups["example"].Value).ToList();
        examples.Should().BeEquivalentTo(["5", "5.", ".5", "0.00005", "5e-1"]);
        examples.Should().OnlyContain(e => CheckCommandLine.NumberGrammar.IsMatch(e), "every example of the sentence is a number by the grammar");
        Accepted.Should().Contain(examples.Where(e => e != "5").Concat(["5"]), "the probe list covers the protocol's examples");
        var excluded = Regex.Replace(m.Groups["no"].Value, @"\s+", " ");
        excluded.Should().Contain("whitespace").And.Contain("underscores").And.Contain("hex").And.Contain("`inf`").And.Contain("`nan`");
        Refused.Should().Contain([" 5", "1_0", "0x5", "inf", "nan", "1e400"], "each excluded class and the finiteness rule has a probe");
    }

    /// <summary>
    /// Through Program.cs: a refused number is a usage error — exit 2, the message on stderr, NO RESULT line, and the
    /// PLC never contacted (zero client writes; the simulator sees no connection).
    /// </summary>
    [Theory]
    [InlineData("move", " 5")]
    [InlineData("--speed", "1e400")]
    [InlineData("jog", "Infinity")]
    [InlineData("move", "5\n")]
    public async Task GA_U_149_ARefusedNumberExitsTwoWithNoResultLineAndNoContact(string arg, string probe)
    {
        using var sim = new LiveSimulator();
        var connected = 0;
        sim.Host.ClientConnected += () => Interlocked.Increment(ref connected);
        var line = Slots.Single(s => s.Arg == arg).Line(probe).Select(a => a == "plc" ? $"127.0.0.1:{sim.Port}" : a);
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll"));
        foreach (var a in line) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }

        process.ExitCode.Should().Be(2);
        (await stderr).Should().Contain($"error: {arg} {probe}: not a number");
        (await stdout).Should().NotContain("RESULT:", "a usage error prints no RESULT line");
        connected.Should().Be(0, "a usage error never contacts the PLC");
    }

    private static string Protocol()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "protocol.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("docs/protocol.md");
    }
}
