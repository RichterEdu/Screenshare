using System.Buffers.Binary;
using System.Text;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// Converte mensagens de/para quadros [type:u8][payloadLength:u32 LE][payload].
/// Layouts em docs/protocol.md; bytes de referência em docs/protocol-vectors.
/// </summary>
public static class MessageCodec
{
    public const ushort ProtocolVersion = 2;
    public const int HeaderSize = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;
    public const int MaxTouchPointers = 10;
    public const int SecretLength = 32;
    public const int TokenLength = 32;
    public const int MaxDeviceNameBytes = 64;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const int TouchPointerSize = 14;
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
            case ConfigMessage m:
            {
                if (m.CodecConfig.Length > ushort.MaxValue)
                    throw new ProtocolException($"Codec config de {m.CodecConfig.Length} bytes não cabe em u16.");
                var w = Begin(MessageType.Config, 11 + m.CodecConfig.Length, out var bytes);
                w.WriteUInt16(m.Width);
                w.WriteUInt16(m.Height);
                w.WriteByte((byte)m.Codec);
                w.WriteUInt32(m.BitrateKbps);
                w.WriteUInt16((ushort)m.CodecConfig.Length);
                w.WriteBytes(m.CodecConfig);
                return bytes;
            }
            case FrameMessage m:
            {
                var w = Begin(MessageType.Frame, 9 + m.Data.Length, out var bytes);
                w.WriteUInt64(m.TimestampUs);
                w.WriteByte(m.IsKeyframe ? (byte)1 : (byte)0);
                w.WriteBytes(m.Data);
                return bytes;
            }
            case TouchMessage m:
            {
                if (m.Pointers.Count is < 1 or > MaxTouchPointers)
                    throw new ProtocolException($"TOUCH precisa de 1..{MaxTouchPointers} ponteiros, recebeu {m.Pointers.Count}.");
                var w = Begin(MessageType.Touch, 1 + TouchPointerSize * m.Pointers.Count, out var bytes);
                w.WriteByte((byte)m.Pointers.Count);
                foreach (var p in m.Pointers)
                {
                    w.WriteByte(p.Id);
                    w.WriteByte((byte)p.Action);
                    w.WriteSingle(p.X);
                    w.WriteSingle(p.Y);
                    w.WriteSingle(p.Pressure);
                }
                return bytes;
            }
            case PairMessage m:
            {
                RequireLength(m.Secret, SecretLength, "segredo");
                var name = EncodeDeviceName(m.DeviceName);
                var w = Begin(MessageType.Pair, SecretLength + 1 + name.Length, out var bytes);
                w.WriteBytes(m.Secret);
                w.WriteByte((byte)name.Length);
                w.WriteBytes(name);
                return bytes;
            }
            case PairedMessage m:
            {
                RequireLength(m.Token, TokenLength, "chave");
                var w = Begin(MessageType.Paired, TokenLength, out var bytes);
                w.WriteBytes(m.Token);
                return bytes;
            }
            case AuthMessage m:
            {
                RequireLength(m.Token, TokenLength, "chave");
                var w = Begin(MessageType.Auth, TokenLength, out var bytes);
                w.WriteBytes(m.Token);
                return bytes;
            }
            case DeniedMessage m:
            {
                var w = Begin(MessageType.Denied, 1, out var bytes);
                w.WriteByte((byte)m.Reason);
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
            MessageType.Config => DecodeConfig(ref r),
            MessageType.Frame => DecodeFrame(ref r),
            MessageType.Touch => DecodeTouch(ref r),
            MessageType.Pair => DecodePair(ref r),
            MessageType.Paired => new PairedMessage(r.ReadBytes(TokenLength)),
            MessageType.Auth => new AuthMessage(r.ReadBytes(TokenLength)),
            MessageType.Denied => DecodeDenied(ref r),
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

    private static ConfigMessage DecodeConfig(ref PayloadReader r)
    {
        var width = r.ReadUInt16();
        var height = r.ReadUInt16();
        var codec = (VideoCodec)r.ReadByte();
        if (codec is not (VideoCodec.H264 or VideoCodec.H265))
            throw new ProtocolException($"Codec inválido: {(byte)codec}.");
        var bitrate = r.ReadUInt32();
        var codecConfigLength = r.ReadUInt16();
        var codecConfig = r.ReadBytes(codecConfigLength);
        return new ConfigMessage(width, height, codec, bitrate, codecConfig);
    }

    private static FrameMessage DecodeFrame(ref PayloadReader r)
    {
        var timestamp = r.ReadUInt64();
        var flags = r.ReadByte();
        return new FrameMessage(timestamp, (flags & 1) != 0, r.ReadRemaining());
    }

    private static TouchMessage DecodeTouch(ref PayloadReader r)
    {
        var count = r.ReadByte();
        if (count is < 1 or > MaxTouchPointers)
            throw new ProtocolException($"TOUCH precisa de 1..{MaxTouchPointers} ponteiros, recebeu {count}.");
        var pointers = new TouchPointer[count];
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadByte();
            var action = r.ReadByte();
            if (action > (byte)TouchAction.Cancel)
                throw new ProtocolException($"Ação de toque desconhecida: {action}.");
            var x = r.ReadSingle();
            var y = r.ReadSingle();
            var pressure = r.ReadSingle();
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(pressure))
                throw new ProtocolException("Coordenada ou pressão de toque não finita.");
            pointers[i] = new TouchPointer(id, (TouchAction)action, x, y, pressure);
        }
        return new TouchMessage(pointers);
    }

    private static void RequireLength(byte[] value, int length, string what)
    {
        if (value.Length != length)
            throw new ProtocolException($"O {what} precisa ter {length} bytes, tem {value.Length}.");
    }

    private static byte[] EncodeDeviceName(string name)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(name);
        }
        catch (EncoderFallbackException)
        {
            throw new ProtocolException("Nome do aparelho não é texto válido.");
        }
        if (bytes.Length is < 1 or > MaxDeviceNameBytes)
            throw new ProtocolException($"Nome do aparelho precisa ter 1..{MaxDeviceNameBytes} bytes, tem {bytes.Length}.");
        return bytes;
    }

    private static PairMessage DecodePair(ref PayloadReader r)
    {
        var secret = r.ReadBytes(SecretLength);
        var nameLength = r.ReadByte();
        if (nameLength is < 1 or > MaxDeviceNameBytes)
            throw new ProtocolException($"Nome do aparelho precisa ter 1..{MaxDeviceNameBytes} bytes, tem {nameLength}.");
        var nameBytes = r.ReadBytes(nameLength);
        try
        {
            return new PairMessage(secret, StrictUtf8.GetString(nameBytes));
        }
        catch (DecoderFallbackException)
        {
            throw new ProtocolException("Nome do aparelho não é UTF-8 válido.");
        }
    }

    private static DeniedMessage DecodeDenied(ref PayloadReader r)
    {
        var reason = r.ReadByte();
        if (reason is < 1 or > 3)
            throw new ProtocolException($"Motivo de DENIED desconhecido: {reason}.");
        return new DeniedMessage((DeniedReason)reason);
    }
}
