namespace ScreenShare.Core.Protocol;

/// <summary>Código do tipo de mensagem no cabeçalho do quadro.</summary>
public enum MessageType : byte
{
    Hello = 1,
    Config = 2,
    Frame = 3,
    Touch = 4,
    Ping = 5,
    Pong = 6,
    KeyframeRequest = 7,
}

/// <summary>Flags de codec: o HELLO carrega uma combinação; o CONFIG, exatamente um.</summary>
[Flags]
public enum VideoCodec : byte
{
    None = 0,
    H264 = 1,
    H265 = 2,
}

public enum TouchAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

public abstract record Message;

/// <summary>Celular → PC, primeira mensagem da conexão.</summary>
public sealed record HelloMessage(
    ushort ProtocolVersion, ushort Width, ushort Height, ushort DensityDpi, VideoCodec SupportedCodecs) : Message;

/// <summary>PC → celular. CodecConfig: SPS/PPS (+VPS no H.265) em Annex-B; vazio se vierem dentro dos FRAMEs.</summary>
public sealed record ConfigMessage(
    ushort Width, ushort Height, VideoCodec Codec, uint BitrateKbps, byte[] CodecConfig) : Message;

/// <summary>PC → celular: um quadro de vídeo codificado (NAL units em Annex-B).</summary>
public sealed record FrameMessage(ulong TimestampUs, bool IsKeyframe, byte[] Data) : Message;

/// <summary>Um dedo na tela. X e Y normalizados ao monitor virtual (0 = esquerda/topo, 1 = direita/base).</summary>
public readonly record struct TouchPointer(byte Id, TouchAction Action, float X, float Y, float Pressure);

/// <summary>Celular → PC: estado de todos os ponteiros ativos num instante.</summary>
public sealed record TouchMessage(IReadOnlyList<TouchPointer> Pointers) : Message;

/// <summary>Qualquer lado; quem recebe responde PONG com o mesmo valor.</summary>
public sealed record PingMessage(ulong TimestampUs) : Message;

public sealed record PongMessage(ulong TimestampUs) : Message;

/// <summary>Celular → PC: pede um keyframe após erro de decodificação.</summary>
public sealed record KeyframeRequestMessage : Message;
