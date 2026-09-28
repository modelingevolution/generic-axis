using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Conformance;

/// <summary>One poll: the status block plus C+8…C+11 (heartbeat, lease, watchdog), read through the driver's codec.</summary>
public readonly record struct PlcView(StatusBlock Status, ushort Heartbeat, ushort LeaseOwner, ushort WatchdogFault, ushort WatchdogTrips)
{
    public ushort State => Status.State;
}

/// <summary>The outcome of waiting for a condition: the last view read, elapsed ms since the trigger, and whether it held.</summary>
public readonly record struct Wait(PlcView View, long ElapsedMs, bool Met);

/// <summary>A command write's handshake: the sequence, whether and when it was acknowledged, and the view that showed it.</summary>
public readonly record struct Ack(ushort Seq, bool Acked, long AckMs, PlcView View, long WrittenAt);

/// <summary>
/// Everything a check needs (design § Conformance checker): the driver's <see cref="ModbusChannel"/>, <see cref="RegisterMap"/>
/// and <see cref="Words"/> — never a second codec — plus the checker's own beat, poll, command handshake and cleanup
/// journal. Checks write raw registers: they must exercise what the driver would refuse.
/// </summary>
internal sealed class CheckContext : IAsyncDisposable
{
    public static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan AckTimeout = RegisterMap.AckTimeout;

    private readonly List<string> _cleanup = [];

    public CheckContext(CheckerOptions options, IModbusChannel channel, ILogger logger)
    {
        Options = options;
        Channel = channel;
        Logger = logger;
        Map = options.Map;
        Beater = new Beater(this, "checker");
        Commands = new CommandWriter(this);
    }

    public CheckerOptions Options { get; }
    public IModbusChannel Channel { get; }
    public ILogger Logger { get; }
    public RegisterMap Map { get; }
    public byte Unit => Options.Unit;
    public Beater Beater { get; }
    public CommandWriter Commands { get; }

    /// <summary>Limits read by CHK-03 (raw register values), for the motion checks.</summary>
    public (int TravelMin, int TravelMax, int MaxVelocity)? Limits { get; set; }

    /// <summary>The checker caused a watchdog trip, so cleanup clears <c>WatchdogFault</c>.</summary>
    public bool CausedTrip { get; set; }

    /// <summary>The checker wrote <c>LeaseOwner</c> with its own id at least once.</summary>
    public bool TookLease { get; set; }

    public IReadOnlyList<string> CleanupLog => _cleanup;

    public static long Now() => Stopwatch.GetTimestamp();

    public static long MsSince(long timestamp) => (long)Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    // ---- raw register access (the driver's channel, lane Move) --------------------------------------------------

    public async Task<StatusBlock> ReadStatusAsync(CancellationToken ct) =>
        StatusBlock.Parse(await Channel.ReadHoldingAsync(Unit, Map.Status, RegisterMap.StatusLength, "read status block",
            ChannelPriority.Move, ct));

    public async Task<PlcView> ReadViewAsync(CancellationToken ct)
    {
        var watchdog = await Channel.ReadHoldingAsync(Unit, Map.Heartbeat, 4, "read heartbeat, lease and watchdog",
            ChannelPriority.Move, ct);
        var status = await ReadStatusAsync(ct);
        return new PlcView(status, watchdog[0], watchdog[1], watchdog[2], watchdog[3]);
    }

    public Task WriteAsync(ushort address, ushort value, string what, CancellationToken ct) =>
        Channel.WriteRegisterAsync(Unit, address, value, what, ChannelPriority.Move, ct);

    public Task WriteAsync(ushort address, ushort[] values, string what, CancellationToken ct) =>
        Channel.WriteRegistersAsync(Unit, address, values, what, ChannelPriority.Move, ct);

    public async Task TakeLeaseAsync(CancellationToken ct)
    {
        await WriteAsync(Map.LeaseOwner, Options.OwnerId, "take lease (checker id)", ct);
        TookLease = true;
    }

    public Task ReleaseLeaseAsync(CancellationToken ct) => WriteAsync(Map.LeaseOwner, 0, "release lease", ct);

    public Task ClearWatchdogFaultAsync(CancellationToken ct) => WriteAsync(Map.WatchdogFault, 0, "clear WatchdogFault", ct);

    /// <summary>
    /// Polls every 20 ms until <paramref name="until"/> holds or <paramref name="timeout"/> passes, measuring from
    /// <paramref name="since"/> (the completion of the triggering write) to the read that shows the effect.
    /// </summary>
    public async Task<Wait> WaitForAsync(Func<PlcView, bool> until, TimeSpan timeout, long since, CancellationToken ct)
    {
        var next = Now();
        while (true)
        {
            var view = await ReadViewAsync(ct);
            var elapsed = MsSince(since);
            if (until(view)) return new Wait(view, elapsed, true);
            if (elapsed >= timeout.TotalMilliseconds) return new Wait(view, elapsed, false);

            next += (long)(PollPeriod.TotalSeconds * Stopwatch.Frequency);
            var delay = Stopwatch.GetElapsedTime(Now(), next);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            else next = Now();
        }
    }

    /// <summary>Polls for <paramref name="duration"/> and reports whether <paramref name="never"/> ever held (and the first view where it did).</summary>
    public async Task<(bool Held, PlcView View)> WatchAsync(Func<PlcView, bool> never, TimeSpan duration, CancellationToken ct)
    {
        var since = Now();
        var w = await WaitForAsync(never, duration, since, ct);
        return (w.Met, w.View);
    }

    // ---- cleanup journal (protocol § Rules for every run) -------------------------------------------------------

    public void Journal(string entry)
    {
        _cleanup.Add(entry);
        Logger.LogInformation("Cleanup: {Entry}", entry);
    }

    public async ValueTask DisposeAsync()
    {
        await Beater.StopAsync();
        Channel.Dispose();
    }
}

/// <summary>
/// The checker's own beat: writes <c>Heartbeat</c> every 100 ms (1…65535, never 0) from its own loop on the heartbeat
/// lane, and records when each write completed.
/// </summary>
internal sealed class Beater(CheckContext ctx, string name)
{
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(100);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ushort _value;
    private long _lastBeatAt;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Stopwatch timestamp of the completion of the last beat write.</summary>
    public long LastBeatAt => Interlocked.Read(ref _lastBeatAt);

    public string Name => name;

    public async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        _value = (await ctx.Channel.ReadHoldingAsync(ctx.Unit, ctx.Map.Heartbeat, 1, "read heartbeat",
                ChannelPriority.Heartbeat, ct))[0];
        _cts = new CancellationTokenSource();
        await BeatOnceAsync(ct); // the first beat is on the wire when Start returns
        _loop = RunAsync(_cts.Token);
        ctx.Logger.LogDebug("Beat started ({Name})", name);
    }

    private async Task BeatOnceAsync(CancellationToken ct)
    {
        _value = Words.NextNonZero(_value);
        await ctx.Channel.WriteRegisterAsync(ctx.Unit, ctx.Map.Heartbeat, _value, "heartbeat", ChannelPriority.Heartbeat, ct);
        Interlocked.Exchange(ref _lastBeatAt, CheckContext.Now());
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await BeatOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning("Beat write failed ({Name}): {Message}", name, ex.Message);
            }
        }
    }

    /// <summary>Stops beating; when this returns no further beat will be written and <see cref="LastBeatAt"/> is final.</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        if (cts is null || loop is null) return;
        await cts.CancelAsync();
        try { await loop; }
        catch (OperationCanceledException) { }
        cts.Dispose();
        _cts = null;
        _loop = null;
        ctx.Logger.LogDebug("Beat stopped ({Name})", name);
    }
}

/// <summary>
/// The protocol handshake, raw: parameters in one FC16 (C+2…C+7), then <c>Command</c> + <c>CommandSeq</c> in a second
/// FC16, wait ≤ 500 ms for <c>CommandAck == CommandSeq</c>, then clear edge bits with the same sequence.
/// The first sequence continues from the PLC's <c>CommandAck</c>.
/// </summary>
internal sealed class CommandWriter(CheckContext ctx)
{
    private ushort? _seq;

    /// <summary>The checker has written <c>Command</c> at least once, so cleanup must return it to 0.</summary>
    public bool Used { get; private set; }

    public async Task WriteParametersAsync(int target, int velocity, int acceleration, CancellationToken ct)
    {
        var words = new ushort[RegisterMap.ParametersLength];
        Words.Write(words.AsSpan(0), target);
        Words.Write(words.AsSpan(2), velocity);
        Words.Write(words.AsSpan(4), acceleration);
        await ctx.WriteAsync(ctx.Map.Parameters, words, "parameters", ct);
    }

    public async Task<Ack> SendAsync(CommandBits bits, CancellationToken ct, ChannelPriority lane = ChannelPriority.Move)
    {
        if (_seq is null) _seq = (await ctx.ReadStatusAsync(ct)).CommandAck;
        var seq = Words.NextNonZero(_seq.Value);
        _seq = seq;

        Used = true;
        await ctx.Channel.WriteRegistersAsync(ctx.Unit, ctx.Map.Command, [(ushort)bits, seq], $"command {bits}", lane, ct);
        var since = CheckContext.Now();
        ctx.Logger.LogInformation("Command {Bits} (0x{Raw:X4}) CommandSeq {Seq}", bits, (ushort)bits, seq);

        var wait = await ctx.WaitForAsync(v => v.Status.CommandAck == seq, CheckContext.AckTimeout, since, ct);

        var level = bits & CommandBits.Enable;
        if (level != bits)
            await ctx.Channel.WriteRegistersAsync(ctx.Unit, ctx.Map.Command, [(ushort)level, seq], "clear edge bits", lane, ct);

        return new Ack(seq, wait.Met, wait.ElapsedMs, wait.View, since);
    }
}
