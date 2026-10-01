using FluentAssertions;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// ADR-36 (protocol § Transport): the status block is input registers, read by FC04; the command block, C+9…C+11
/// included, is holding registers, read by FC03. Each of the driver's three status reads is pinned by name: the
/// attach read before any write, the tick, and the fresh read at the ack deadline.
/// </summary>
public class FunctionCodeTests
{
    [Fact(DisplayName = "GA-U-139 Every status-block read is FC04 and every command-block read FC03: attach, tick, ack deadline")]
    public async Task AttachTickAndDeadline_StatusReadsAreInput_CommandReadsAreHolding()
    {
        await using var rig = new DriverRig();
        await rig.ConnectAsync();
        var attach = rig.Plc.Ops;
        var firstWrite = attach.ToList().FindIndex(o => o.IsWrite);
        firstWrite.Should().BePositive("anchor: attach writes the lease");
        attach.Take(firstWrite).Should().Contain(o => o.IsInputRead && o.Address == rig.Plc.Map.Status
            && o.Count == RegisterMap.StatusLength, "the pre-write read (MapVersion, limits) is FC04");

        rig.BehaveLikePlc();
        rig.Plc.State = 0;
        await rig.TickAsync();
        // Ticks answer a block from before the write, so the verb needs the deadline read (GA-U-132's path).
        var stale = Enumerable.Range(0, RegisterMap.StatusLength)
            .Select(i => rig.Plc[(ushort)(rig.Plc.Map.Status + i)]).ToArray();
        var serveStale = true;
        rig.Plc.AnswerWith = op => serveStale && op.What == "read status block" ? stale : null;
        rig.Plc.OnRead = (_, op) => { if (op.What.EndsWith("at the ack deadline")) serveStale = false; };
        var power = rig.Axis.PowerAsync(true);
        for (var i = 0; i < 10 && !power.IsCompleted; i++) await rig.TickAsync();
        await power.WaitAsync(DriverRig.RealTimeout);

        var ops = rig.Plc.Ops;
        ops.Should().Contain(o => o.What == "read status block" && o.Lane == ChannelPriority.Heartbeat,
            "anchor: ticks ran");
        ops.Should().Contain(o => o.What.EndsWith("at the ack deadline"), "anchor: the deadline read ran");

        var reads = ops.Where(o => !o.IsWrite).ToList();
        reads.Where(o => o.Count == RegisterMap.StatusLength).Should().NotBeEmpty()
            .And.OnlyContain(o => o.IsInputRead, "a 15-register read is the status block");
        reads.Where(o => o.What.Contains("status block")).Should().HaveCountGreaterThanOrEqualTo(3)
            .And.OnlyContain(o => o.IsInputRead && o.Address == rig.Plc.Map.Status);
        reads.Where(o => o.Count < RegisterMap.StatusLength).Should().NotBeEmpty()
            .And.OnlyContain(o => o.IsHoldingRead && o.Address >= rig.Plc.Map.Heartbeat
                && o.Address + o.Count <= rig.Plc.Map.CommandBase + RegisterMap.CommandLength,
                "the driver reads only C+8…C+11 of the command block, by FC03");
    }
}
