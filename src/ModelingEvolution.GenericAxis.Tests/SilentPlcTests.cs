using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelingEvolution.GenericAxis.Tests.Support;
using RocketWelder.SDK.Devices.Motion;
using Xunit.Abstractions;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// Review #34: a PLC that accepts TCP and never answers. The channel's 500 ms I/O timeout is the only thing between a
/// silent PLC and a hung heartbeat/STOP lane, so it is pinned here (GA-U-86) and end to end (GA-I-25).
/// </summary>
[Collection(LiveModbusCollection.Name)]
[Trait("Category", "Integration")]
public class SilentPlcTests(ITestOutputHelper output)
{
    /// <summary>Accepts every connection and parks it: open, never read, never answered.</summary>
    private sealed class SilentListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _parked = [];
        private readonly Task _loop;

        public SilentListener()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true) _parked.Add(await _listener.AcceptTcpClientAsync());
                }
                catch (Exception)
                {
                    // stopped
                }
            });
        }

        public int Port { get; }

        public int Accepted => _parked.Count;

        public void Dispose()
        {
            _listener.Stop();
            while (_parked.TryTake(out var c)) c.Dispose();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }

    public static TheoryData<string> Operations => new() { "read", "write" };

    [Theory(DisplayName = "GA-U-86 A silent PLC times out each attempt in 450–700 ms and the call in ≤ 1.6 s")]
    [MemberData(nameof(Operations))]
    public async Task SilentPeer_EachAttemptBoundedByIoTimeout(string operation)
    {
        using var peer = new SilentListener();
        var logs = new FakeLogCollector();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(logs)));
        using var channel = new ModbusChannel("127.0.0.1", peer.Port, factory.CreateLogger("channel"), null, "carriage",
            RegisterMap.Default);

        var warningAt = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        var call = operation == "read"
            ? channel.ReadInputAsync(1, 100, 15, "read status block", ChannelPriority.Heartbeat)
            : channel.WriteRegistersAsync(1, 0, [1, 7], "Stop", ChannelPriority.Stop);
        var watch = Task.Run(async () =>
        {
            while (!logs.GetSnapshot().Any(r => r.Level == LogLevel.Warning)) await Task.Delay(1);
            warningAt = sw.Elapsed;
        });

        var ex = (await call.Awaiting(c => c.WaitAsync(LiveRig.T)).Should().ThrowAsync<MotionException>()).Which;
        var total = sw.Elapsed;
        await watch.WaitAsync(LiveRig.T);

        var first = warningAt;
        var second = total - warningAt - ModbusChannel.RetryPause;
        output.WriteLine($"{operation}: attempt 1 {first.TotalMilliseconds:F0} ms, attempt 2 {second.TotalMilliseconds:F0} ms, "
                         + $"call {total.TotalMilliseconds:F0} ms: {ex.Message}");
        ex.Error.Should().Be(MotionError.CommunicationLost);
        ex.Message.Should().Contain("failed twice (reconnected once)").And.Contain("timed out");
        first.TotalMilliseconds.Should().BeInRange(450, 700, "attempt 1 is bounded by the 500 ms I/O timeout");
        second.TotalMilliseconds.Should().BeInRange(450, 700, "attempt 2 (after reconnect) is bounded the same way");
        total.TotalMilliseconds.Should().BeLessThanOrEqualTo(1600);
        peer.Accepted.Should().Be(2, "one connection per attempt");
        logs.GetSnapshot().Count(r => r.Level == LogLevel.Warning).Should().Be(1);
    }
}
