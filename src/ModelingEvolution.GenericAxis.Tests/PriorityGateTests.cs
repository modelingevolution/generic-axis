using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>test-scenarios.md § Unit — Priority gate (GA-U-13 … GA-U-15).</summary>
public class PriorityGateTests
{
    [Fact(DisplayName = "GA-U-13 Stop preempts queued traffic")]
    public async Task Release_StopQueuedLast_IsGrantedFirst()
    {
        using var gate = new PriorityGate(new FakeTimeProvider());
        var holder = await gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None);

        var move = gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None).AsTask();
        var beat = gate.AcquireAsync(ChannelPriority.Heartbeat, CancellationToken.None).AsTask();
        var stop = gate.AcquireAsync(ChannelPriority.Stop, CancellationToken.None).AsTask();

        holder.Dispose();
        (await stop.WaitAsync(TimeSpan.FromSeconds(5))).Should().NotBeNull();
        move.IsCompleted.Should().BeFalse("the stop, queued last, holds the channel");
        beat.IsCompleted.Should().BeFalse();

        (await stop).Dispose();
        (await move.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        (await beat.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact(DisplayName = "GA-U-14 Heartbeat deferral is bounded")]
    public async Task Release_HeartbeatWaitedPastBound_IsGrantedBeforeMove()
    {
        var time = new FakeTimeProvider();
        using var gate = new PriorityGate(time);
        var holder = await gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None);

        var beat = gate.AcquireAsync(ChannelPriority.Heartbeat, CancellationToken.None).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(100));
        var moveA = gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None).AsTask();

        // Under the bound the move stream wins.
        holder.Dispose();
        var a = await moveA.WaitAsync(TimeSpan.FromSeconds(5));
        beat.IsCompleted.Should().BeFalse("100 ms is inside the 200 ms deferral bound");

        var moveB = gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(100));
        a.Dispose();

        var granted = await beat.WaitAsync(TimeSpan.FromSeconds(5));
        moveB.IsCompleted.Should().BeFalse("the overdue beat was granted ahead of the queued move");
        granted.Dispose();
        (await moveB.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact(DisplayName = "GA-U-14 control: a beat under the bound yields to every move")]
    public async Task Release_HeartbeatUnderBound_YieldsToMove()
    {
        var time = new FakeTimeProvider();
        using var gate = new PriorityGate(time);
        var holder = await gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None);
        var beat = gate.AcquireAsync(ChannelPriority.Heartbeat, CancellationToken.None).AsTask();
        var move = gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(199));

        holder.Dispose();
        var granted = await move.WaitAsync(TimeSpan.FromSeconds(5));
        beat.IsCompleted.Should().BeFalse();
        granted.Dispose();
        (await beat.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact(DisplayName = "GA-U-15 Disposal fails stranded waiters")]
    public async Task Dispose_WaiterWithNonCancellableToken_FaultsObjectDisposed()
    {
        var gate = new PriorityGate(new FakeTimeProvider());
        _ = await gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None);
        var stranded = gate.AcquireAsync(ChannelPriority.Move, CancellationToken.None).AsTask();

        gate.Dispose();

        await stranded.Invoking(t => t.WaitAsync(TimeSpan.FromSeconds(5))).Should().ThrowAsync<ObjectDisposedException>();
    }
}
