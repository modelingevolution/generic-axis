using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// One serialised FluentModbus <see cref="ModbusTcpClient"/> session to the PLC (mirrors delta-positioner's
/// <c>ModbusChannel</c>).
///
/// <para>
/// All traffic — commands, the heartbeat tick and Stop — shares this one session. Serialisation goes through
/// <see cref="PriorityGate"/>, so Stop preempts queued move traffic and the heartbeat's deferral is bounded
/// (protocol FR-11). Every call retries once after a reconnect and a <see cref="RetryPause"/>; the second failure
/// is <see cref="MotionError.CommunicationLost"/>.
/// </para>
///
/// <para>
/// Timeouts (design § Driver components): connect <see cref="ConnectTimeout"/>, read/write
/// <see cref="IoTimeout"/>. A silent PLC is therefore detected ≤ 1.8 s into a tick (500 ms read, 300 ms pause,
/// 1 s connect); a refused connection in ~300 ms.
/// </para>
/// </summary>
internal sealed class ModbusChannel : IModbusChannel
{
    /// <summary>FluentModbus <c>ConnectTimeout</c> (design: 1 000 ms).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>FluentModbus <c>ReadTimeout</c> and <c>WriteTimeout</c> (design: 500 ms).</summary>
    public static readonly TimeSpan IoTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Pause between a failed transaction and its one retry (design: 300 ms).</summary>
    public static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(300);

    private readonly ILogger? _logger;
    private readonly PriorityGate _gate;
    private ModbusTcpClient _client;
    private volatile bool _disposed;

    public ModbusChannel(string host, int port, ILogger? logger, PriorityGate? gate = null)
    {
        Host = host;
        Port = port;
        _logger = logger;
        _gate = gate ?? new PriorityGate();
        _client = NewClient();
    }

    private long _retries;

    /// <summary>Reconnect-and-retries performed so far (protocol "The one retry"; the conformance checker reports it).</summary>
    public long Retries => Interlocked.Read(ref _retries);

    /// <inheritdoc/>
    public string Host { get; }

    /// <inheritdoc/>
    public int Port { get; }

    /// <inheritdoc/>
    public bool IsConnected => !_disposed && _client.IsConnected;

    private static ModbusTcpClient NewClient() => new()
    {
        ConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
        ReadTimeout = (int)IoTimeout.TotalMilliseconds,
        WriteTimeout = (int)IoTimeout.TotalMilliseconds,
    };

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct) =>
        ExecuteAsync<object?>(_ => null, "connect", ChannelPriority.Move, ct);

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return;
        // Stop lane: disconnect is part of the shutdown path and must not queue behind a move.
        using var _ = await _gate.AcquireAsync(ChannelPriority.Stop, ct);
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Disconnecting {Host}:{Port} threw; ignoring", Host, Port);
        }
    }

    private void EnsureConnected()
    {
        if (_client.IsConnected) return;
        var endpoint = IPAddress.TryParse(Host, out var ip)
            ? new IPEndPoint(ip, Port)
            : new IPEndPoint(Dns.GetHostAddresses(Host)[0], Port);
        _client.Connect(endpoint, ModbusEndianness.BigEndian);
    }

    /// <summary>Runs one transaction in its lane, retrying once after a reconnect.</summary>
    /// <exception cref="MotionException"><see cref="MotionError.CommunicationLost"/> — both attempts failed, or the
    /// channel is disposed.</exception>
    private async Task<T> ExecuteAsync<T>(Func<ModbusTcpClient, T> operation, string what,
        ChannelPriority priority, CancellationToken ct)
    {
        // A disposed channel must never quietly reopen the socket: that would make a killed commander look alive
        // again for one transaction.
        if (_disposed)
            throw new MotionException(MotionError.CommunicationLost,
                $"{Host}:{Port}: {what} attempted on a disposed channel");

        IDisposable slot;
        try
        {
            slot = await _gate.AcquireAsync(priority, ct);
        }
        catch (ObjectDisposedException)
        {
            throw new MotionException(MotionError.CommunicationLost,
                $"{Host}:{Port}: {what} abandoned — the channel was disposed");
        }

        using (slot)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_disposed)
                    throw new MotionException(MotionError.CommunicationLost,
                        $"{Host}:{Port}: {what} abandoned — the channel was disposed");
                try
                {
                    EnsureConnected();
                    return operation(_client);
                }
                catch (Exception ex) when (attempt == 0)
                {
                    // protocol rule 3 "No silent recovery": the one retry is logged at Warning and counted.
                    Interlocked.Increment(ref _retries);
                    _logger?.LogWarning(ex, "{Host}:{Port}: {What} failed, reconnecting and retrying once", Host, Port, what);
                    Reset();
                    await Task.Delay(RetryPause, ct);
                }
                catch (Exception ex)
                {
                    Reset();
                    throw new MotionException(MotionError.CommunicationLost,
                        $"{Host}:{Port}: {what} failed — {ex.Message}");
                }
            }
        }
    }

    private void Reset()
    {
        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch
        {
            // The point of resetting is that the old client is untrustworthy.
        }

        try { _client.Dispose(); } catch { /* same */ }
        _client = NewClient();
    }

    /// <inheritdoc/>
    public Task<ushort[]> ReadHoldingAsync(byte unit, ushort address, ushort count, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
        => ExecuteAsync(c => c.ReadHoldingRegisters<ushort>(unit, address, count).ToArray(), what, priority, ct);

    /// <inheritdoc/>
    public Task WriteRegisterAsync(byte unit, ushort address, ushort value, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
        => ExecuteAsync<object?>(c => { c.WriteSingleRegister(unit, address, value); return null; },
            what, priority, ct);

    /// <inheritdoc/>
    public Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
        => ExecuteAsync<object?>(c => { c.WriteMultipleRegisters(unit, address, values); return null; },
            what, priority, ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_client.IsConnected) _client.Disconnect();
            _client.Dispose();
        }
        catch
        {
            // Disposal must not throw.
        }

        _gate.Dispose();
    }
}
