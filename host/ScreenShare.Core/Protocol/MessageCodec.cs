using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
/// Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
/// </summary>
public static class MessageCodec
{
    public const ushort ProtocolVersion = 1;
    public const int HeaderSize = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;
    public const int MaxTouchPointers = 10;

    private const VideoCodec KnownCodecs = VideoCodec.H264 | VideoCodec.H265;

    public static byte[] Encode(Message message)
    {
        switch (message)
        {
            case HelloMessage m:
            {
                var w = Begin(MessageType.Hello, 9, out var bytes);
                w.WriteUInt16(m.ProtocolVersion);
                w.WriteUInt16(m.Width);
                w.WriteUInt16(m.Height);
                w.WriteUInt16(m.DensityDpi);
                w.WriteByte((byte)m.SupportedCodecs);
                return bytes;
            }
            case PingMessage m:
            {
                var w = Begin(MessageType.Ping, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case PongMessage m:
            {
                var w = Begin(MessageType.Pong, 8, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                return bytes;
            }
            case KeyframeRequestMessage:
            {
                Begin(MessageType.KeyframeRequest, 0, out var bytes);
                return bytes;
            }
            default:
                throw new ArgumentException($"Mensagem não suportada: {message.GetType().Name}.", nameof(message));
        }
    }

    public static Message Decode(byte type, ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var kind = (MessageType)type;
        Message message = kind switch
        {
            MessageType.Hello => DecodeHello(ref r),
            MessageType.Ping => new PingMessage(r.ReadUInt64()),
            MessageType.Pong => new PongMessage(r.ReadUInt64()),
            MessageType.KeyframeRequest => new KeyframeRequestMessage(),
            _ => throw new ProtocolException($"Tipo de mensagem desconhecido: 0x{type:X2}."),
        };
        r.EnsureEnd();
        return message;
    }

    private static PayloadWriter Begin(MessageType type, int payloadLength, out byte[] bytes)
    {
        if (payloadLength > MaxPayloadLength)
            throw new ProtocolException($"Payload de {payloadLength} bytes excede o limite de {MaxPayloadLength}.");
        bytes = new byte[HeaderSize + payloadLength];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), (uint)payloadLength);
        return new PayloadWriter(bytes.AsSpan(HeaderSize));
    }

    private static HelloMessage DecodeHello(ref PayloadReader r)
    {
        var version = r.ReadUInt16();
        var width = r.ReadUInt16();
        var height = r.ReadUInt16();
        var dpi = r.ReadUInt16();
        var codecs = (VideoCodec)r.ReadByte();
        if (codecs == VideoCodec.None || (codecs & ~KnownCodecs) != 0)
            throw new ProtocolException($"Flags de codec inválidas: {(byte)codecs}.");
        return new HelloMessage(version, width, height, dpi, codecs);
    }
}
