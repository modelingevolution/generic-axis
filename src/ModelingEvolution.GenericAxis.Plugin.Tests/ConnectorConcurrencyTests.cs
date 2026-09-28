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

    [Fact]
    public async Task An_Axis_Waiting_For_A_Lease_Does_Not_Hold_Up_Another_Axis()
    {
        var leaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freeAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _connector.StartAttempt(_held, 1, _ => leaseWait.Task, CancellationToken.None).Should().BeTrue();
        _connector.StartAttempt(_free, 1, _ => { freeAttached.SetResult(); return Task.CompletedTask; },
            CancellationToken.None).Should().BeTrue();

        await freeAttached.Task.WaitAsync(Bound);
        await _connector.AttemptFor(_free)!.WaitAsync(Bound).ContinueWith(_ => { });
        _connector.AttemptFor(_held).Should().NotBeNull("the held axis is still waiting for its lease");

        leaseWait.SetResult();
        await _connector.AttemptFor(_held)!.WaitAsync(Bound).ContinueWith(_ => { });
    }

    [Fact]
    public async Task At_Most_One_Attempt_Is_In_Flight_Per_Device()
    {
        var leaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Func<CancellationToken, Task> connect = _ => { Interlocked.Increment(ref calls); return leaseWait.Task; };

        _connector.StartAttempt(_held, 1, connect, CancellationToken.None).Should().BeTrue();
        _connector.StartAttempt(_held, 1, connect, CancellationToken.None).Should().BeFalse();
        _connector.StartAttempt(_held, 1, connect, CancellationToken.None).Should().BeFalse();

        var attempt = _connector.AttemptFor(_held)!;
        leaseWait.SetResult();
        await attempt.WaitAsync(Bound);

        calls.Should().Be(1);
        await WaitUntilAsync(() => _connector.AttemptFor(_held) is null);
        _connector.StartAttempt(_held, 1, _ => Task.CompletedTask, CancellationToken.None)
            .Should().BeTrue("a finished attempt frees the device for the next tick");
    }

    [Fact]
    public async Task A_Failed_Attempt_Frees_The_Device_For_Its_Next_Attempt()
    {
        _connector.StartAttempt(_held, 1,
            _ => Task.FromException(new MotionException(MotionError.LeaseHeld, "carriage: held", "carriage")),
            CancellationToken.None).Should().BeTrue();

        await WaitUntilAsync(() => _connector.AttemptFor(_held) is null);
        _connector.IsDue(_held).Should().BeTrue();
    }

    [Fact]
    public async Task Shutdown_Cancels_And_Drains_Attempts_In_Flight_Without_Reporting_A_Failure()
    {
        var log = new ListLogger();
        var connector = new GenericAxisConnector(logger: log, timeProvider: new FakeTimeProvider());
        using var stopping = new CancellationTokenSource();

        connector.StartAttempt(_held, 1, ct => Task.Delay(Timeout.Infinite, ct), stopping.Token).Should().BeTrue();
        await stopping.CancelAsync();

        await connector.DrainAsync().WaitAsync(Bound);
        connector.AttemptFor(_held).Should().BeNull();
        log.Levels.Should().NotContain(l => l >= LogLevel.Warning);
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
