using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>One transaction the driver sent, as the fake PLC saw it.</summary>
/// <param name="Space">Holding (FC03/FC06/FC16) or input (FC04).</param>
internal sealed record ChannelOp(bool IsWrite, ushort Address, ushort[] Values, int Count, ChannelPriority Lane,
    string What, RegisterSpace Space = RegisterSpace.Holding)
{
    /// <summary>A read of input registers (FC04).</summary>
    public bool IsInputRead => !IsWrite && Space == RegisterSpace.Input;

    /// <summary>A read of holding registers (FC03).</summary>
    public bool IsHoldingRead => !IsWrite && Space == RegisterSpace.Holding;

    public override string ToString() =>
        IsWrite ? $"W {Address} [{string.Join(", ", Values)}] {Lane} ({What})"
            : $"R{(Space == RegisterSpace.Input ? "I" : "H")} {Address} x{Count} {Lane} ({What})";
}

/// <summary>
/// Two 65 536-register banks standing in for the PLC (design § Tests, <c>FakePlcChannel</c>): holding registers (the
/// command block, FC03/FC06/FC16) and input registers (the status block, FC04; ADR-36). Writes reach only the holding
/// bank, as on a real PLC. Records every transaction
/// with its lane; <see cref="OnCommand"/> lets a test script the PLC's answer to a command write. All calls complete
/// synchronously, so a verb runs up to its first wait before the call returns.
/// </summary>
internal sealed class FakePlcChannel : IModbusChannel
{
    private readonly Lock _sync = new();
    private readonly ushort[] _regs = new ushort[65536];   // holding
    private readonly ushort[] _input = new ushort[65536];
    private readonly List<ChannelOp> _ops = [];

    public FakePlcChannel(RegisterMap? map = null)
    {
        Map = map ?? RegisterMap.Default;
    }

    public RegisterMap Map { get; }

    public string Host => "fake-plc";

    public int Port => 502;

    public bool IsConnected { get; private set; }

    /// <summary>Called under the bank lock for a write of <c>[Command, CommandSeq]</c>: (word, seq).</summary>
    public Action<FakePlcChannel, ushort, ushort>? OnCommand { get; set; }

    /// <summary>Called under the bank lock after every read.</summary>
    public Action<FakePlcChannel, ChannelOp>? OnRead { get; set; }

    /// <summary>Called under the bank lock after every write.</summary>
    public Action<FakePlcChannel, ChannelOp>? OnWrite { get; set; }

    /// <summary>When it returns true for an operation, that operation throws CommunicationLost.</summary>
    public Func<ChannelOp, bool>? FailWhen { get; set; }

    /// <summary>When it returns an exception for an operation, that operation throws it as is (a non-transport defect).</summary>
    public Func<ChannelOp, Exception?>? ThrowWhen { get; set; }

    /// <summary>When it returns registers for a read, the read answers them instead of the bank (a malformed answer).</summary>
    public Func<ChannelOp, ushort[]?>? AnswerWith { get; set; }

    /// <summary>Whether a hook that makes an operation fail or answer wrongly is installed.</summary>
    public bool Injecting => FailWhen is not null || ThrowWhen is not null || AnswerWith is not null;

    public IReadOnlyList<ChannelOp> Ops
    {
        get { lock (_sync) return [.. _ops]; }
    }

    public int OpCount
    {
        get { lock (_sync) return _ops.Count; }
    }

    public IReadOnlyList<ChannelOp> Writes => Ops.Where(o => o.IsWrite).ToArray();

    /// <summary>Writes after operation index <paramref name="from"/> that are not heartbeat-lane traffic.</summary>
    public IReadOnlyList<ChannelOp> CommandWritesSince(int from) =>
        Ops.Skip(from).Where(o => o.IsWrite && o.Lane != ChannelPriority.Heartbeat).ToArray();

    /// <summary>A holding register (the command block).</summary>
    public ushort this[ushort address]
    {
        get { lock (_sync) return _regs[address]; }
        set { lock (_sync) _regs[address] = value; }
    }

    /// <summary>An input register (the status block).</summary>
    public ushort Input(ushort address)
    {
        lock (_sync) return _input[address];
    }

    /// <summary>Sets an input register (the PLC publishing).</summary>
    public void SetInput(ushort address, ushort value)
    {
        lock (_sync) _input[address] = value;
    }

    /// <summary>The 15 status registers as the PLC publishes them now.</summary>
    public ushort[] StatusWords()
    {
        lock (_sync) return _input.AsSpan(Map.Status, RegisterMap.StatusLength).ToArray();
    }

    public void Set(Action<FakePlcChannel> mutate)
    {
        lock (_sync) mutate(this);
    }

    // ── register helpers: status fields live in the input bank, command fields in the holding bank ──

    public ushort State { get => Input(Map.State); set => SetInput(Map.State, value); }

    public StatusFlags Flags { get => (StatusFlags)Input(Map.Flags); set => SetInput(Map.Flags, (ushort)value); }

    public int ActualPosition { get => GetInputInt(Map.ActualPosition); set => SetInputInt(Map.ActualPosition, value); }

    public int ActualVelocity { get => GetInputInt(Map.ActualVelocity); set => SetInputInt(Map.ActualVelocity, value); }

    public ushort FaultCode { get => Input(Map.FaultCode); set => SetInput(Map.FaultCode, value); }

    public ushort CommandAck { get => Input(Map.CommandAck); set => SetInput(Map.CommandAck, value); }

    public ushort Command => this[Map.Command];

    public ushort CommandSeq => this[Map.CommandSeq];

    public ushort LeaseOwner { get => this[Map.LeaseOwner]; set => this[Map.LeaseOwner] = value; }

    public ushort Heartbeat { get => this[Map.Heartbeat]; set => this[Map.Heartbeat] = value; }

    public ushort WatchdogFault { get => this[Map.WatchdogFault]; set => this[Map.WatchdogFault] = value; }

    public ushort WatchdogTrips { get => this[Map.WatchdogTrips]; set => this[Map.WatchdogTrips] = value; }

    public ushort MapVersion { get => Input(Map.MapVersion); set => SetInput(Map.MapVersion, value); }

    public void SetLimits(int travelMin, int travelMax, int maxVelocity)
    {
        lock (_sync)
        {
            SetInputInt(Map.TravelMin, travelMin);
            SetInputInt(Map.TravelMax, travelMax);
            SetInputInt(Map.MaxVelocity, maxVelocity);
        }
    }

    public int GetInt(ushort address)
    {
        lock (_sync) return Words.Join(_regs[address], _regs[address + 1]);
    }

    public void SetInt(ushort address, int value)
    {
        lock (_sync)
        {
            var (lo, hi) = Words.Split(value);
            _regs[address] = lo;
            _regs[address + 1] = hi;
        }
    }

    public int GetInputInt(ushort address)
    {
        lock (_sync) return Words.Join(_input[address], _input[address + 1]);
    }

    public void SetInputInt(ushort address, int value)
    {
        lock (_sync)
        {
            var (lo, hi) = Words.Split(value);
            _input[address] = lo;
            _input[address + 1] = hi;
        }
    }

    // ── IModbusChannel ──

    public Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadHoldingAsync(byte unit, ushort address, ushort count, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default) =>
        Read(RegisterSpace.Holding, address, count, what, priority, ct);

    public Task<ushort[]> ReadInputAsync(byte unit, ushort address, ushort count, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default) =>
        Read(RegisterSpace.Input, address, count, what, priority, ct);

    private Task<ushort[]> Read(RegisterSpace space, ushort address, ushort count, string what,
        ChannelPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var op = new ChannelOp(false, address, [], count, priority, what, space);
            _ops.Add(op);
            if (FailWhen?.Invoke(op) == true)
                return Task.FromException<ushort[]>(new MotionException(MotionError.CommunicationLost,
                    $"fake-plc: {what} failed (injected)"));
            if (ThrowWhen?.Invoke(op) is { } thrown) return Task.FromException<ushort[]>(thrown);
            OnRead?.Invoke(this, op);
            if (AnswerWith?.Invoke(op) is { } answer) return Task.FromResult(answer);
            return Task.FromResult((space == RegisterSpace.Input ? _input : _regs).AsSpan(address, count).ToArray());
        }
    }

    public Task WriteRegisterAsync(byte unit, ushort address, ushort value, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default) =>
        WriteRegistersAsync(unit, address, [value], what, priority, ct);

    public Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var op = new ChannelOp(true, address, [.. values], values.Length, priority, what);
            _ops.Add(op);
            if (FailWhen?.Invoke(op) == true)
                return Task.FromException(new MotionException(MotionError.CommunicationLost,
                    $"fake-plc: {what} failed (injected)"));
            if (ThrowWhen?.Invoke(op) is { } thrown) return Task.FromException(thrown);
            values.CopyTo(_regs.AsSpan(address));
            if (address == Map.Command && values.Length == 2)
                OnCommand?.Invoke(this, values[0], values[1]);
            OnWrite?.Invoke(this, op);
            return Task.CompletedTask;
        }
    }

    public void Dispose() => IsConnected = false;
}
