using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.GenericAxis.Tests.Support;
using Xunit.Abstractions;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// The channel never blocks its caller's thread while a frame is in flight (PR #6 / 8f10e06: sync-over-async FluentModbus
/// calls starved the pool into false NotAcknowledged on small hosts). A pool-size-independent guard: the thread-pool
/// floor (Support/ThreadPoolFloor) must not be able to mask this regression class.
/// </summary>
[Collection(LiveModbusCollection.Name)]
public class ChannelBlockingTests(ITestOutputHelper output)
{
    public static TheoryData<string> Operations => new() { "read", "write one", "write many" };

    [Theory(DisplayName = "GA-U-134 A frame the PLC has not answered yet never blocks the caller's thread")]
    [MemberData(nameof(Operations))]
    public async Task Frame_PlcNotAnsweredYet_CallReturnsPendingTaskAtOnce(string operation)
    {
        await using var plc = new MiniPlc();
        using var channel = new ModbusChannel("127.0.0.1", plc.Port, NullLogger.Instance, null, "carriage", RegisterMap.Default);
        await channel.ReadHoldingAsync(MiniPlc.Unit, 100, 15, "connect", ChannelPriority.Move).WaitAsync(LiveRig.T);

        Task call;
        TimeSpan returnedAfter;
        using (plc.HoldLock()) // the PLC cannot answer until released
        {
            var sw = Stopwatch.StartNew();
            call = operation switch
            {
                "read" => channel.ReadHoldingAsync(MiniPlc.Unit, 100, 15, "read status block", ChannelPriority.Heartbeat),
                "write one" => channel.WriteRegisterAsync(MiniPlc.Unit, 8, 7, "heartbeat", ChannelPriority.Heartbeat),
                _ => channel.WriteRegistersAsync(MiniPlc.Unit, 2, [0, 0, 0, 0, 0, 0], "parameters", ChannelPriority.Move),
            };
            returnedAfter = sw.Elapsed;
            call.IsCompleted.Should().BeFalse("the PLC has not answered");
            Thread.Sleep(50); // never await while holding a Monitor
        }

        await call.WaitAsync(LiveRig.T);
        output.WriteLine($"{operation}: the call returned its task after {returnedAfter.TotalMilliseconds:F1} ms");
        returnedAfter.Should().BeLessThan(TimeSpan.FromMilliseconds(100),
            "the caller gets a pending task; no thread waits for the PLC (a blocked call would return only at the 500 ms frame timeout)");
    }

    [Fact(DisplayName = "GA-U-135 A frame started on a busy caller context does not hold the gate against the heartbeat lane")]
    public async Task Frame_StartedOnHeldContext_HeartbeatLaneNotBlocked()
    {
        await using var plc = new MiniPlc();
        using var channel = new ModbusChannel("127.0.0.1", plc.Port, NullLogger.Instance, null, "carriage", RegisterMap.Default);
        await channel.ReadHoldingAsync(MiniPlc.Unit, 100, 15, "connect", ChannelPriority.Move).WaitAsync(LiveRig.T);
        using var ui = new HoldableContext();

        // The frame starts on the "UI" thread, which stays busy 1.5 s in the SAME work item (review #47): no continuation
        // of the frame can run on that thread before the busy period, however fast the PLC answers.
        var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            started.SetResult(channel.WriteRegistersAsync(MiniPlc.Unit, 2, [0, 0, 0, 0, 0, 0], "parameters",
                ChannelPriority.Move));
            Thread.Sleep(1500);
        }, null);
        var move = await started.Task.WaitAsync(LiveRig.T);

        var sw = Stopwatch.StartNew();
        await channel.WriteRegisterAsync(MiniPlc.Unit, 8, 7, "heartbeat", ChannelPriority.Heartbeat).WaitAsync(LiveRig.T);
        var beatAfter = sw.Elapsed;
        await move.WaitAsync(LiveRig.T);

        output.WriteLine($"heartbeat-lane frame completed after {beatAfter.TotalMilliseconds:F0} ms");
        beatAfter.Should().BeLessThan(TimeSpan.FromMilliseconds(300),
            "the move's frame resumes on the pool and releases the gate; it does not wait for the busy caller");
    }
}
