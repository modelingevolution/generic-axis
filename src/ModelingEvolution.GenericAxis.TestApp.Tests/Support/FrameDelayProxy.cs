using System.Net;
using System.Net.Sockets;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Support;

/// <summary>
/// A Modbus TCP proxy in front of a simulator. Once <see cref="Delay"/> is set, every request frame except a heartbeat
/// write (FC06 to <see cref="HeartbeatAddress"/>) is held for that long before it is forwarded: a slow link or a starved
/// host for the checker's reads and commands, while its beat still gets through between them. Responses pass straight.
/// </summary>
internal sealed class FrameDelayProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _target;
    private readonly CancellationTokenSource _stop = new();

    public FrameDelayProxy(int targetPort, ushort heartbeatAddress = 8)
    {
        _target = targetPort;
        HeartbeatAddress = heartbeatAddress;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ushort HeartbeatAddress { get; }

    /// <summary>Held per non-heartbeat request frame; zero forwards at once.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Request frames held so far.</summary>
    public int Delayed => _delayed;

    private int _delayed;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = PumpAsync(client);
        }
    }

    private async Task PumpAsync(TcpClient client)
    {
        using var c = client;
        using var server = new TcpClient();
        try
        {
            await server.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
            var up = RequestsAsync(c.GetStream(), server.GetStream());
            var down = c.GetStream() is var cs ? server.GetStream().CopyToAsync(cs, _stop.Token) : Task.CompletedTask;
            await Task.WhenAny(up, down);
        }
        catch (Exception)
        {
            // A closed side ends the pair.
        }
    }

    private async Task RequestsAsync(NetworkStream from, NetworkStream to)
    {
        var header = new byte[7];
        while (!_stop.IsCancellationRequested)
        {
            await from.ReadExactlyAsync(header, _stop.Token);
            var length = (header[4] << 8) | header[5]; // unit id + PDU
            var pdu = new byte[length - 1];
            await from.ReadExactlyAsync(pdu, _stop.Token);
            var heartbeat = pdu.Length >= 3 && pdu[0] == 0x06 && ((pdu[1] << 8) | pdu[2]) == HeartbeatAddress;
            var delay = Delay;
            if (!heartbeat && delay > TimeSpan.Zero)
            {
                Interlocked.Increment(ref _delayed);
                await Task.Delay(delay, _stop.Token);
            }

            // One write per frame: the server parses a frame from one receive.
            await to.WriteAsync((byte[])[.. header, .. pdu], _stop.Token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
