using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>Runs the app's simulator for the lifetime of the host.</summary>
public sealed class SimulatorService(SimulatorHost host, ILogger<SimulatorService> logger) : IHostedService, IDisposable
{
    private SimulatorLoop? _loop;
    private int _clientSeen;
    private TimeSpan _gapAtStop;

    /// <summary>
    /// The longest gap between two consecutive scans since the first Modbus client connected (start-up and JIT stalls
    /// before it do not count); zero while no client has connected. Kept after <see cref="StopAsync"/>.
    /// </summary>
    public TimeSpan MaxScanGapSinceFirstClient =>
        Volatile.Read(ref _clientSeen) == 0 ? TimeSpan.Zero : _loop?.MaxScanGap ?? _gapAtStop;

    /// <summary>
    /// The line <c>--headless</c> prints on exit (eng-python's pytest reads it; keep the text exact). With no client ever
    /// connected nothing was measured, and the line says so in a form their regex deliberately does not match (review #51:
    /// "0 ms" would read as a healthy measurement of an event that never happened).
    /// </summary>
    public string CadenceLine => Volatile.Read(ref _clientSeen) == 0
        ? "simulator: max scan gap not measured (no client connected)"
        : $"simulator: max scan gap {(long)MaxScanGapSinceFirstClient.TotalMilliseconds} ms since the first client connected "
          + $"(scan interval {(long)host.Options.ScanInterval.TotalMilliseconds} ms)";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before the loop opens the listener: a client accepted in between would never be counted, and the
        // exit line would say "not measured" although a client connected (GA-I-66 flake on the 2-vCPU runner).
        var loop = new SimulatorLoop(host, logger);
        _loop = loop;
        host.ClientConnected += OnClientConnected;
        loop.Start();
        return Task.CompletedTask;
    }

    private void OnClientConnected()
    {
        if (Interlocked.Exchange(ref _clientSeen, 1) == 0) _loop?.ResetMaxScanGap();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        host.ClientConnected -= OnClientConnected;
        var loop = _loop;
        if (loop is not null)
        {
            _gapAtStop = loop.MaxScanGap;
            _loop = null;
            loop.Dispose();
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _loop?.Dispose();
}
