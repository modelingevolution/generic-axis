using System.Collections.Concurrent;
using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// The heartbeat is the commander's proof of life to the PLC watchdog (FR-11); it must not share a thread with
/// whoever called <c>ConnectAsync</c>. Under xunit that caller's context is a 2-thread scheduler on a 2-CPU runner,
/// shared by every running test; in rw2 it could be a Blazor circuit's dispatcher. Observed on taskset -c 0,1: an
/// 850 ms beat gap with the fixture on cadence, a false NotAcknowledged and a real watchdog trip mid-move.
/// </summary>
public class HeartbeatContextTests
{
    [Fact(DisplayName = "GA-U-129 The heartbeat keeps beating while the context that connected the device is busy")]
    public async Task Heartbeat_CallerContextHeld_KeepsBeating()
    {
        await using var rig = new DriverRig();
        using var caller = new HoldableContext();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        caller.Post(async _ =>
        {
            try
            {
                await rig.ConnectAsync();
                // Since c77a901 ConnectAsync reaches Start() on the pool (its awaits are ConfigureAwait(false)), which
                // would hide a loop that captures its starter's context. Start the loop from this thread directly: the
                // heartbeat's own contract is that its loop never runs on the context that started it.
                await rig.Device.Heartbeat.StopAsync();
                SynchronizationContext.Current.Should().BeSameAs(caller, "anchor: Start() runs on the caller's context");
                rig.Device.Heartbeat.Start();
                connected.SetResult();
            }
            catch (Exception ex)
            {
                connected.SetException(ex);
            }
        }, null);
        await connected.Task.WaitAsync(DriverRig.RealTimeout);

        caller.Hold();
        caller.Post(_ => { }, null); // the caller's thread is now busy elsewhere (held before running this)
        try
        {
            var ticks = rig.Device.Heartbeat.TickCount;
            await rig.TickAsync(3);
            rig.Device.Heartbeat.TickCount.Should().BeGreaterThanOrEqualTo(ticks + 3);
        }
        finally
        {
            caller.Release();
        }
    }
}
