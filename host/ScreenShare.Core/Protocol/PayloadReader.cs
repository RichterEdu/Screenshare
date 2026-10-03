using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Lê campos little-endian em sequência; falta ou sobra de bytes vira ProtocolException.</summary>
internal ref struct PayloadReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public PayloadReader(ReadOnlySpan<byte> span) => _span = span;

    public byte ReadByte() => Take(1)[0];
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public byte[] ReadBytes(int count) => Take(count).ToArray();
    public byte[] ReadRemaining() => Take(_span.Length - _position).ToArray();

    public readonly void EnsureEnd()
    {
        if (_position != _span.Length)
            throw new ProtocolException($"{_span.Length - _position} bytes inesperados no fim do payload.");
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _span.Length - _position)
            throw new ProtocolException("Payload truncado.");
        var slice = _span.Slice(_position, count);
        _position += count;
        return slice;
    }
}
