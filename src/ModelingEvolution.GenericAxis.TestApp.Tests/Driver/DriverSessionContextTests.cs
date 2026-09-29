using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Driver;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Driver;

/// <summary>
/// GA-U-99 (review #45 exposure on /driver): a verb runs off the caller's SynchronizationContext (the Blazor circuit's),
/// so a busy UI context cannot hold the driver channel's gate; the result still comes back to the caller's context.
/// </summary>
public sealed class DriverSessionContextTests
{
    [Fact]
    public async Task GA_U_99_AVerbRunsOffTheCallersContextAndReportsBackOnIt()
    {
        using var host = new SimulatorHost(new SimulatedAxisOptions { Port = 0 });
        var session = new DriverSession(host, NullLoggerFactory.Instance);
        var circuit = new RecordingContext();
        SynchronizationContext? verbContext = null;
        SynchronizationContext? changedContext = null;
        session.Changed += () => changedContext = SynchronizationContext.Current; // the last one: after the verb

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(circuit);
        Task run;
        try
        {
            run = session.Run("probe", _ =>
            {
                verbContext = SynchronizationContext.Current;
                return Task.CompletedTask;
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await run;
        verbContext.Should().NotBeSameAs(circuit, "the verb must not run on the circuit's context");
        changedContext.Should().BeSameAs(circuit, "the UI is told on its own context, as before");
        session.LastResult.Should().Be("probe: OK");
    }

    /// <summary>Runs posted work on the pool with itself as the current context (a stand-in for a Blazor circuit).</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ =>
        {
            SetSynchronizationContext(this);
            d(state);
        });
    }
}
