using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// A listener that can be pulled out from under the Modbus server without stopping it — the comms-drop fault.
/// Closing drops every established connection and stops accepting; re-opening rebinds the same port. Register
/// contents are untouched throughout: the PLC did not reboot, only its network stopped talking.
/// Binding port 0 picks a free port once; <see cref="Port"/> reports it and every re-open reuses it.
/// </summary>
public sealed class GatedTcpClientProvider(IPEndPoint endpoint, ILogger logger) : ITcpClientProvider, IDisposable
{
    private readonly Lock _sync = new();
    private readonly List<TcpClient> _accepted = [];
    private TcpListener? _listener;
    private int _port = endpoint.Port;
    private volatile bool _open;
    private volatile bool _disposed;

    /// <summary>The port is bound and connections are served.</summary>
    public bool IsOpen => _open;

    /// <summary>The bound port (the requested one, or the one the OS picked for port 0).</summary>
    public int Port => _port;

    /// <summary>Binds the port and starts accepting.</summary>
    public void Open()
    {
        lock (_sync)
        {
            if (_disposed || _open) return;
            var listener = new TcpListener(new IPEndPoint(endpoint.Address, _port));
            // No ReuseAddress: on Linux .NET it also sets SO_REUSEPORT, which lets a second simulator bind the same port
            // and the kernel then splits clients between two PLCs. Closing resets accepted sockets (linger 0), so no
            // TIME_WAIT is left on our side to block the re-open.
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _open = true;
        }

        logger.LogInformation("Modbus listener open on {Address}:{Port}", endpoint.Address, _port);
    }

    /// <summary>Drops every connection and releases the port.</summary>
    public void Close()
    {
        int dropped;
        lock (_sync)
        {
            if (!_open) return;
            _open = false;

            try { _listener?.Stop(); }
            catch (Exception ex) { logger.LogDebug(ex, "Stopping the listener on port {Port} threw; ignoring", _port); }
            _listener?.Dispose();
            _listener = null;

            dropped = _accepted.Count;
            foreach (var client in _accepted)
            {
                try { client.Client.LingerState = new LingerOption(true, 0); client.Close(); }
                catch (Exception ex) { logger.LogDebug(ex, "Closing a client on port {Port} threw; ignoring", _port); }
            }

            _accepted.Clear();
        }

        logger.LogWarning("Modbus listener on port {Port} closed — {Dropped} connection(s) dropped", _port, dropped);
    }

    /// <inheritdoc/>
    public async Task<TcpClient> AcceptTcpClientAsync()
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            TcpListener? listener;
            lock (_sync) listener = _open ? _listener : null;

            if (listener is null)
            {
                // Gated. The Modbus server loops on this method, so wait instead of throwing — an exception here would
                // take the whole server down with the injected fault.
                await Task.Delay(50);
                continue;
            }

            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GatedTcpClientProvider));
                continue; // The gate closed mid-accept; wait for it to re-open.
            }

            lock (_sync)
            {
                if (!_open)
                {
                    client.Close();
                    continue;
                }

                _accepted.RemoveAll(c => !c.Connected);
                _accepted.Add(client);
            }

            logger.LogInformation("Modbus client {Remote} connected on port {Port}", client.Client.RemoteEndPoint, _port);
            return client;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _disposed = true;
    }
}
