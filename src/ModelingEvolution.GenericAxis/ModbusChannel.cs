using System.Net;
using System.Net.Sockets;
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
    private readonly string _label;
    private readonly RegisterMap? _map;
    private ModbusTcpClient _client;
    private volatile bool _disposed;

    /// <param name="host">PLC host.</param>
    /// <param name="port">Modbus TCP port.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gate">The lane gate; a new one when absent.</param>
    /// <param name="label">Message prefix (the axis name, or a checker id); <c>host:port</c> when absent.</param>
    /// <param name="map">Block bases, so messages name ranges as <c>S+0…S+14</c>; raw addresses when absent.</param>
    public ModbusChannel(string host, int port, ILogger? logger, PriorityGate? gate = null, string? label = null,
        RegisterMap? map = null)
    {
        Host = host;
        Port = port;
        _logger = logger;
        _gate = gate ?? new PriorityGate();
        _label = label ?? $"{host}:{port}";
        _map = map;
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
        ExecuteAsync<object?>(_ => null, "connect", null, ChannelPriority.Move, ct);

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

    /// <summary>
    /// Whether an exception is a transport failure (protocol § Errors and debugging, class Transport): a socket or IO
    /// error, a timeout, a Modbus exception from the PLC, or FluentModbus's "connection closed" signal
    /// (<see cref="InvalidOperationException"/> from the transport layer).
    /// </summary>
    internal static bool IsTransport(Exception ex) => Innermost(ex) is IOException or SocketException or TimeoutException
        or ModbusException or InvalidOperationException;

    /// <summary>Runs one transaction in its lane, retrying once after a reconnect.</summary>
    /// <exception cref="MotionException"><see cref="MotionError.CommunicationLost"/> — both attempts failed with a
    /// transport failure, or the channel is disposed.</exception>
    private async Task<T> ExecuteAsync<T>(Func<ModbusTcpClient, T> operation, string what, string? range,
        ChannelPriority priority, CancellationToken ct, byte unit = 0)
    {
        // A disposed channel must never quietly reopen the socket: that would make a killed commander look alive
        // again for one transaction.
        if (_disposed) throw Disposed(what, range);

        IDisposable slot;
        try
        {
            slot = await _gate.AcquireAsync(priority, ct);
        }
        catch (ObjectDisposedException)
        {
            throw Disposed(what, range);
        }

        using (slot)
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_disposed) throw Disposed(what, range);
                Exception failure;
                try
                {
                    // Any failure to open the socket is a transport failure by definition (FluentModbus reports a
                    // connect timeout as a plain Exception and a refusal wrapped in an AggregateException).
                    EnsureConnected();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    goto failed;
                }

                try
                {
                    return operation(_client);
                }
                catch (Exception ex) when (IsTransport(ex))
                {
                    failure = ex;
                }

                failed:
                var reason = Innermost(failure).Message;
                Reset();
                if (attempt == 0)
                {
                    Interlocked.Increment(ref _retries); // counted for the conformance checker's `retries`
                    _logger?.LogWarning(failure,
                        "{Label}: {What}{Range} on {Host}:{Port} unit {Unit} failed ({Message}); reconnecting "
                        + "and retrying once", _label, what, range is null ? "" : " " + range, Host, Port, unit, reason);
                    await Task.Delay(RetryPause, ct);
                    continue;
                }

                throw new MotionException(MotionError.CommunicationLost,
                    $"{_label}: {MotionError.CommunicationLost}: {what}"
                    + $"{(range is null ? "" : " " + range)} on {Host}:{Port} unit {unit} failed twice "
                    + $"(reconnected once): {reason}.");
            }
        }
    }

    private static Exception Innermost(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: 1 } agg ? Innermost(agg.InnerExceptions[0]) : ex;

    private MotionException Disposed(string what, string? range) =>
        new(MotionError.CommunicationLost,
            $"{_label}: {MotionError.CommunicationLost}: {what}"
            + $"{(range is null ? "" : " " + range)} on {Host}:{Port} not sent: the channel was disposed.");

    private string Range(string op, ushort address, int count) =>
        $"({op} {(_map is null ? (count <= 1 ? $"{address}" : $"{address}…{address + count - 1}") : _map.DescribeRange(address, count))})";

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
        => ExecuteAsync(c => c.ReadHoldingRegisters<ushort>(unit, address, count).ToArray(), what,
            Range("read", address, count), priority, ct, unit);

    /// <inheritdoc/>
    public Task WriteRegisterAsync(byte unit, ushort address, ushort value, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
        => ExecuteAsync<object?>(c => { c.WriteSingleRegister(unit, address, value); return null; },
            what, Range("write", address, 1), priority, ct, unit);

    /// <inheritdoc/>
    public Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default)
        => ExecuteAsync<object?>(c => { c.WriteMultipleRegisters(unit, address, values); return null; },
            what, Range("write", address, values.Length), priority, ct, unit);

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
