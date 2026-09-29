namespace ModelingEvolution.GenericAxis.TestApp;

/// <summary>
/// Raises the thread-pool minimum so blocking Modbus calls cannot starve the process.
/// </summary>
/// <remarks>
/// The simulator's FluentModbus server runs in synchronous mode (a client handler waits for the next scan) and the
/// driver's channel makes synchronous FluentModbus calls inside its async methods. Both hold pool threads while they
/// wait. With the default minimum (one thread per CPU) the pool injects new threads only about twice a second, so on a
/// small CI runner a 10 ms Modbus round trip was measured at 1.6 s (CHK-04 on one CPU) and a watchdog trip was seen
/// 2.56 s after the last beat (CHK-08 on GitHub Actions). Timing checks judge the PLC, so the tool must not add
/// that latency itself.
/// </remarks>
public static class ThreadPoolFloor
{
    public const int MinThreads = 64;

    public static void Ensure()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, MinThreads), Math.Max(io, MinThreads));
    }
}
