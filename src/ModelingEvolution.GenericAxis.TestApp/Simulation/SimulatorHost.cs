using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// One simulated PLC on the wire: a FluentModbus server in <b>asynchronous</b> mode, the gated listener and
/// <see cref="AxisPlc"/>. FluentModbus serves each request under <c>ModbusServer.Lock</c>, and every scan and every
/// public mutation holds that same lock, so a read always sees one scan's image and a write lands between scans,
/// never inside one (protocol § Transport, Consistency). Every public member is thread-safe.
/// </summary>
/// <remarks>
/// Not synchronous mode (review #30): there one response written to a client that had already reset its connection
/// ends FluentModbus's single processing task, and the PLC never answers anyone again while the port still accepts.
/// In asynchronous mode each connection has its own handler, and a dead peer ends only its own.
/// </remarks>
public sealed class SimulatorHost : IDisposable
{
    private readonly ILogger _log;
    private readonly ModbusTcpServer _server;

    // The one lock: FluentModbus holds ModbusServer.Lock while it serves a request, so the scan and every mutation
    // hold it too.
    private readonly object _sync;
    private readonly GatedTcpClientProvider _provider;
    private readonly AxisPlc _plc;
    private readonly int _commandBase;
    private readonly int _statusBase;
    private volatile SimSnapshot _snapshot;
    private bool _started;
    private bool _disposed;

    public SimulatorHost(SimulatedAxisOptions options, ILoggerFactory? loggerFactory = null, IPAddress? bindAddress = null)
    {
        Options = options.Validate();
        _log = loggerFactory?.CreateLogger<SimulatorHost>() ?? NullLogger<SimulatorHost>.Instance;
        _commandBase = options.CommandBase;
        _statusBase = options.StatusBase;

        _server = new ModbusTcpServer((ILogger?)loggerFactory?.CreateLogger<ModbusTcpServer>() ?? NullLogger.Instance, isAsynchronous: true)
        {
            EnableRaisingEvents = true,
            AlwaysRaiseChangedEvent = true,
        };
        _sync = _server.Lock;
        _server.AddUnit(options.UnitId);
        _server.RegistersChanged += OnRegistersChanged;

        Registers = new PlcRegisterFile(_server, options.UnitId);
        _plc = new AxisPlc(options, Registers, loggerFactory?.CreateLogger<AxisPlc>());
        _provider = new GatedTcpClientProvider(new IPEndPoint(bindAddress ?? IPAddress.Any, options.Port),
            (ILogger?)loggerFactory?.CreateLogger<GatedTcpClientProvider>() ?? NullLogger.Instance);
        _snapshot = _plc.Snapshot(false);
    }

    public SimulatedAxisOptions Options { get; }

    /// <summary>The unit's holding registers (absolute addresses).</summary>
    public PlcRegisterFile Registers { get; }

    /// <summary>The bound Modbus port (the OS-picked one when configured as 0), valid after <see cref="Start"/>.</summary>
    public int Port => _provider.Port;

    /// <summary>The listener is bound and serving.</summary>
    public bool IsListening => _provider.IsOpen;

    /// <summary>
    /// Defects in force. Assigning opens or closes the listener at once, so <see cref="SimFaults.CommunicationDown"/>
    /// does not wait for the next scan. Every change is logged at Warning.
    /// </summary>
    public SimFaults Faults
    {
        get => _plc.Faults;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_sync)
            {
                if (_plc.Faults == value) return;
                _log.LogWarning("Fault injection changed: {Old} -> {New}", _plc.Faults, value);
                _plc.Faults = value;
                ApplyGate();
                Refresh();
            }
        }
    }

    /// <summary>Whether S+8…S+13 carry the limits (UI switch; tests).</summary>
    public bool PublishLimits
    {
        get => _plc.PublishLimits;
        set
        {
            lock (_sync)
            {
                if (_plc.PublishLimits == value) return;
                _log.LogWarning("PublishLimits {Old} -> {New}", _plc.PublishLimits, value);
                _plc.PublishLimits = value;
                _plc.Publish();
                Refresh();
            }
        }
    }

    /// <summary>Value served in S+14 (UI field; tests).</summary>
    public ushort MapVersion
    {
        get => _plc.MapVersion;
        set
        {
            lock (_sync)
            {
                if (_plc.MapVersion == value) return;
                _log.LogWarning("MapVersion {Old} -> {New}", _plc.MapVersion, value);
                _plc.MapVersion = value;
                _plc.Publish();
                Refresh();
            }
        }
    }

    /// <summary>Binds the port and starts serving.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _provider.Open();                       // bind first so Port is known
            _server.Start(_provider, leaveOpen: true);
            ApplyGate();
            _log.LogInformation(
                "Simulated {Kind} axis on Modbus TCP port {Port}, unit {Unit}, command block {C}, status block {S}, faults {Faults}",
                Options.Kind, Port, Options.UnitId, _commandBase, _statusBase, _plc.Faults);
            Refresh();
        }
    }

    /// <summary>Runs one PLC scan. Requests are served between scans, as they arrive, under the same lock.</summary>
    public void Tick(TimeSpan dt)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _plc.Tick(dt);
            Refresh();
        }
    }

    /// <summary>The state at the end of the last scan. Lock-free.</summary>
    public SimSnapshot Snapshot() => _snapshot;

    /// <summary>Moves the carriage to a published position by hand.</summary>
    public void Teleport(double position)
    {
        lock (_sync)
        {
            _plc.Teleport(position);
            Refresh();
        }
    }

    /// <summary>PLC power cycle (see <see cref="AxisPlc.PowerCycle"/>).</summary>
    public void PowerCycle()
    {
        lock (_sync)
        {
            _plc.PowerCycle();
            Refresh();
        }
    }

    private void OnRegistersChanged(object? sender, RegistersChangedEventArgs e)
    {
        // Raised while FluentModbus serves a write, under ModbusServer.Lock (= _sync). The PLC ignores writes to the registers it owns (protocol checklist
        // item 1): put them back at once, so a read later in the same batch cannot see the client's value.
        foreach (var address in e.Registers)
        {
            var plcOwned = address == _commandBase + SimRegisters.WatchdogTrips
                           || (address >= _statusBase && address < _statusBase + SimRegisters.StatusLength);
            if (!plcOwned) continue;
            _log.LogDebug("Client wrote PLC-owned register {Address}; ignored", address);
            _plc.Publish();
            return;
        }
    }

    private void ApplyGate()
    {
        if (!_started) return;
        if (_plc.Faults.CommunicationDown) _provider.Close();
        else _provider.Open();
        _provider.Silent = _plc.Faults.Silent;
    }

    private void Refresh() => _snapshot = _plc.Snapshot(_provider.IsOpen);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _server.RegistersChanged -= OnRegistersChanged;
        try { _server.Stop(); }
        catch (Exception ex) { _log.LogDebug(ex, "Stopping the Modbus server threw; ignoring"); }
        _provider.Dispose();
        _server.Dispose();
    }
}
