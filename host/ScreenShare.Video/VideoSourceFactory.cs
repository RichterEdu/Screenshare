using ScreenShare.Core.Protocol;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Video;

public static class VideoSourceFactory
{
    /// <summary>Vídeo com captura DXGI e encoder de hardware; null se o PC não tem encoder de hardware.</summary>
    public static IVideoSource? CreateDefault(VideoOptions options, Action<string> log)
    {
        var codecs = EncoderCatalog.HardwareCodecs;
        if (codecs == VideoCodec.None)
        {
            log("Nenhum encoder de vídeo de hardware encontrado: os celulares vão conectar sem vídeo.");
            return null;
        }
        var names = new List<string>();
        if (codecs.HasFlag(VideoCodec.H265)) names.Add("H.265");
        if (codecs.HasFlag(VideoCodec.H264)) names.Add("H.264");
        log($"Encoders de vídeo de hardware: {string.Join(" e ", names)}.");
        return new PipelineVideoSource(() => new HardwareBackend(), codecs, options, TimeProvider.System, log, CaptureThread.Prepare);
    }
}
