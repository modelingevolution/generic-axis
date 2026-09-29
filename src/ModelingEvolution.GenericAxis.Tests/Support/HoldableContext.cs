using System.Collections.Concurrent;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>A single-threaded context whose one thread can be held by the test.</summary>
/// <remarks>Stands in for a UI dispatcher or a test scheduler: one thread, FIFO, and the test can keep it busy.</remarks>
internal sealed class HoldableContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly ManualResetEventSlim _released = new(true);
    private readonly Thread _thread;

    public HoldableContext()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                _released.Wait();
                callback(state);
            }
        }) { IsBackground = true, Name = "caller context" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public void Hold() => _released.Reset();

    public void Release() => _released.Set();

    public void Dispose()
    {
        Release();
        _queue.CompleteAdding();
    }
}
