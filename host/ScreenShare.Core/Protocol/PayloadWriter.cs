using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Escreve campos little-endian em sequência num span já dimensionado.</summary>
internal ref struct PayloadWriter
{
    private readonly Span<byte> _span;
    private int _position;

    public PayloadWriter(Span<byte> span) => _span = span;

    public void WriteByte(byte value) => _span[_position++] = value;

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_span[_position..], value);
        _position += 2;
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_span[_position..], value);
        _position += 4;
    }

    public void WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_span[_position..], value);
        _position += 8;
    }

    public void WriteSingle(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(_span[_position..], value);
        _position += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_span[_position..]);
        _position += value.Length;
    }
}
