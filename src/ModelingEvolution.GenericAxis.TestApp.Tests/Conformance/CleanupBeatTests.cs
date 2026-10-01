using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using ModelingEvolution.GenericAxis.TestApp.Simulation;
using ModelingEvolution.GenericAxis.TestApp.Tests.Support;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>
/// GA-I-67 (review #52, protocol § Rules Cleanup: "The beat continues through cleanup and stops just before
/// LeaseOwner = 0"): slow frames during an interrupted run's cleanup must not leave a watchdog trip behind.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class CleanupBeatTests
{
    /// <summary>
    /// Every non-heartbeat frame is held 400 ms once the run is interrupted (a starved host). Beating until just before
    /// the release keeps the axis beaten between the slow frames; stopping the beat before the final read and the release
    /// would leave ~1.2 s unbeaten and a trip (State 7) behind.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_67_SlowFinalReadInCleanupLeavesNoTrip()
    {
        using var sim = new LiveSimulator();
        using var proxy = new FrameDelayProxy(sim.Port);
        using var cts = new CancellationTokenSource();

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = proxy.Port }, cts.Token, r =>
            {
                if (r.Running != "CHK-07" || cts.IsCancellationRequested) return;
                proxy.Delay = TimeSpan.FromMilliseconds(400);
                cts.Cancel();
            });

        report.ExitCode.Should().Be(4, string.Join("; ", report.Checks.Where(c => c.Result == CheckResultKind.Fail).Select(c => $"{c.Id}: {c.Message}")));
        proxy.Delayed.Should().BeGreaterThan(0, "setup: the cleanup met slow frames");
        report.Cleanup.Should().Contain(l => l.Contains("release lease"));
        var end = await sim.SettledAsync();
        end.State.Should().NotBe(SimAxisState.ErrorStop, "the beat ran until just before the release: no trip");
        end.WatchdogFault.Should().Be(0);
        end.WatchdogTrips.Should().Be(0);
        end.LeaseOwner.Should().Be(0);
    }

    /// <summary>
    /// The PLC acknowledges the cleanup's Stop only 900 ms late (while the axis homes). The beat runs through the wait,
    /// so the axis stops, is disabled and released without a trip.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task GA_I_67_LateStopAckInCleanupLeavesNoTrip()
    {
        using var sim = new LiveSimulator();
        using var cts = new CancellationTokenSource();
        Task? release = null;
        var armed = false;
        sim.Host.OnClientWrite = addresses =>
        {
            if (!armed || !addresses.Contains(SimRegisters.Command) || (sim.Host.Registers.Holding.Read(SimRegisters.Command) & (ushort)SimCommandBits.Stop) == 0) return;
            armed = false;
            sim.Host.Faults = sim.Host.Faults with { SuppressAck = true }; // the Stop is held unacknowledged…
            release = Task.Run(async () =>
            {
                await Task.Delay(900); // …for 900 ms (the stimulus), then accepted
                sim.Host.Faults = sim.Host.Faults with { SuppressAck = false };
            });
        };

        var report = await new ConformanceRunner(NullLoggerFactory.Instance).RunAsync(
            new CheckerOptions { Host = "127.0.0.1", Port = sim.Port, AllowMotion = true }, cts.Token, r =>
            {
                if (r.Running != "CHK-12" || cts.IsCancellationRequested) return;
                _ = Task.Run(async () =>
                {
                    await CheckerAgainstSimulatorTests.Until(() => sim.Snapshot.State == SimAxisState.Homing, "CHK-12 homes");
                    armed = true;
                    await cts.CancelAsync();
                });
            });
        release.Should().NotBeNull("setup: the cleanup sent a Stop");
        await release!;

        report.ExitCode.Should().Be(4, "interrupted");
        var end = await sim.SettledAsync();
        end.State.Should().Be(SimAxisState.Disabled, "stopped, disabled, no trip");
        end.Velocity.Should().Be(0);
        end.WatchdogFault.Should().Be(0);
        end.LeaseOwner.Should().Be(0);
    }
}
