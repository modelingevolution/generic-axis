using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>
/// A device over <see cref="FakePlcChannel"/> on a <see cref="FakeTimeProvider"/>. The PLC starts as a healthy,
/// homed, standing axis publishing travel 0..10 000 and 500 unit/s (test values, not machine numbers).
/// Ticks happen only when the test calls <see cref="TickAsync"/>.
/// </summary>
internal sealed class DriverRig : IAsyncDisposable
{
    public const ushort Owner = 1;

    /// <summary>Real-time bound on any single wait in a test — generous; a hit is a hang, not a slow machine.</summary>
    public static readonly TimeSpan RealTimeout = TimeSpan.FromSeconds(10);

    public DriverRig(Func<GenericAxisOptions, GenericAxisOptions>? configure = null, AxisKind kind = AxisKind.Linear,
        RegisterMap? map = null)
    {
        Plc = new FakePlcChannel(map);
        Plc.MapVersion = RegisterMap.Version;
        Plc.SetLimits(0, 10_000_000, 500_000);
        Plc.State = 1;
        Plc.Flags = StatusFlags.Homed | StatusFlags.DriveReady | StatusFlags.InPosition;
        Plc.ActualPosition = 1_000_000;

        var options = new GenericAxisOptions
        {
            Name = kind == AxisKind.Linear ? "carriage" : "turntable",
            Host = "fake-plc",
            Map = map ?? RegisterMap.Default,
        };
        Options = configure?.Invoke(options) ?? options;

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(Logs)));

        Device = kind == AxisKind.Linear
            ? new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), Options, Owner, LoggerFactory, Time, (_, _) => Plc)
            : new ModbusPositioner(DeviceId.New("GenericPositioner"), Options, Owner, LoggerFactory, Time, (_, _) => Plc);
    }

    public FakePlcChannel Plc { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero));

    public FakeLogCollector Logs { get; } = new();

    public ILoggerFactory LoggerFactory { get; }

    public GenericAxisOptions Options { get; }

    public ModbusAxisDevice Device { get; }

    public ILinearAxis Linear => (ILinearAxis)Device.Axis;

    public IRotaryAxis Rotary => (IRotaryAxis)Device.Axis;

    public IMotionAxis Axis => Device.Axis;

    public async Task<DriverRig> ConnectAsync()
    {
        await Device.ConnectAsync().WaitAsync(RealTimeout);
        return this;
    }

    /// <summary>Advances fake time by one heartbeat interval and waits (real time) until that tick completed or
    /// failed, then lets continuations settle.</summary>
    public async Task TickAsync(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var before = Device.Heartbeat.TickCount;
            var opsBefore = Plc.OpCount;
            Time.Advance(Options.HeartbeatInterval);
            // A tick is done when TickCount moved (success) or when a failing op was recorded and the loop logged it.
            await Until(() => Device.Heartbeat.TickCount > before || (Plc.FailWhen is not null && Plc.OpCount > opsBefore));
            await Settle();
        }
    }

    /// <summary>Gives async continuations (engine waiters, verb bodies) time to run on the thread pool.</summary>
    public static async Task Settle()
    {
        for (var i = 0; i < 5; i++) await Task.Delay(5);
    }

    /// <summary>Polls a condition in real time.</summary>
    public static async Task Until(Func<bool> condition, string? because = null)
    {
        var deadline = DateTime.UtcNow + RealTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"condition not met within {RealTimeout.TotalSeconds} s {because}");
            await Task.Delay(2);
        }
    }

    /// <summary>A PLC that acks every command in the scan that enters its state (protocol § Handshake).</summary>
    public void BehaveLikePlc(Func<ushort, ushort?>? stateFor = null)
    {
        Plc.OnCommand = (plc, word, seq) =>
        {
            if (seq == plc.CommandAck) return;
            var bits = (CommandBits)word;
            ushort? next = stateFor?.Invoke(word) ?? DefaultNext(bits, plc.State);
            if (next is { } state) plc.State = state;
            plc.CommandAck = seq;
        };
    }

    private static ushort? DefaultNext(CommandBits bits, ushort state) =>
        (bits & CommandBits.Stop) != 0 ? (state is 2 or 3 or 4 ? (ushort)6 : state)
        : (bits & CommandBits.Reset) != 0 ? (state == 7 ? (ushort)0 : state)
        : (bits & CommandBits.Home) != 0 ? (ushort)2
        : (bits & CommandBits.MoveAbsolute) != 0 ? (ushort)3
        : (bits & CommandBits.MoveVelocity) != 0 ? (ushort)4
        : (bits & CommandBits.Enable) != 0 ? (state == 0 ? (ushort)1 : state)
        : state is 1 ? (ushort)0 : state;

    /// <summary>Bounds an act in real time, so a guard that stops refusing fails with a timeout instead of hanging
    /// on an ack wait nobody advances.</summary>
    public static Func<Task> Bounded(Func<Task> act) => () => act().WaitAsync(RealTimeout);

    public IReadOnlyList<FakeLogRecord> LogsAt(LogLevel level) =>
        Logs.GetSnapshot().Where(r => r.Level == level).ToArray();

    public async ValueTask DisposeAsync()
    {
        Device.Dispose();
        LoggerFactory.Dispose();
        await Task.CompletedTask;
    }
}
