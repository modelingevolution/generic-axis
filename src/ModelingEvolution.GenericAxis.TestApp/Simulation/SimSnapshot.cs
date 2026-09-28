using System.Collections.Immutable;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// One scan's picture of the simulated PLC, for the UI and the tests (ground truth). Immutable: taken at the end of a
/// scan and handed out without a lock.
/// </summary>
public sealed record SimSnapshot
{
    /// <summary>Simulated time since power-up.</summary>
    public TimeSpan Clock { get; init; }

    /// <summary>Physical position (the frame the switches and the home sensor sit in).</summary>
    public double TruePosition { get; init; }

    /// <summary>What the PLC publishes in <c>ActualPosition</c>, in axis units.</summary>
    public double PublishedPosition { get; init; }

    public double Velocity { get; init; }
    public SimAxisState State { get; init; }
    public SimStatusFlags Flags { get; init; }
    public SimFaultCode FaultCode { get; init; }
    public bool Homed { get; init; }
    public bool Energised { get; init; }
    public ushort CommandWord { get; init; }
    public ushort CommandSeq { get; init; }
    public ushort CommandAck { get; init; }
    public ushort Heartbeat { get; init; }

    /// <summary>Time since the PLC last saw <c>Heartbeat</c> change.</summary>
    public TimeSpan HeartbeatAge { get; init; }

    public bool WatchdogArmed { get; init; }
    public ushort WatchdogFault { get; init; }
    public ushort WatchdogTrips { get; init; }
    public ushort LeaseOwner { get; init; }
    public SimFaults Faults { get; init; } = SimFaults.None;
    public bool PublishLimits { get; init; }
    public ushort MapVersion { get; init; }

    /// <summary>The Modbus listener is bound and serving.</summary>
    public bool Listening { get; init; }

    /// <summary>Raw command block C+0…C+11 as served on the wire.</summary>
    public ImmutableArray<ushort> CommandBlock { get; init; } = [];

    /// <summary>Raw status block S+0…S+14 as served on the wire.</summary>
    public ImmutableArray<ushort> StatusBlock { get; init; } = [];
}
