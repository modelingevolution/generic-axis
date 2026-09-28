using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// Runs <see cref="SimulatorHost.Tick"/> in real time on a dedicated thread, every
/// <see cref="SimulatedAxisOptions.ScanInterval"/>, with the measured elapsed time as the scan's dt. A thrown scan is
/// logged and the loop continues. Used by <see cref="SimulatorService"/> and by the live test rigs.
/// </summary>
public sealed class SimulatorLoop : IDisposable
{
    private readonly SimulatorHost _host;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public SimulatorLoop(SimulatorHost host, ILogger? logger = null)
    {
        _host = host;
        _log = logger ?? NullLogger.Instance;
        _thread = new Thread(Run) { IsBackground = true, Name = "GenericAxis PLC scan" };
    }

    /// <summary>Starts the host (binds the port) and the scan thread.</summary>
    public SimulatorLoop Start()
    {
        _host.Start();
        _thread.Start();
        return this;
    }

    private void Run()
    {
        var interval = _host.Options.ScanInterval;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        var next = last + interval;
        _log.LogInformation("PLC scan loop running every {Interval} ms", interval.TotalMilliseconds);

        while (!_cts.IsCancellationRequested)
        {
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero && _cts.Token.WaitHandle.WaitOne(wait)) break;

            var now = clock.Elapsed;
            try
            {
                _host.Tick(now - last);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PLC scan threw; continuing");
            }

            last = now;
            next += interval;
            if (next < now) next = now + interval; // fell behind (debugger, GC): do not burst
        }

        _log.LogInformation("PLC scan loop stopped");
    }

    /// <summary>Stops the scan thread. Does not dispose the host.</summary>
    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
    }
}
