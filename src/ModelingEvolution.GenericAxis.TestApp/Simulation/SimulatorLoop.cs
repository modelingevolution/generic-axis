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
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastScanTicks;
    private long _maxGapTicks;

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
        ResetMaxScanGap();
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
                RecordScan();
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

    /// <summary>
    /// The longest time between two completed scans since the last <see cref="ResetMaxScanGap"/>, including the time
    /// since the last one (a scan stuck right now counts). Nominal: <see cref="SimulatedAxisOptions.ScanInterval"/>. A
    /// test reads it to tell a budget the PLC missed from a budget the starved fixture could not keep.
    /// </summary>
    public TimeSpan MaxScanGap
    {
        get
        {
            var sinceLast = _clock.Elapsed.Ticks - Interlocked.Read(ref _lastScanTicks);
            return TimeSpan.FromTicks(Math.Max(Interlocked.Read(ref _maxGapTicks), sinceLast));
        }
    }

    /// <summary>Forgets the gaps seen so far; the next gap is measured from now.</summary>
    public void ResetMaxScanGap()
    {
        Interlocked.Exchange(ref _lastScanTicks, _clock.Elapsed.Ticks);
        Interlocked.Exchange(ref _maxGapTicks, 0);
    }

    private void RecordScan()
    {
        var now = _clock.Elapsed.Ticks;
        var gap = now - Interlocked.Exchange(ref _lastScanTicks, now);
        long seen;
        while (gap > (seen = Interlocked.Read(ref _maxGapTicks)) && Interlocked.CompareExchange(ref _maxGapTicks, gap, seen) != seen) { }
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
