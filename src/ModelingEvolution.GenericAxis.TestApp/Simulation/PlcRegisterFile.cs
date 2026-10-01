using System.Buffers.Binary;
using FluentModbus;

namespace ModelingEvolution.GenericAxis.TestApp.Simulation;

/// <summary>
/// The registers of one unit, backed by the <see cref="ModbusTcpServer"/>'s own buffers, so a client reads the very
/// words the PLC scan wrote — there is no second copy that can drift. Two address spaces (protocol § Transport,
/// ADR-36): the command block lives in <see cref="Holding"/> (FC03/06/16), the status block in <see cref="Input"/>
/// (FC04), each from its own 0. A client cannot write an input register: the function code enforces ownership.
/// </summary>
/// <remarks>Addresses are absolute; <see cref="AxisPlc"/> adds the block bases.</remarks>
public sealed class PlcRegisterFile(ModbusTcpServer server, byte unitId)
{
    /// <summary>The unit these registers belong to.</summary>
    public byte UnitId => unitId;

    /// <summary>Holding registers: the command block (C+0…C+11).</summary>
    public PlcRegisterSpace Holding { get; } = new(server, unitId, input: false);

    /// <summary>Input registers: the status block (S+0…S+14).</summary>
    public PlcRegisterSpace Input { get; } = new(server, unitId, input: true);
}

/// <summary>
/// One register array of the server (holding or input).
/// </summary>
/// <remarks>
/// FluentModbus stores registers in wire order (big-endian); <c>GetHoldingRegisters</c>/<c>GetInputRegisters</c> are a
/// <c>Span&lt;short&gt;</c> over those bytes, so every access swaps the bytes of the word. 32-bit values are low word
/// first (protocol § Transport) unless the PLC under simulation is defective (<see cref="SimFaults.SwappedWordOrder"/>).
/// </remarks>
public sealed class PlcRegisterSpace
{
    private readonly ModbusTcpServer _server;
    private readonly byte _unitId;
    private readonly bool _input;

    internal PlcRegisterSpace(ModbusTcpServer server, byte unitId, bool input)
    {
        _server = server;
        _unitId = unitId;
        _input = input;
    }

    private Span<short> Words => _input ? _server.GetInputRegisters(_unitId) : _server.GetHoldingRegisters(_unitId);

    public ushort Read(int address) => Swap(unchecked((ushort)Words[address]));

    public void Write(int address, ushort value) => Words[address] = unchecked((short)Swap(value));

    /// <summary>Reads a 32-bit two's-complement value; <paramref name="highWordFirst"/> models a defective PLC.</summary>
    public int ReadInt32(int address, bool highWordFirst = false)
    {
        var first = Read(address);
        var second = Read(address + 1);
        var (low, high) = highWordFirst ? (second, first) : (first, second);
        return unchecked((int)(((uint)high << 16) | low));
    }

    /// <summary>Writes a 32-bit two's-complement value; <paramref name="highWordFirst"/> models a defective PLC.</summary>
    public void WriteInt32(int address, int value, bool highWordFirst = false)
    {
        var raw = unchecked((uint)value);
        var low = (ushort)(raw & 0xFFFF);
        var high = (ushort)(raw >> 16);
        Write(address, highWordFirst ? high : low);
        Write(address + 1, highWordFirst ? low : high);
    }

    /// <summary>Copies <paramref name="count"/> registers from <paramref name="address"/>.</summary>
    public ushort[] ReadBlock(int address, int count)
    {
        var result = new ushort[count];
        for (var i = 0; i < count; i++) result[i] = Read(address + i);
        return result;
    }

    private static ushort Swap(ushort value) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
}
