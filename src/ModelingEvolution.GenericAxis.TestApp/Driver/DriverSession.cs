using Microsoft.Extensions.Logging;
using ModelingEvolution.Drawing;
using ModelingEvolution.Drawing.Units;
using ModelingEvolution.GenericAxis.TestApp.Simulation;
using RocketWelder.SDK.Abstractions;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Driver;

/// <summary>The connection form of the <c>/driver</c> page. Defaults point at the in-app simulator.</summary>
public sealed class DriverForm
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int Unit { get; set; } = 1;
    public int CommandBase { get; set; } = RegisterMap.DefaultCommandBase;
    public int StatusBase { get; set; } = RegisterMap.DefaultStatusBase;
    public SimAxisKind Kind { get; set; } = SimAxisKind.Linear;
    public int OwnerId { get; set; } = 100;
    public double? LeaseTimeoutSeconds { get; set; } = 3;
    public double? ConfiguredTravelMin { get; set; }
    public double? ConfiguredTravelMax { get; set; }
    public double? ConfiguredMaxVelocity { get; set; }
}

/// <summary>
/// One driver per browser circuit (design § Test app): builds <see cref="ModbusLinearTrack"/> or
/// <see cref="ModbusPositioner"/> straight from the form — never through the plugin — so the same page commissions the
/// simulator and a real PLC. Verbs run in the background; their outcome lands in <see cref="LastResult"/>.
/// </summary>
public sealed class DriverSession : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private ModbusAxisDevice? _device;
    private Task? _jog;

    public DriverSession(SimulatorHost simulator, ILoggerFactory hostLoggers)
    {
        Form = new DriverForm { Port = simulator.Port, Unit = simulator.Options.UnitId, Kind = simulator.Options.Kind,
            CommandBase = simulator.Options.CommandBase, StatusBase = simulator.Options.StatusBase };
        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(Log));
        _log = _loggerFactory.CreateLogger("DriverPanel");
        _hostLog = hostLoggers.CreateLogger<DriverSession>();
    }

    private readonly ILogger _hostLog;

    public DriverForm Form { get; }
    public InMemoryLog Log { get; } = new();
    public ModbusAxisDevice? Device => _device;
    public bool IsConnected => _device?.IsConnected == true;
    public bool Busy { get; private set; }
    public string? Running { get; private set; }

    /// <summary>The last verb's outcome: "OK", or the <see cref="MotionError"/> and message.</summary>
    public string LastResult { get; private set; } = "—";

    public bool LastFailed { get; private set; }

    public string Unit => (_device?.Options.Kind ?? AxisKindOf(Form.Kind)) == AxisKind.Rotary ? "°" : "mm";

    /// <summary>Raised when a verb finishes (the page re-renders on its timer anyway).</summary>
    public event Action? Changed;

    public Task ConnectAsync() => Run("Connect", async ct =>
    {
        if (_device is not null) await DisposeDeviceAsync();
        var f = Form;
        var options = new GenericAxisOptions
        {
            Name = f.Kind == SimAxisKind.Rotary ? "turntable" : "carriage",
            Host = f.Host.Trim(),
            Port = f.Port,
            UnitId = f.Unit,
            Map = new RegisterMap(f.CommandBase, f.StatusBase),
            LeaseTimeout = f.LeaseTimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            ConfiguredTravelMin = f.ConfiguredTravelMin,
            ConfiguredTravelMax = f.ConfiguredTravelMax,
            ConfiguredMaxVelocity = f.ConfiguredMaxVelocity,
        };
        var owner = (ushort)Math.Clamp(f.OwnerId, 1, 65535);
        _device = f.Kind == SimAxisKind.Rotary
            ? new ModbusPositioner(DeviceId.New("GenericPositioner"), options, owner, _loggerFactory)
            : new ModbusLinearTrack(DeviceId.New("GenericLinearTrack"), options, owner, _loggerFactory);
        _hostLog.LogInformation("Driver panel connecting to {Address} as owner {Owner}", _device.Address, owner);
        await _device.ConnectAsync(ct);
    }, cancellable: true);

    public Task DisconnectAsync() => Run("Disconnect", async _ => await DisposeDeviceAsync());

    public Task PowerAsync(bool on) => Axis(on ? "Power on" : "Power off", (a, ct) => a.PowerAsync(on, ct));
    public Task HomeAsync() => Axis("Home", (a, ct) => a.HomeAsync(ct));
    public Task ResetAsync() => Axis("Reset", (a, ct) => a.ResetAsync(ct));

    /// <summary>STOP: never queued behind a running verb, never refused while connected.</summary>
    public async Task StopAsync()
    {
        if (_device is null) return;
        try
        {
            await _device.StopAllAsync();
            Report("Stop", null);
        }
        catch (Exception ex)
        {
            Report("Stop", ex);
        }
    }

    public Task MoveAbsoluteAsync(double target, double? speed) => Axis($"MoveAbsolute {target}", (a, ct) => a switch
    {
        ModbusLinearAxis l => l.MoveAbsoluteAsync(new Length<double, Millimetre<double>>(target), Mm(speed), ct),
        ModbusRotaryAxis r => r.MoveAbsoluteAsync(Degree<double>.Create(target), Deg(speed), RotationSense.Shortest, ct),
        _ => throw new NotSupportedException(a.GetType().Name),
    });

    public Task MoveRelativeAsync(double delta, double? speed) => Axis($"MoveRelative {delta}", (a, ct) => a switch
    {
        ModbusLinearAxis l => l.MoveRelativeAsync(new Length<double, Millimetre<double>>(delta), Mm(speed), ct),
        ModbusRotaryAxis r => r.MoveRelativeAsync(Degree<double>.Create(delta), Deg(speed), ct),
        _ => throw new NotSupportedException(a.GetType().Name),
    });

    /// <summary>Jog: MoveVelocity on press …</summary>
    public void JogStart(double velocity)
    {
        if (_device is null || _jog is { IsCompleted: false }) return;
        _jog = Axis($"Jog {velocity}", (a, ct) => a switch
        {
            ModbusLinearAxis l => l.MoveVelocityAsync(new Speed<double, MillimetrePerSecond<double>>(velocity), ct),
            ModbusRotaryAxis r => r.MoveVelocityAsync(new AngularSpeed<double, DegreePerSecond<double>>(velocity), ct),
            _ => throw new NotSupportedException(a.GetType().Name),
        });
    }

    /// <summary>… Stop on release.</summary>
    public Task JogStop() => _device is null ? Task.CompletedTask : StopAsync();

    private static Speed<double, MillimetrePerSecond<double>>? Mm(double? v) =>
        v is { } s ? new Speed<double, MillimetrePerSecond<double>>(s) : null;

    private static AngularSpeed<double, DegreePerSecond<double>>? Deg(double? v) =>
        v is { } s ? new AngularSpeed<double, DegreePerSecond<double>>(s) : null;

    private static AxisKind AxisKindOf(SimAxisKind k) => k == SimAxisKind.Rotary ? AxisKind.Rotary : AxisKind.Linear;

    private Task Axis(string what, Func<IMotionAxis, CancellationToken, Task> verb) =>
        _device is null ? Task.CompletedTask : Run(what, ct => verb(_device.Axis, ct));

    private CancellationTokenSource? _cts;

    /// <summary>Cancels a running Connect (a lease wait) — moves are stopped with STOP instead.</summary>
    public void Cancel() => _cts?.Cancel();

    private async Task Run(string what, Func<CancellationToken, Task> action, bool cancellable = false)
    {
        _cts?.Dispose();
        _cts = cancellable ? new CancellationTokenSource() : null;
        Busy = true;
        Running = what;
        _log.LogInformation("{Verb}…", what);
        Changed?.Invoke();
        try
        {
            await action(_cts?.Token ?? CancellationToken.None);
            Report(what, null);
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }
        finally
        {
            Busy = false;
            Running = null;
            Changed?.Invoke();
        }
    }

    private void Report(string what, Exception? ex)
    {
        LastFailed = ex is not null;
        LastResult = ex switch
        {
            null => $"{what}: OK",
            MotionException m => $"{what}: {m.Error} — {m.Message}",
            OperationCanceledException => $"{what}: cancelled",
            _ => $"{what}: {ex.GetType().Name} — {ex.Message}",
        };
        if (ex is null) _log.LogInformation("{Result}", LastResult);
        else _log.LogWarning("{Result}", LastResult);
    }

    private async Task DisposeDeviceAsync()
    {
        var device = _device;
        _device = null;
        if (device is null) return;
        if (device.IsConnected) await device.DisconnectAsync();
        await device.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { await DisposeDeviceAsync(); }
        catch (Exception ex) { _hostLog.LogWarning(ex, "Driver panel: disconnect on circuit close failed"); }
        _loggerFactory.Dispose();
        _cts?.Dispose();
    }
}
