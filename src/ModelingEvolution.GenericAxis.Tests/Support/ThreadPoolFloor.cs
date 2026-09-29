using System.Runtime.CompilerServices;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>
/// Gives the code under test the thread pool production has (lead ruling on (f), option (ii), 2026-09-29).
/// <para>
/// The test host parks two thread-pool workers for the whole run: the xunit VS adapter's
/// <c>VsTestRunner.RunTestsInAssembly</c> (blocked in <c>WaitHandle.WaitOne</c>) and vstest's
/// <c>TcpClientExtensions.MessageLoopAsync</c> (a synchronous <c>Socket.Poll</c> loop). dotnet-stack captures taken during
/// every thread-pool stall on <c>taskset -c 9,17</c> showed exactly those two, and nothing of the driver or the fixture
/// (dev-log, eng-driver-2, 3421e21). The pool's default minimum is one worker per CPU, so on a 2-CPU runner the host
/// held all of them and every driver work item waited for thread injection (~500 ms steps): watchdog trips and false
/// timeouts that production never sees.
/// </para>
/// <para>
/// The floor is exactly <c>ProcessorCount + 2</c>: it returns the two parked workers, no more. It is not a mitigation
/// for code that blocks the pool: a driver regression that blocks threads still starves it (proved RED with the floor
/// under <c>taskset -c 0</c>: sync-over-async channel, heartbeat on the caller's context, verb gate on the caller's
/// context). Do not raise it.
/// </para>
/// </summary>
internal static class ThreadPoolFloor
{
    /// <summary>Workers the test host parks for the whole run.</summary>
    public const int HostParkedWorkers = 2;

    [ModuleInitializer]
    internal static void Apply()
    {
        ThreadPool.GetMinThreads(out _, out var io);
        ThreadPool.SetMinThreads(Environment.ProcessorCount + HostParkedWorkers, io);
    }
}
