using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Registers;

/// <summary>A PLC endpoint the <c>/registers</c> page reads: host, port, unit and the two block bases.</summary>
public sealed record RegisterEndpoint(string Host, int Port, byte Unit, int CommandBase, int StatusBase)
{
    internal RegisterMap Map => new(CommandBase, StatusBase);

    public override string ToString() => $"{Host}:{Port} unit {Unit} (C={CommandBase}, S={StatusBase})";
}

/// <summary>One decoded read of both blocks, or the error of the last attempt with the last rows read.</summary>
public sealed record RegisterReading(DateTimeOffset At, IReadOnlyList<RegisterRow> Rows, string? Error);

/// <summary>
/// Reads C+0…C+11 and S+0…S+14 of any PLC at 5 Hz through the driver's <see cref="ModbusChannel"/> and decodes them
/// with <see cref="RegisterDump"/>, the decoder behind <c>--dump</c> (design § Test app, <c>/registers</c>; GA-I-42).
/// Read-only: it takes no lease, never beats and writes nothing. The latest reading is an immutable record swapped
/// atomically, so the page renders it without a lock.
/// </summary>
public sealed class RemoteRegisterReader : IAsyncDisposable
{
    /// <summary>5 Hz (protocol § Errors and debugging, rule 4).</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(200);

    private readonly ModbusChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private volatile RegisterReading? _latest;
    private long _reads;

    public RemoteRegisterReader(RegisterEndpoint endpoint, ILoggerFactory loggerFactory)
    {
        Endpoint = endpoint;
        _channel = new ModbusChannel(endpoint.Host, endpoint.Port, loggerFactory.CreateLogger<ModbusChannel>());
        _loop = Task.Run(RunAsync);
    }

    public RegisterEndpoint Endpoint { get; }

    /// <summary>The latest reading; null before the first attempt completes.</summary>
    public RegisterReading? Latest => _latest;

    /// <summary>Successful reads of both blocks so far.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    private async Task RunAsync()
    {
        using var period = new PeriodicTimer(Period);
        var ct = _stop.Token;
        var map = Endpoint.Map;
        do
        {
            try
            {
                var command = await _channel.ReadHoldingAsync(Endpoint.Unit, map.Command, RegisterMap.CommandLength,
                    "read C+0…C+11 (/registers)", ChannelPriority.Move, ct);
                var status = await _channel.ReadHoldingAsync(Endpoint.Unit, map.Status, RegisterMap.StatusLength,
                    "read S+0…S+14 (/registers)", ChannelPriority.Move, ct);
                _latest = new RegisterReading(DateTimeOffset.UtcNow, RegisterDump.Decode(map, command, status), null);
                Interlocked.Increment(ref _reads);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (MotionException ex)
            {
                _latest = new RegisterReading(DateTimeOffset.UtcNow, _latest?.Rows ?? [], CheckerText.Describe(ex));
            }
            catch (Exception ex)
            {
                // A PLC that answers with a malformed block must not end the page's loop silently.
                _latest = new RegisterReading(DateTimeOffset.UtcNow, _latest?.Rows ?? [], $"{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (!await period.WaitForNextTickAsync(ct)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        } while (true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync();
        try { await _loop; }
        catch (OperationCanceledException) { }
        _channel.Dispose();
        _stop.Dispose();
    }
}
