using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using RocketWelder.SDK.Abstractions;

namespace ModelingEvolution.GenericAxis.Tests.Support;

/// <summary>xunit collection for the live Modbus tests: they run alone, one at a time (design § Tests).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveModbusCollection
{
    public const string Name = "Live Modbus (sequential)";
}

/// <summary>
/// The real driver over real Modbus TCP against <see cref="MiniPlc"/> on a loopback port chosen by the OS
/// (design § Tests, <c>LiveRig</c>): 10 ms PLC scan, 100 ms driver tick, owner id 1 unless stated. Timing assertions
/// read the PLC's ground truth (<see cref="MiniPlc.Truth"/>), never the driver's view.
/// </summary>
internal sealed class LiveRig : IAsyncDisposable
{
    /// <summary>Real-time bound for one step of a live test. A hit is a hang, not slowness.</summary>
    public static readonly TimeSpan T = TimeSpan.FromSeconds(15);

    private readonly List<ModbusAxisDevice> _devices = [];

    public LiveRig(MiniPlcOptions? plc = null)
    {
        Plc = new MiniPlc(plc);
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
            b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FakeLoggerProvider(Logs)));
    }

    public MiniPlc Plc { get; }

    public FakeLogCollector Logs { get; } = new();

    public ILoggerFactory LoggerFactory { get; }

    public GenericAxisOptions Options(string name = "carriage") => new()
    {
        Name = name,
        Host = "127.0.0.1",
        Port = Plc.Port,
        Map = new RegisterMap(Plc.Options.CommandBase, Plc.Options.StatusBase),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
    };

    public ModbusLinearTrack Track(Func<GenericAxisOptions, GenericAxisOptions>? configure = null, ushort owner = 1)
    {
        var options = Options();
        var track = new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), configure?.Invoke(options) ?? options,
            owner, LoggerFactory);
        _devices.Add(track);
        return track;
    }

    public ModbusPositioner Positioner(Func<GenericAxisOptions, GenericAxisOptions>? configure = null, ushort owner = 1)
    {
        var options = Options("turntable");
        var positioner = new ModbusPositioner(DeviceId.New("GenericPositioner"),
            configure?.Invoke(options) ?? options, owner, LoggerFactory);
        _devices.Add(positioner);
        return positioner;
    }

    public async Task<ModbusLinearTrack> ConnectedTrack(Func<GenericAxisOptions, GenericAxisOptions>? configure = null,
        ushort owner = 1, bool power = false)
    {
        var track = Track(configure, owner);
        await track.ConnectAsync().WaitAsync(T);
        if (power) await track.Carriage.PowerAsync(true).WaitAsync(T);
        return track;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
        {
            try
            {
                await device.DisposeAsync().AsTask().WaitAsync(T);
            }
            catch (Exception)
            {
                device.Dispose();
            }
        }

        await Plc.DisposeAsync();
        LoggerFactory.Dispose();
    }
}
