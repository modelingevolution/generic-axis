using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>
/// GA-I-66: <c>--headless</c> stopped by SIGTERM prints, once and flushed, the cadence line the Python fixtures parse
/// (their regex, verbatim): the longest scan gap since the first client connected, and the scan interval.
/// </summary>
public sealed partial class HeadlessCadenceLineTests
{
    [GeneratedRegex(@"^simulator: max scan gap (\d+) ms since the first client connected \(scan interval (\d+) ms\)$", RegexOptions.Multiline)]
    internal static partial Regex CadenceLine();

    [Fact(Timeout = 120_000)]
    public async Task GA_I_66_SigtermPrintsTheCadenceLineOnce()
    {
        var port = FreePort();
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll");
        var workDir = Directory.CreateTempSubdirectory("ga-i-66-").FullName;
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--headless", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        psi.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";

        // stdout is read line by line as it arrives, so the test can wait for the child's own log of the accepted
        // client (an observed condition) before it sends SIGTERM; the full text is read after the process has exited.
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.Enqueue(e.Data);
            if (e.Data.Contains($"connected on port {port}", StringComparison.Ordinal)) accepted.TrySetResult();
        };
        process.Start();
        process.BeginOutputReadLine();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = await ConnectWhenListeningAsync(port, process);
            client.ReadHoldingRegisters<ushort>(1, 100, 15).ToArray().Should().HaveCount(15);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the child has counted the client

            using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!) kill.WaitForExit();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token); // also drains the redirected stdout to EOF
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var text = string.Join("\n", output);
        process.ExitCode.Should().Be(0, await stderr);
        var lines = CadenceLine().Matches(text);
        lines.Should().ContainSingle("printed once:\n" + text);
        long.Parse(lines[0].Groups[1].Value).Should().BeGreaterThanOrEqualTo(0);
        lines[0].Groups[2].Value.Should().Be("10", "the configured scan interval");
        Directory.Delete(workDir, recursive: true);
    }

    /// <summary>GA-I-66: stopped with no client ever connected, the process says "not measured", never "0 ms".</summary>
    [Fact(Timeout = 120_000)]
    public async Task GA_I_66_SigtermWithNoClientSaysNotMeasured()
    {
        var port = FreePort();
        var dll = Path.Combine(AppContext.BaseDirectory, "ModelingEvolution.GenericAxis.TestApp.dll");
        var workDir = Directory.CreateTempSubdirectory("ga-i-66b-").FullName;
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "--headless", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        psi.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await WhenListeningAsync(port, process); // observed without connecting: a connection would be a client
            using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!) kill.WaitForExit();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var output = await stdout;
        process.ExitCode.Should().Be(0, await stderr);
        output.Split('\n').Select(l => l.TrimEnd('\r')).Should().ContainSingle(l => l == "simulator: max scan gap not measured (no client connected)", output);
        CadenceLine().IsMatch(output).Should().BeFalse("no line the Python rule could read as a measurement");
        Directory.Delete(workDir, recursive: true);
    }

    /// <summary>Waits (bounded) until the port is listening, observed from the OS listener table without connecting a client.</summary>
    private static async Task WhenListeningAsync(int port, Process process)
    {
        var sw = Stopwatch.StartNew();
        while (!IPGlobalPropertiesListens(port))
        {
            if (process.HasExited) throw new InvalidOperationException("setup: the headless simulator exited");
            if (sw.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("setup: the headless simulator never listened");
            await Task.Delay(50);
        }
    }

    private static bool IPGlobalPropertiesListens(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<FluentModbus.ModbusTcpClient> ConnectWhenListeningAsync(int port, Process process)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited) throw new InvalidOperationException("setup: the headless simulator exited");
            var client = new FluentModbus.ModbusTcpClient { ConnectTimeout = 1000, ReadTimeout = 2000, WriteTimeout = 2000 };
            try
            {
                client.Connect(new IPEndPoint(IPAddress.Loopback, port), FluentModbus.ModbusEndianness.BigEndian);
                return client;
            }
            catch (Exception) when (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                client.Dispose();
                await Task.Delay(50); // polling for the listener, bounded; not a timing assertion
            }
        }
    }
}

/// <summary>GA-U-98: the cadence is measured from the first client connection; before it the line reports 0.</summary>
[Collection(LiveCollection.Name)]
public sealed class SimulatorServiceCadenceTests
{
    [Fact]
    public async Task GA_U_98_TheGapIsCountedFromTheFirstClientConnection()
    {
        using var host = new ModelingEvolution.GenericAxis.TestApp.Simulation.SimulatorHost(
            new ModelingEvolution.GenericAxis.TestApp.Simulation.SimulatedAxisOptions { Port = 0 }, bindAddress: IPAddress.Loopback);
        using var service = new ModelingEvolution.GenericAxis.TestApp.Simulation.SimulatorService(host,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelingEvolution.GenericAxis.TestApp.Simulation.SimulatorService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        service.MaxScanGapSinceFirstClient.Should().Be(TimeSpan.Zero, "no client has connected yet");
        service.CadenceLine.Should().Be("simulator: max scan gap not measured (no client connected)", "nothing was measured: not a 0 ms measurement");
        HeadlessCadenceLineTests.CadenceLine().IsMatch(service.CadenceLine).Should().BeFalse("the Python regex must not read it as a measurement");

        using var client = new FluentModbus.ModbusTcpClient();
        client.Connect(new IPEndPoint(IPAddress.Loopback, host.Port), FluentModbus.ModbusEndianness.BigEndian);
        client.ReadHoldingRegisters<ushort>(1, 100, 15).ToArray();
        await ModelingEvolution.GenericAxis.TestApp.Tests.Conformance.CheckerAgainstSimulatorTests.Until(
            () => service.MaxScanGapSinceFirstClient > TimeSpan.Zero, "a gap measured since the client connected");

        await service.StopAsync(CancellationToken.None);
        service.MaxScanGapSinceFirstClient.Should().BeGreaterThan(TimeSpan.Zero, "kept after the stop, for the exit line");
    }
}
