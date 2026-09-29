using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace ModelingEvolution.GenericAxis.TestApp.Tests;

/// <summary>
/// GA-U-91 (review #32): an argument the UI mode does not own is a usage error, never a silent start of a simulated
/// PLC and the <c>/driver</c> UI. GA-U-92 (review #22): the UI binds localhost unless <c>--urls</c> widens it.
/// </summary>
public sealed class WebModeTests
{
    [Theory]
    [InlineData("--dump", "192.168.58.20")]
    [InlineData("192.168.58.20", "--dump")]
    [InlineData("127.0.0.1:5020", "--dump", "--watch")]
    [InlineData("--allow-motion")]
    [InlineData("--port", "5100")]
    public void GA_U_91_AnArgumentTheUiDoesNotOwn_IsAUsageError(params string[] args) =>
        WebMode.Validate(args).Should().NotBeNull($"'{string.Join(' ', args)}' must not start the UI and a simulator");

    [Theory]
    [InlineData]
    [InlineData("--urls", "http://0.0.0.0:5070")]
    [InlineData("--urls=http://127.0.0.1:5171")]
    [InlineData("--Simulator:Port=5100")]
    [InlineData("Simulator:Faults:SuppressAck=true")]
    [InlineData("--environment", "Development")]
    public void GA_U_91_TheUiOwnArguments_AreAccepted(params string[] args) =>
        WebMode.Validate(args).Should().BeNull();

    [Fact]
    public void GA_U_91_DumpWithoutCheck_NamesTheRightSpelling() =>
        WebMode.Validate(["--dump", "192.168.58.20"]).Should().Contain("--check <host>[:port] --dump");

    /// <summary>The deny path through Program.cs itself: exit 2 at once, and nothing started.</summary>
    [Fact]
    public async Task GA_U_91_ProcessWithDumpButNoCheck_ExitsTwoWithoutStartingTheUi()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--dump", "127.0.0.1" },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.Environment["Simulator__Port"] = "0"; // should it start after all, never on a shared port
        psi.Environment["urls"] = "http://127.0.0.1:0";
        using var process = Process.Start(psi)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var stderr = await process.StandardError.ReadToEndAsync();
        process.ExitCode.Should().Be(2, stderr);
        stderr.Should().Contain("--dump");
        (await process.StandardOutput.ReadToEndAsync()).Should().NotContain("Now listening");
    }

    [Fact]
    public void GA_U_92_TheUiDefaultsToLocalhost()
    {
        var empty = new ConfigurationBuilder().Build();
        empty.GetUiUrls().Should().Be("http://localhost:5070");
    }

    [Fact]
    public void GA_U_92_UrlsWidensIt()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://0.0.0.0:5070" })
            .Build();
        config.GetUiUrls().Should().Be("http://0.0.0.0:5070");
    }
}
