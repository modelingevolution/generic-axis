using System.Net;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests;

/// <summary>A real simulator on a free loopback port, scanned in real time every 10 ms.</summary>
internal sealed class LiveSimulator : IDisposable
{
    private readonly SimulatorLoop _loop;

    public LiveSimulator(SimulatedAxisOptions? options = null)
    {
        Host = new SimulatorHost((options ?? new SimulatedAxisOptions()) with { Port = 0 }, bindAddress: IPAddress.Loopback);
        _loop = new SimulatorLoop(Host).Start();
    }

    public SimulatorHost Host { get; }
    public int Port => Host.Port;
    public SimSnapshot Snapshot => Host.Snapshot();

    /// <summary>
    /// The snapshot a few scans from now. A client sees its write complete inside a scan, before that scan's snapshot is
    /// taken, so ground truth read right after a write can be one scan stale.
    /// </summary>
    public async Task<SimSnapshot> SettledAsync()
    {
        await Task.Delay(Host.Options.ScanInterval * 5);
        return Host.Snapshot();
    }

    public void Dispose()
    {
        _loop.Dispose();
        Host.Dispose();
    }
}

/// <summary>Live tests measure wall-clock timing: they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCollection
{
    public const string Name = "live";
}
