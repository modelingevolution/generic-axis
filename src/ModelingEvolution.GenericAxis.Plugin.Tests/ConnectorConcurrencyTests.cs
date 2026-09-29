using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Plugin.Tests;

/// <summary>
/// Review #13: each device attaches on its own task, at most one attempt in flight per device, so an
/// axis waiting out a live commander's lease (up to its lease timeout, or unbounded) never holds up
/// another generic axis.
/// </summary>
public sealed class ConnectorConcurrencyTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private readonly GenericAxisConnector _connector =
        new(logger: NullLogger<GenericAxisConnector>.Instance, timeProvider: new FakeTimeProvider());

    private readonly DeviceId _held = DeviceId.New(GenericAxisPlugin.LinearTrackDeviceType);
    private readonly DeviceId _free = DeviceId.New(GenericAxisPlugin.PositionerDeviceType);

    // Review #28: every attempt is awaited through the Task StartAttempt returned. A finished attempt
    // removes itself from AttemptFor on the thread that finished it, so looking it up after its gate
    // is released races that removal (measured: null in ~7 % of 2000 iterations for the free axis).

    [Fact]
    public async Task An_Axis_Waiting_For_A_Lease_Does_Not_Hold_Up_Another_Axis()
    {
        var leaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freeAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var held = _connector.StartAttempt(_held, 1, _ => leaseWait.Task, CancellationToken.None);
        var free = _connector.StartAttempt(_free, 1, _ => { freeAttached.SetResult(); return Task.CompletedTask; },
            CancellationToken.None);
        held.Should().NotBeNull();
        free.Should().NotBeNull();

        // The free axis attaches to completion while the held one is still waiting for its lease.
        await free!.WaitAsync(Bound);
        freeAttached.Task.IsCompletedSuccessfully.Should().BeTrue("the free axis's connect ran");
        free.IsCompletedSuccessfully.Should().BeTrue();
        held!.IsCompleted.Should().BeFalse("the held axis is still waiting for its lease");
        _connector.AttemptFor(_held).Should().BeSameAs(held, "the held axis's attempt is still in flight");

        leaseWait.SetResult();
        await held.WaitAsync(Bound);
        held.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task At_Most_One_Attempt_Is_In_Flight_Per_Device()
    {
        var leaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<CancellationToken, Task> connect = _ => { Interlocked.Increment(ref calls); return leaseWait.Task; };

        var attempt = _connector.StartAttempt(_held, 1, connect, CancellationToken.None);
        attempt.Should().NotBeNull();
        _connector.StartAttempt(_held, 1, connect, CancellationToken.None).Should().BeNull();
        _connector.StartAttempt(_held, 1, connect, CancellationToken.None).Should().BeNull();
        _connector.AttemptFor(_held).Should().BeSameAs(attempt);

        leaseWait.SetResult();
        await attempt!.WaitAsync(Bound);

        calls.Should().Be(1);
        await WaitUntilAsync(() => _connector.AttemptFor(_held) is null);
        _connector.StartAttempt(_held, 1, _ => Task.CompletedTask, CancellationToken.None)
            .Should().NotBeNull("a finished attempt frees the device for the next tick");
    }

    [Fact]
    public async Task A_Failed_Attempt_Frees_The_Device_For_Its_Next_Attempt()
    {
        var attempt = _connector.StartAttempt(_held, 1,
            _ => Task.FromException(new MotionException(MotionError.LeaseHeld, "carriage: held", "carriage")),
            CancellationToken.None);
        attempt.Should().NotBeNull();

        // The failure is reported inside the attempt, so the attempt itself completes without faulting.
        await attempt!.WaitAsync(Bound);
        await WaitUntilAsync(() => _connector.AttemptFor(_held) is null);
        _connector.IsDue(_held).Should().BeTrue();
        _connector.StartAttempt(_held, 1, _ => Task.CompletedTask, CancellationToken.None)
            .Should().NotBeNull("a failed attempt frees the device");
    }

    [Fact]
    public async Task Shutdown_Cancels_And_Drains_Attempts_In_Flight_Without_Reporting_A_Failure()
    {
        var log = new ListLogger();
        var connector = new GenericAxisConnector(logger: log, timeProvider: new FakeTimeProvider());
        using var stopping = new CancellationTokenSource();

        var attempt = connector.StartAttempt(_held, 1, ct => Task.Delay(Timeout.Infinite, ct), stopping.Token);
        attempt.Should().NotBeNull();
        attempt!.IsCompleted.Should().BeFalse("the attempt is in flight until the host stops");
        await stopping.CancelAsync();

        await connector.DrainAsync().WaitAsync(Bound);
        attempt.IsCanceled.Should().BeTrue("drain returns only once the in-flight attempt has ended");
        await WaitUntilAsync(() => connector.AttemptFor(_held) is null);
        log.Levels.Should().NotContain(l => l >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_Device_Whose_Host_Query_Throws_Does_Not_Stop_The_Connector()
    {
        // Review #27: the per-device guard around reconcile. Both devices' queries throw; the loop must
        // reach the second one and keep running rather than fault ExecuteAsync.
        var query = new ThrowingDeviceQuery(expectedCalls: 2);
        var connector = new GenericAxisConnector(query, NullLogger<GenericAxisConnector>.Instance, new FakeTimeProvider());
        foreach (var info in new[] { PluginHarness.Track, PluginHarness.Positioner })
        {
            var id = DeviceId.New(info.DeviceType);
            var config = PluginHarness.ConfigFor(info, (GenericAxisConfigKey.For(info.Axes.Single().Name, "Host"), "192.0.2.1"));
            connector.Track(id, PluginHarness.Build(info, config, id));
        }

        await connector.StartAsync(CancellationToken.None);
        try
        {
            await query.Reached.Task.WaitAsync(Bound);
            connector.ExecuteTask!.IsCompleted.Should().BeFalse("one device's failure must not stop the connector");
        }
        finally
        {
            await connector.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ThrowingDeviceQuery(int expectedCalls) : RocketWelder.SDK.Automation.IDeviceQuery
    {
        private int _calls;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<DeviceId>? DeviceConfigChanged { add { } remove { } }

        public RocketWelder.SDK.Automation.DeviceSnapshot? GetById(DeviceId id)
        {
            if (Interlocked.Increment(ref _calls) >= expectedCalls) Reached.TrySetResult();
            throw new InvalidOperationException("read model unavailable");
        }

        public IEnumerable<RocketWelder.SDK.Automation.DeviceSnapshot> GetByInterface(string interfaceType) => [];
        public int GetNextNumber(string interfaceType) => 1;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached within the bound");
            await Task.Delay(5);
        }
    }

    private sealed class ListLogger : ILogger<GenericAxisConnector>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(level);
        }
    }
}
