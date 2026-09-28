using FluentModbus;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Simulation;

/// <summary>
/// A bare <see cref="AxisPlc"/> on an unstarted FluentModbus register bank, scanned by hand in simulated time — no
/// socket, no clock. The helpers write registers the way a driver would, straight from protocol.md.
/// </summary>
internal sealed class PlcBench : IDisposable
{
    public static readonly TimeSpan Scan = TimeSpan.FromMilliseconds(10);

    private readonly ModbusTcpServer _server = new(isAsynchronous: false);
    private ushort _seq;
    private ushort _beat;

    public PlcBench(SimulatedAxisOptions? options = null)
    {
        Options = options ?? new SimulatedAxisOptions();
        _server.AddUnit(Options.UnitId);
        Registers = new PlcRegisterFile(_server, Options.UnitId);
        Plc = new AxisPlc(Options, Registers);
    }

    public SimulatedAxisOptions Options { get; }
    public PlcRegisterFile Registers { get; }
    public AxisPlc Plc { get; }
    public ushort Seq => _seq;

    private int C => Options.CommandBase;
    private int S => Options.StatusBase;

    public SimSnapshot Snap => Plc.Snapshot(false);
    public ushort State => Registers.Read(S + SimRegisters.State);
    public SimStatusFlags Flags => (SimStatusFlags)Registers.Read(S + SimRegisters.Flags);
    public ushort FaultCode => Registers.Read(S + SimRegisters.FaultCode);
    public ushort Ack => Registers.Read(S + SimRegisters.CommandAck);
    public int ActualPositionRaw => Registers.ReadInt32(S + SimRegisters.ActualPosition);
    public int ActualVelocityRaw => Registers.ReadInt32(S + SimRegisters.ActualVelocity);
    public ushort WatchdogFault => Registers.Read(C + SimRegisters.WatchdogFault);
    public ushort WatchdogTrips => Registers.Read(C + SimRegisters.WatchdogTrips);

    public void Tick(int scans = 1)
    {
        for (var i = 0; i < scans; i++) Plc.Tick(Scan);
    }

    public void TickFor(TimeSpan duration) => Tick((int)Math.Round(duration / Scan));

    /// <summary>Scans until <paramref name="until"/> holds; fails after <paramref name="limit"/> of simulated time.</summary>
    public TimeSpan TickUntil(Func<bool> until, TimeSpan limit)
    {
        var elapsed = TimeSpan.Zero;
        while (!until())
        {
            if (elapsed > limit) throw new TimeoutException($"condition not met within {limit} of simulated time");
            Tick();
            elapsed += Scan;
        }

        return elapsed;
    }

    /// <summary>Parameters (C+2…C+7), each int32 low word first, in axis units.</summary>
    public void Parameters(double target, double velocity, double acceleration = 0)
    {
        Registers.WriteInt32(C + SimRegisters.TargetPosition, (int)Math.Round(target * 1000));
        Registers.WriteInt32(C + SimRegisters.Velocity, (int)Math.Round(velocity * 1000));
        Registers.WriteInt32(C + SimRegisters.Acceleration, (int)Math.Round(acceleration * 1000));
    }

    /// <summary>A command write: <c>Command</c> and a fresh <c>CommandSeq</c>.</summary>
    public ushort Command(SimCommandBits bits)
    {
        _seq = _seq == ushort.MaxValue ? (ushort)1 : (ushort)(_seq + 1);
        Registers.Write(C + SimRegisters.Command, (ushort)bits);
        Registers.Write(C + SimRegisters.CommandSeq, _seq);
        return _seq;
    }

    /// <summary>The write that clears edge bits after an ack: same sequence.</summary>
    public void ClearEdges(SimCommandBits level) => Registers.Write(C + SimRegisters.Command, (ushort)level);

    public void Lease(ushort owner) => Registers.Write(C + SimRegisters.LeaseOwner, owner);
    public void ClearWatchdogFault() => Registers.Write(C + SimRegisters.WatchdogFault, 0);

    public void Beat()
    {
        _beat = _beat == ushort.MaxValue ? (ushort)1 : (ushort)(_beat + 1);
        Registers.Write(C + SimRegisters.Heartbeat, _beat);
    }

    /// <summary>Beats every 100 ms of simulated time for <paramref name="duration"/>.</summary>
    public void BeatFor(TimeSpan duration)
    {
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(100))
        {
            Beat();
            Tick(10);
        }
    }

    /// <summary>Enable 1 and wait for Standstill (and the ack).</summary>
    public void Energise()
    {
        var seq = Command(SimCommandBits.Enable);
        TickUntil(() => Ack == seq && State == (ushort)SimAxisState.Standstill, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _server.Dispose();
}
