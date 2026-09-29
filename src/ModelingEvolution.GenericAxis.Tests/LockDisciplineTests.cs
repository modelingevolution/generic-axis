using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Unit — review #9: the engine lock guards state only. The overlay clear by ResetAsync raises
/// StatusChanged after the lock is released, and no log line is written under the lock (GA-U-76, GA-U-77).
/// </summary>
public class LockDisciplineTests
{
    /// <summary>A logger that records, per log line, whether the engine lock was held by the logging thread.</summary>
    private sealed class LockProbe : ILoggerProvider, ILogger
    {
        public Func<bool>? Held { get; set; }

        public List<(string Message, bool UnderLock)> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var held = Held?.Invoke() ?? false;
            lock (Lines) Lines.Add((formatter(state, exception), held));
        }

        public void Dispose()
        {
        }
    }

    private static async Task LatchCommunicationLostAndAnswerAgain(DriverRig rig)
    {
        rig.Plc.FailWhen = op => op.IsWrite && op.Address == rig.Plc.Map.Heartbeat;
        await rig.TickAsync();
        rig.Plc.FailWhen = null;
        await rig.TickAsync();
        rig.Axis.Status.Error.Should().Be(MotionError.CommunicationLost, "anchor: the overlay is latched");
    }

    [Fact(DisplayName = "GA-U-76 Reset's overlay clear raises StatusChanged once, outside the lock, with the cleared status")]
    public async Task Reset_ClearsOverlay_OneStatusChangedOutsideLock()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        await LatchCommunicationLostAndAnswerAgain(rig);
        var engine = rig.Device.Engine;
        var events = new List<(AxisStatus Status, bool UnderLock)>();
        rig.Axis.StatusChanged += (_, status) => events.Add((status, engine.LockHeldByCurrentThread));

        await rig.Axis.ResetAsync().WaitAsync(DriverRig.RealTimeout); // no tick: the PLC is not in ErrorStop

        events.Should().ContainSingle("the overlay change is one status change").Which.Should().Match<(AxisStatus Status, bool UnderLock)>(e =>
            e.Status.Error == null && e.Status.State == AxisState.Standstill && !e.UnderLock);
        rig.LogsAt(LogLevel.Information).Should().Contain(r => r.Message == "carriage: CommunicationLost overlay cleared by ResetAsync");
    }

    [Fact(DisplayName = "GA-U-77 No log line is written under the engine lock (overlays, limits, reset)")]
    public async Task Lifecycle_NoLogUnderLock()
    {
        var probe = new LockProbe();
        await using var rig = new DriverRig(o => o with { ConfiguredTravelMin = 0, ConfiguredTravelMax = 9000, ConfiguredMaxVelocity = 400 },
            extraLogger: probe);
        probe.Held = () => rig.Device.Engine.LockHeldByCurrentThread;
        await rig.ConnectAsync();                           // attach: limit mismatch Warning
        rig.BehaveLikePlc();
        rig.Plc.SetLimits(0, 0, 0);                         // Configuration Information
        await rig.TickAsync();
        rig.Plc.SetLimits(0, 10_000_000, 500_000);          // back to PLC: mismatch Warning again
        await rig.TickAsync();
        await LatchCommunicationLostAndAnswerAgain(rig);    // overlay Error + "answers again"
        await rig.Axis.ResetAsync().WaitAsync(DriverRig.RealTimeout);

        List<(string Message, bool UnderLock)> lines;
        lock (probe.Lines) lines = [.. probe.Lines];
        lines.Should().Contain(l => l.Message.Contains("differ from the configured ones"), "anchor: the limit logs ran");
        lines.Should().Contain(l => l.Message.Contains("using the configured values"), "anchor");
        lines.Should().Contain(l => l.Message.Contains("overlay cleared by ResetAsync"), "anchor");
        lines.Where(l => l.UnderLock).Select(l => l.Message).Should().BeEmpty();
    }
}
