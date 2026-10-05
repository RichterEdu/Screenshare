using ScreenShare.Core.Protocol;
using ScreenShare.Display;

namespace ScreenShare.Video;

/// <summary>Por onde o celular está ligado: decide o bitrate padrão.</summary>
public enum VideoLink
{
    Wifi,
    Usb,
}

/// <summary>O vídeo de uma sessão: o monitor a capturar, os codecs que o celular decodifica e o caminho da conexão.</summary>
public sealed record VideoRequest(IMonitorSource Monitor, VideoCodec PhoneCodecs, VideoLink Link);

/// <summary>Para onde o vídeo vai (o SessionWriter da sessão). Chamado na thread do encoder; nunca bloqueia.</summary>
public interface IVideoOutput
{
    /// <summary>Começo de um stream: o próximo quadro é keyframe.</summary>
    void OnConfig(ConfigMessage config);

    /// <summary>Um access unit. O array passa a ser de quem recebe: quem chama não pode reutilizá-lo.</summary>
    void OnFrame(FrameMessage frame);

    /// <summary>Quadros esperando a rede: a captura só pega imagem nova com zero.</summary>
    int PendingFrames { get; }
}

/// <summary>Números acumulados do vídeo de uma sessão, para as estatísticas do console.</summary>
public sealed record VideoStats(int Width, int Height, VideoCodec Codec, long Frames, long Bytes, long Keyframes, double EncodeP95Ms)
{
    public static VideoStats Empty { get; } = new(0, 0, VideoCodec.None, 0, 0, 0, 0);
}

/// <summary>O vídeo em andamento de uma sessão. DisposeAsync para a captura e o encoder.</summary>
public interface IVideoStream : IAsyncDisposable
{
    /// <summary>Pede um keyframe (celular, descarte na fila). Nunca lança; pedidos próximos são juntados.</summary>
    void RequestKeyframe();

    VideoStats Stats { get; }
}

public interface IVideoSource
{
    /// <summary>Começa o vídeo de uma sessão; null = sem vídeo (por exemplo, sem encoder para os codecs do celular).</summary>
    Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken);
}
