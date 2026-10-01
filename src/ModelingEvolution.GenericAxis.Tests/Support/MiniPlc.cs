using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentModbus;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>Test values for <see cref="MiniPlc"/> — not machine numbers (ADR-21).</summary>
internal sealed record MiniPlcOptions
{
    public int CommandBase { get; init; }
    public int StatusBase { get; init; }
    public double TravelMin { get; init; }
    public double TravelMax { get; init; } = 10_000;
    public double MaxVelocity { get; init; } = 500;
    public double Acceleration { get; init; } = 2_000;
    public double QuickStopDeceleration { get; init; } = 10_000;
    public double HomingVelocity { get; init; } = 500;
    public double HomeSensorPosition { get; init; }
    public double HomeSensorWidth { get; init; } = 5;
    public double LimitSwitchMargin { get; init; } = 20;
    public double InPositionWindow { get; init; } = 0.005;
    public double InitialPosition { get; init; } = 500;
    public bool HomedAtPowerUp { get; init; } = true;
    public bool PublishLimits { get; init; } = true;
    public ushort MapVersion { get; init; } = 1;
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromMilliseconds(10);
}

/// <summary>What the fixture's PLC looks like at one scan — the tests' ground truth.</summary>
internal sealed record PlcTruth(
    long At,
    double Position,
    double Velocity,
    ushort State,
    StatusFlags Flags,
    ushort FaultCode,
    ushort CommandSeq,
    ushort CommandAck,
    ushort Heartbeat,
    bool WatchdogArmed,
    ushort WatchdogFault,
    ushort WatchdogTrips,
    ushort LeaseOwner,
    bool Homed,
    int HomeCommandsAccepted);

/// <summary>
/// A minimal PLC implementing <c>docs/protocol.md</c> map version 1 behind a real FluentModbus
/// <see cref="ModbusTcpServer"/> on a loopback port — the driver-side integration target until the test app's
/// simulator is published (team-lead instruction, epic-065 f7). It is written from protocol.md alone and shares
/// no code with the driver's codec or register map: a misreading on one side is not mirrored into the other.
///
/// <para>
/// One scan every <see cref="MiniPlcOptions.ScanInterval"/> on its own thread, under the server's lock (requests are
/// served between scans, never inside one): read the command block (holding registers), run the FR-11
/// watchdog, accept a new CommandSeq and ack it in the same scan that enters the state, integrate the motion, and
/// publish the status block (input registers, FC04, ADR-36) from that one image.
/// </para>
/// </summary>
internal sealed class MiniPlc : IAsyncDisposable
{
    private const ushort Enable = 1, Home = 2, MoveAbs = 4, MoveVel = 8, Stop = 16, Reset = 32;

    // Asynchronous mode: each request is served at once under the server's Lock, which the scan also holds, so a
    // request still lands between scans. Synchronous mode was tried first: there one failed response write (a
    // connection the fixture dropped on purpose) ends FluentModbus's whole processing loop and the "PLC" never
    // answers again, which is a fixture artefact, not a PLC behaviour.
    private readonly ModbusTcpServer _server = new(isAsynchronous: true);
    private readonly GatedProvider _provider;
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly ConcurrentDictionary<int, byte> _published = new();
    private readonly ConcurrentBag<int> _writtenRegisters = [];
    private readonly ConcurrentQueue<(Func<PlcTruth, bool> Predicate, TaskCompletionSource<PlcTruth> Done)> _waiters = new();
    private volatile PlcTruth _truth = null!;

    // PLC program state (scan thread only).
    private double _p, _v, _target, _vcmd, _accel, _moveStart;
    private ushort _state, _fault, _ack, _lastBeat;
    private bool _homed, _inPosition, _armed, _needFreshEnable;
    private long _lastBeatChange;
    private ushort _trips;
    private int _homeCommands;

    // Scan cadence (adr.md, lead ruling "Two-vCPU flakes"): written by the scan thread, reset/read by the test.
    private long _lastScanAt, _maxScanGapTicks;

    public MiniPlc(MiniPlcOptions? options = null)
    {
        Options = options ?? new MiniPlcOptions();
        _p = Options.InitialPosition;
        _homed = Options.HomedAtPowerUp;
        _provider = new GatedProvider();
        _server.EnableRaisingEvents = true;
        _server.AlwaysRaiseChangedEvent = true;
        _server.RegistersChanged += (_, e) =>
        {
            foreach (var r in e.Registers) _writtenRegisters.Add(r);
        };
        _server.AddUnit(Unit);
        _server.Start(_provider);
        lock (_server.Lock) Publish(Stopwatch.GetTimestamp());
        _lastScanAt = Stopwatch.GetTimestamp();
        _thread = new Thread(Run) { IsBackground = true, Name = "MiniPlc scan" };
        _thread.Start();
    }

    public MiniPlcOptions Options { get; }

    /// <summary>The Modbus unit the PLC serves (protocol default 1).</summary>
    public const byte Unit = 1;

    public int Port => _provider.Port;

    public MiniPlcFaults Faults { get; } = new();

    public PlcTruth Truth => _truth;

    /// <summary>Every holding register address a client wrote since construction.</summary>
    public IReadOnlyCollection<int> WrittenRegisters => _writtenRegisters;

    /// <summary>Whether this raw ActualPosition was ever published.</summary>
    public bool WasPublished(int rawPosition) => _published.ContainsKey(rawPosition);

    /// <summary>Drops every connection and refuses new ones while down; registers untouched.</summary>
    public void SetCommunicationDown(bool down) => _provider.SetDown(down);

    /// <summary>
    /// A PLC that accepts TCP and never answers (review #34): setting it drops the established connections, and every
    /// connection accepted while silent is parked, never served; clearing it drops the parked ones. Mirrors the
    /// test app's <c>SimFaults.Silent</c>.
    /// </summary>
    public void SetSilent(bool silent) => _provider.SetSilent(silent);

    /// <summary>Closes the next <paramref name="count"/> accepted connections straight after accepting them.</summary>
    public void DropNextConnections(int count) => _provider.DropNext(count);

    /// <summary>TCP connections accepted (dropped ones included) since construction.</summary>
    public int AcceptedConnections => _provider.Accepted;

    /// <summary>Moves the axis instantly (a hand crank on the bench).</summary>
    public void Teleport(double position) => _actions.Enqueue(() => { _p = position; _v = 0; });

    /// <summary>Runs an action on the scan thread before the next scan.</summary>
    public void OnScan(Action action) => _actions.Enqueue(action);

    /// <summary>
    /// The longest gap between two consecutive scans since <see cref="ResetMaxScanGap"/>, including the gap still open
    /// now (a scan thread starved at the moment of reading counts). Nominal is one <see cref="MiniPlcOptions.ScanInterval"/>;
    /// a timing test reads it at the end of its measured window to tell a starved fixture from a slow driver
    /// (<see cref="Cadence"/>).
    /// </summary>
    public TimeSpan MaxScanGap
    {
        get
        {
            var open = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastScanAt);
            return Stopwatch.GetElapsedTime(0, Math.Max(Volatile.Read(ref _maxScanGapTicks), open));
        }
    }

    /// <summary>Test hook: holds the server's lock (as a long request would) until disposed.</summary>
    internal IDisposable HoldLock()
    {
        Monitor.Enter(_server.Lock);
        return new Releaser(_server.Lock);
    }

    private sealed class Releaser(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    /// <summary>Starts a measured window: forgets every scan gap recorded so far.</summary>
    public void ResetMaxScanGap() => Volatile.Write(ref _maxScanGapTicks, 0);

    private void RecordScan(long at)
    {
        var gap = at - Interlocked.Exchange(ref _lastScanAt, at);
        long seen;
        while (gap > (seen = Volatile.Read(ref _maxScanGapTicks))
               && Interlocked.CompareExchange(ref _maxScanGapTicks, gap, seen) != seen)
        {
        }
    }

    /// <summary>Waits until a scan that started after this call has published its truth — requests are served
    /// between scans, so a write awaited before this call is visible in <see cref="Truth"/> after it.</summary>
    public async Task NextScanAsync()
    {
        var mark = Stopwatch.GetTimestamp();
        await WaitFor(t => t.At > mark, TimeSpan.FromSeconds(2), "a scan after the mark");
    }

    /// <summary>
    /// The first scan, at or after this call, whose published truth satisfies <paramref name="predicate"/>, evaluated
    /// on the scan thread. Its <see cref="PlcTruth.At"/> is when the fixture did it — a budget measured on it does not
    /// include the test's own (possibly starved) polling latency.
    /// </summary>
    public async Task<PlcTruth> WhenScan(Func<PlcTruth, bool> predicate, TimeSpan timeout, string because)
    {
        var waiter = (predicate, new TaskCompletionSource<PlcTruth>(TaskCreationOptions.RunContinuationsAsynchronously));
        _waiters.Enqueue(waiter);
        try
        {
            return await waiter.Item2.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"PLC truth never satisfied '{because}' within {timeout}: {Truth}");
        }
    }

    public async Task WaitFor(Func<PlcTruth, bool> predicate, TimeSpan timeout, string because)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate(Truth))
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException($"PLC truth never satisfied '{because}' within {timeout}: {Truth}");
            await Task.Delay(2);
        }
    }

    private void Run()
    {
        var period = Options.ScanInterval;
        var last = Stopwatch.GetTimestamp();
        while (!_stop.IsCancellationRequested)
        {
            Thread.Sleep(period);
            try
            {
                // One scan under the server's lock: requests land between scans, and a status read is served
                // from one scan's image (protocol § Transport, Consistency).
                lock (_server.Lock)
                {
                    // Stamped once the lock is held (review #44): a request served while the scan waited for the
                    // lock is applied in this scan, so the scan's time (PlcTruth.At, dt, the watchdog) is after it.
                    var now = Stopwatch.GetTimestamp();
                    var dt = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
                    last = now;
                    RecordScan(now);
                    while (_actions.TryDequeue(out var action)) action();
                    Scan(dt, now);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // ── registers (big-endian on the wire; FluentModbus stores the wire bytes) ──

    private ushort Get(int address) => BinaryPrimitives.ReverseEndianness((ushort)_server.GetHoldingRegisters(Unit)[address]);

    private void Set(int address, ushort value) =>
        _server.GetHoldingRegisters(Unit)[address] = (short)BinaryPrimitives.ReverseEndianness(value);

    private int GetInt(int address) => (int)((uint)Get(address) | ((uint)Get(address + 1) << 16));

    // The status block is input registers (protocol § Transport, ADR-36): FC04 only, so no client can write it.
    private void SetInput(int address, ushort value) =>
        _server.GetInputRegisters(Unit)[address] = (short)BinaryPrimitives.ReverseEndianness(value);

    private void SetInputInt(int address, int value)
    {
        SetInput(address, (ushort)((uint)value & 0xFFFF));
        SetInput(address + 1, (ushort)((uint)value >> 16));
    }

    private void SetInt(int address, int value)
    {
        Set(address, (ushort)((uint)value & 0xFFFF));
        Set(address + 1, (ushort)((uint)value >> 16));
    }

    private int C(int offset) => Options.CommandBase + offset;

    private int S(int offset) => Options.StatusBase + offset;

    // ── the PLC program ──

    private void Scan(double dt, long now)
    {
        var command = Get(C(0));
        var seq = Get(C(1));
        var beat = Get(C(8));
        var lease = Get(C(9));
        var watchdogFault = Get(C(10));

        // FR-11 watchdog: arm on the first change of Heartbeat with WatchdogFault 0 and a lease held; disarm on
        // LeaseOwner 0; trip on a 1 s stall.
        if (lease == 0)
        {
            _armed = false;
        }
        else if (beat != _lastBeat)
        {
            if (!_armed && watchdogFault == 0) _armed = true;
            _lastBeatChange = now;
        }

        _lastBeat = beat;
        if (_armed && Stopwatch.GetElapsedTime(_lastBeatChange, now) >= TimeSpan.FromSeconds(1))
        {
            _armed = false;
            _v = 0;
            _state = 7;
            _fault = 4;
            _needFreshEnable = true;
            _trips++;
            Set(C(10), 1);
        }

        InjectFaults();

        // Accept: a CommandSeq different from CommandAck, decoded Stop > Reset > Home > MoveAbsolute > MoveVelocity.
        if (seq != _ack && !Faults.SuppressAck)
        {
            Accept(command);
            ApplyEnableLevel(command);
            _ack = seq;
        }
        else
        {
            ApplyEnableLevel(command);
        }

        Integrate(dt);
        Publish(now);
    }

    private void InjectFaults()
    {
        if (Faults.TakeDriveFault()) Fault(1);
        if (Faults.TakeSafetyStop()) Fault(7);
    }

    private void Fault(ushort code)
    {
        _state = 7;
        _fault = code;
        _v = 0;
        _needFreshEnable = true;
    }

    private void Accept(ushort command)
    {
        var energised = _state is 1 or 2 or 3 or 4 or 6;
        if ((command & Stop) != 0)
        {
            if (_state is 2 or 3 or 4) _state = 6;
            return;
        }

        if ((command & Reset) != 0)
        {
            if (_state == 7)
            {
                _state = 0;
                _fault = 0;
            }

            return;
        }

        if (!energised || _state != 1) return; // "A move with Enable low is ignored"

        if ((command & Home) != 0)
        {
            _state = 2;
            _inPosition = false;
            _homeCommands++;
            return;
        }

        if ((command & MoveAbs) != 0)
        {
            if (!_homed) return;
            _target = GetInt(C(2)) / 1000.0;
            _vcmd = Math.Abs(GetInt(C(4)) / 1000.0);
            _accel = AccelerationOrDefault();
            _moveStart = _p;
            _inPosition = false;
            _state = 3;
            return;
        }

        if ((command & MoveVel) != 0)
        {
            var v = GetInt(C(4)) / 1000.0;
            if ((v < 0 && LimitMinActive) || (v > 0 && LimitMaxActive)) return; // recovery: only away from the switch
            _vcmd = v;
            _accel = AccelerationOrDefault();
            _inPosition = false;
            _state = 4;
        }
    }

    private double AccelerationOrDefault()
    {
        var a = GetInt(C(6)) / 1000.0;
        return a > 0 ? a : Options.Acceleration;
    }

    private void ApplyEnableLevel(ushort command)
    {
        var level = (command & Enable) != 0;
        if (!level)
        {
            _needFreshEnable = false;
            if (_state is 1 or 2 or 3 or 4 or 6)
            {
                _state = 0;
                _v = 0;
            }

            return;
        }

        if (_state == 0 && !_needFreshEnable) _state = 1;
    }

    private bool LimitMinActive => _p <= Options.TravelMin - Options.LimitSwitchMargin;

    private bool LimitMaxActive => Faults.ForceLimitMax || _p >= Options.TravelMax + Options.LimitSwitchMargin;

    private void Integrate(double dt)
    {
        double vt;
        var a = _accel > 0 ? _accel : Options.Acceleration;
        switch (_state)
        {
            case 3:
                var d = _target - _p;
                vt = Math.Sign(d) * Math.Min(_vcmd, Math.Sqrt(2 * a * Math.Abs(d)));
                break;
            case 4:
                vt = _vcmd;
                break;
            case 2:
                vt = Math.Sign(Options.HomeSensorPosition - _p) * Options.HomingVelocity;
                if (vt == 0) vt = -Options.HomingVelocity;
                break;
            case 6:
                vt = 0;
                a = Options.QuickStopDeceleration;
                break;
            default:
                _v = 0;
                return;
        }

        var dv = vt - _v;
        var step = a * dt;
        _v += Math.Abs(dv) <= step ? dv : Math.Sign(dv) * step;
        var before = _p;
        _p += _v * dt;

        if (_state == 3)
        {
            // Arrival: inside the window, or this step reached/crossed the target (a fixture, not drive physics).
            var crossed = Math.Sign(_target - _p) != Math.Sign(_target - before);
            if (crossed || Math.Abs(_target - _p) <= Options.InPositionWindow)
            {
                _p = _target;
                _v = 0;
                _inPosition = true;
                _state = 1;
                return;
            }
        }

        // Limit switches: moving toward an active switch is a fault (homing: FaultCode 5).
        if ((_v < 0 && LimitMinActive) || (_v > 0 && LimitMaxActive))
        {
            Fault(_state == 2 ? (ushort)5 : (ushort)2);
            return;
        }

        switch (_state)
        {
            case 3:
                if (Faults.FollowingErrorAtHalfway && Math.Abs(_p - _moveStart) >= Math.Abs(_target - _moveStart) / 2)
                {
                    Faults.FollowingErrorAtHalfway = false;
                    Fault(3);
                    return;
                }

                break;
            case 4:
                if (_homed && ((_v > 0 && _p >= Options.TravelMax) || (_v < 0 && _p <= Options.TravelMin)))
                {
                    _p = _v > 0 ? Options.TravelMax : Options.TravelMin;
                    _v = 0;
                    _state = 1;
                }

                break;
            case 2:
                var sensor = Options.HomeSensorPosition;
                var crossed = (before - sensor) * (_p - sensor) <= 0;
                if (crossed && !Faults.HomeSensorDead)
                {
                    _p = sensor;
                    _v = 0;
                    _homed = true;
                    _state = 1;
                }

                break;
            case 6:
                if (_v == 0) _state = 1;
                break;
        }
    }

    private void Publish(long now)
    {
        var flags = StatusFlags.DriveReady;
        if (_homed) flags |= StatusFlags.Homed;
        if (_inPosition) flags |= StatusFlags.InPosition;
        if (LimitMinActive) flags |= StatusFlags.LimitMin;
        if (LimitMaxActive) flags |= StatusFlags.LimitMax;
        if (Math.Abs(_p - Options.HomeSensorPosition) <= Options.HomeSensorWidth / 2) flags |= StatusFlags.HomeSensor;
        if (_v != 0) flags |= StatusFlags.Moving;

        var raw = (int)Math.Round(_p * 1000, MidpointRounding.AwayFromZero);
        {
            SetInput(S(0), _state);
            SetInput(S(1), (ushort)flags);
            SetInputInt(S(2), raw);
            SetInputInt(S(4), (int)Math.Round(_v * 1000, MidpointRounding.AwayFromZero));
            SetInput(S(6), _fault);
            SetInput(S(7), _ack);
            SetInputInt(S(8), Options.PublishLimits ? (int)Math.Round(Options.TravelMin * 1000) : 0);
            SetInputInt(S(10), Options.PublishLimits ? (int)Math.Round(Options.TravelMax * 1000) : 0);
            SetInputInt(S(12), Options.PublishLimits ? (int)Math.Round(Options.MaxVelocity * 1000) : 0);
            SetInput(S(14), Options.MapVersion);
            Set(C(11), _trips);
        }

        if (_published.Count < 1_000_000) _published.TryAdd(raw, 0);
        var truth = _truth = new PlcTruth(now, _p, _v, _state, flags, _fault, Get(C(1)), _ack, Get(C(8)), _armed,
            Get(C(10)), _trips, Get(C(9)), _homed, _homeCommands);
        for (var n = _waiters.Count; n > 0 && _waiters.TryDequeue(out var waiter); n--)
        {
            if (waiter.Done.Task.IsCompleted) continue;
            if (waiter.Predicate(truth)) waiter.Done.TrySetResult(truth);
            else _waiters.Enqueue(waiter);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        // Teardown blocks: the scan thread's join, and FluentModbus 5.3.2's ModbusTcpServer.Stop() → each
        // ModbusRequestHandler.Dispose() → _task.Wait(). On a thread-pool thread that is one worker fewer for everyone
        // (a 2-CPU host's pool starts with two, and the test host already parks two), so it runs on its own thread.
        var torndown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                _thread.Join(TimeSpan.FromSeconds(2));
                try
                {
                    _server.Stop();
                }
                catch (AggregateException)
                {
                    // A handler whose connection the fixture dropped on purpose ends with a socket error; teardown only.
                }

                _server.Dispose();
                _provider.Dispose();
                _stop.Dispose();
                torndown.SetResult();
            }
            catch (Exception ex)
            {
                torndown.SetException(ex);
            }
        }) { IsBackground = true, Name = "MiniPlc teardown" }.Start();
        await torndown.Task;
    }

    /// <summary>A listener on 127.0.0.1:0 that reports its port and can drop and refuse connections.</summary>
    private sealed class GatedProvider : ITcpClientProvider, IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _accepted = [];
        private readonly ConcurrentBag<TcpClient> _parked = [];
        private volatile bool _down;
        private volatile bool _silent;
        private int _dropNext;
        private int _acceptedCount;

        public GatedProvider()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public int Accepted => Volatile.Read(ref _acceptedCount);

        public void DropNext(int count) => Interlocked.Exchange(ref _dropNext, count);

        public void SetDown(bool down)
        {
            _down = down;
            if (!down) return;
            while (_accepted.TryTake(out var client))
            {
                try { client.Client.Close(0); } catch { /* already closed */ }
            }
        }

        public void SetSilent(bool silent)
        {
            _silent = silent;
            var drop = silent ? _accepted : _parked;
            while (drop.TryTake(out var client))
            {
                try { client.Client.Close(0); } catch { /* already closed */ }
            }
        }

        public async Task<TcpClient> AcceptTcpClientAsync()
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref _acceptedCount);
                if (_down || Interlocked.Decrement(ref _dropNext) >= 0)
                {
                    try { client.Client.Close(0); } catch { /* ignore */ }
                    continue;
                }

                if (_silent)
                {
                    _parked.Add(client); // open, never read, never answered
                    continue;
                }

                _accepted.Add(client);
                return client;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            while (_accepted.TryTake(out var client)) client.Dispose();
            while (_parked.TryTake(out var client)) client.Dispose();
        }
    }
}

/// <summary>Injected defects (the design's <c>SimFaults</c> subset the driver-side scenarios need).</summary>
internal sealed class MiniPlcFaults
{
    private int _driveFault, _safetyStop;

    public volatile bool SuppressAck;
    public volatile bool HomeSensorDead;
    public volatile bool ForceLimitMax;
    public volatile bool FollowingErrorAtHalfway;

    /// <summary>One-shot: the next scan faults with FaultCode 1.</summary>
    public void InjectDriveFault() => Interlocked.Exchange(ref _driveFault, 1);

    /// <summary>One-shot: the next scan faults with FaultCode 7.</summary>
    public void InjectSafetyStop() => Interlocked.Exchange(ref _safetyStop, 1);

    internal bool TakeDriveFault() => Interlocked.Exchange(ref _driveFault, 0) == 1;

    internal bool TakeSafetyStop() => Interlocked.Exchange(ref _safetyStop, 0) == 1;
}
