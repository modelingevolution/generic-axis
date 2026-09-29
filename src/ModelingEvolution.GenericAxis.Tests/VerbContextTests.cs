using System.Diagnostics;
using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;
using Xunit.Abstractions;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// Review #45: a verb called from a busy context must not hold the channel's gate while its continuation waits for
/// that context — the beat and the STOP lane queue behind the gate. The driver is a library: every await is
/// ConfigureAwait(false), so nothing it holds depends on the caller's thread.
/// </summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class VerbContextTests(ITestOutputHelper output)
{
    [TimingFact(DisplayName = "GA-U-131 A verb from a busy caller context stalls neither the beat nor STOP, and trips nothing")]
    public async Task Verb_CallerContextBusy_BeatAndStopUnaffected()
    {
        await using var rig = new LiveRig();
        var track = await rig.ConnectedTrack();
        await rig.Plc.WaitFor(t => t.WatchdogArmed, LiveRig.T, "armed");
        var tripsBefore = rig.Plc.Truth.WatchdogTrips;
        using var ui = new HoldableContext();

        // The "UI" issues PowerAsync and then renders for 1.5 s on the same thread.
        rig.Plc.ResetMaxScanGap();
        var sw = Stopwatch.StartNew();
        var powered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(async _ =>
        {
            try
            {
                await track.Carriage.PowerAsync(true);
                powered.SetResult();
            }
            catch (Exception ex)
            {
                powered.SetException(ex);
            }
        }, null);
        ui.Post(_ => Thread.Sleep(1500), null);

        // Beat gaps on the PLC's side, sampled off the busy thread, for the whole render.
        var lastBeat = rig.Plc.Truth.Heartbeat;
        var lastChange = sw.Elapsed;
        var maxBeatGap = TimeSpan.Zero;
        TimeSpan? stopReturned = null;
        var stop = Task.Run(async () =>
        {
            await Task.Delay(300);
            var s = Stopwatch.StartNew();
            await track.Carriage.StopAsync().WaitAsync(LiveRig.T);
            stopReturned = s.Elapsed;
        });
        while (sw.Elapsed < TimeSpan.FromMilliseconds(1800))
        {
            var beat = rig.Plc.Truth.Heartbeat;
            if (beat != lastBeat)
            {
                if (sw.Elapsed - lastChange > maxBeatGap) maxBeatGap = sw.Elapsed - lastChange;
                lastBeat = beat;
                lastChange = sw.Elapsed;
            }

            await Task.Delay(5);
        }

        if (sw.Elapsed - lastChange > maxBeatGap) maxBeatGap = sw.Elapsed - lastChange;
        var gap = rig.Plc.MaxScanGap;
        await stop.WaitAsync(LiveRig.T);
        // The STOP may cancel a PowerAsync still in flight; either ending is fine, only a hang is not.
        await powered.Task.WaitAsync(LiveRig.T).ContinueWith(_ => { }, TaskScheduler.Default);

        output.WriteLine($"GA-U-131 max beat gap {maxBeatGap.TotalMilliseconds:F0} ms, STOP returned after "
                         + $"{stopReturned?.TotalMilliseconds:F0} ms, fixture max scan gap {gap.TotalMilliseconds:F0} ms");
        rig.Plc.Truth.WatchdogTrips.Should().Be(tripsBefore, "the beat never stopped");
        Cadence.Budget(gap, () =>
        {
            maxBeatGap.Should().BeLessThan(TimeSpan.FromMilliseconds(300), "the beat does not wait for the caller's thread");
            stopReturned.Should().NotBeNull().And.BeLessThan(TimeSpan.FromMilliseconds(300), "STOP does not wait for it either");
        });
    }
}
