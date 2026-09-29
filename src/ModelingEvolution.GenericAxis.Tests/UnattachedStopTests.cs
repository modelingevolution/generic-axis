using FluentAssertions;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.Tests.Support;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// test-scenarios.md § Unit — review #12 / FR-5: the station STOP reaches every device; on one that is not attached
/// it writes nothing, throws nothing, and says so in one Information line (GA-U-82).
/// </summary>
public class UnattachedStopTests
{
    private static IReadOnlyList<string> NotAttachedLines(DriverRig rig) => rig.Logs.GetSnapshot()
        .Where(r => r.Message.Contains("not attached, nothing of ours is moving"))
        .Select(r => $"{r.Level}: {r.Message}").ToArray();

    [Fact(DisplayName = "GA-U-82 STOP on a never-attached device: no write, no throw, one Information line")]
    public async Task StopAll_NeverAttached_NoWriteNoThrowOneLine()
    {
        await using var rig = new DriverRig();

        await rig.Device.Awaiting(d => d.StopAllAsync().WaitAsync(DriverRig.RealTimeout)).Should().NotThrowAsync();

        rig.Plc.Ops.Should().BeEmpty("nothing is sent to a PLC we are not attached to");
        NotAttachedLines(rig).Should().Equal("Information: carriage: Stop not sent: not attached, nothing of ours is moving");
        rig.Logs.GetSnapshot().Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    [Fact(DisplayName = "GA-U-82 STOP after a clean disconnect: no write, no throw, one Information line")]
    public async Task StopAxis_AfterDisconnect_NoWriteNoThrowOneLine()
    {
        await using var rig = await new DriverRig().ConnectAsync();
        rig.BehaveLikePlc();
        var disconnect = rig.Device.DisconnectAsync();
        for (var i = 0; i < 50 && !disconnect.IsCompleted; i++) await rig.TickAsync();
        await disconnect.WaitAsync(DriverRig.RealTimeout);
        rig.Logs.Clear();
        var from = rig.Plc.OpCount;

        await rig.Axis.Awaiting(a => a.StopAsync().WaitAsync(DriverRig.RealTimeout)).Should().NotThrowAsync();

        rig.Plc.OpCount.Should().Be(from);
        NotAttachedLines(rig).Should().ContainSingle();
    }
}
