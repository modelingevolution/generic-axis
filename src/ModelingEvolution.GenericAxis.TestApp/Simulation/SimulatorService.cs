using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>Runs the app's simulator for the lifetime of the host.</summary>
public sealed class SimulatorService(SimulatorHost host, ILogger<SimulatorService> logger) : IHostedService, IDisposable
{
    private SimulatorLoop? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = new SimulatorLoop(host, logger).Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _loop?.Dispose();
        _loop = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _loop?.Dispose();
}
