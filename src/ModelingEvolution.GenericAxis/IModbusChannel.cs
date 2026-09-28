namespace ModelingEvolution.GenericAxis;

/// <summary>
/// One serialised Modbus TCP session to the PLC, holding registers only (protocol § Transport: no coils).
/// An interface so the axis logic — guards, handshake, lease — is tested against a register bank rather than a
/// socket (design § Tests, <c>FakePlcChannel</c>).
/// </summary>
internal interface IModbusChannel : IDisposable
{
    /// <summary>The PLC endpoint, for messages and logging.</summary>
    string Host { get; }

    /// <summary>The PLC's Modbus TCP port.</summary>
    int Port { get; }

    /// <summary>Whether the socket is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the session.</summary>
    /// <exception cref="RocketWelder.SDK.Devices.Motion.MotionException"><c>CommunicationLost</c> — the PLC does not answer.</exception>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Closes the session without disposing the channel. Travels on the stop lane.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>FC03: reads <paramref name="count"/> holding registers.</summary>
    Task<ushort[]> ReadHoldingAsync(byte unit, ushort address, ushort count, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default);

    /// <summary>FC06: writes one holding register.</summary>
    Task WriteRegisterAsync(byte unit, ushort address, ushort value, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default);

    /// <summary>FC16: writes consecutive holding registers in one transaction.</summary>
    Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, string what,
        ChannelPriority priority = ChannelPriority.Move, CancellationToken ct = default);
}
