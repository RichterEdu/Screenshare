using System.Globalization;
using ScreenShare.Core.Protocol;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>A linha de estatísticas do vídeo que o console mostra a cada 5 s.</summary>
public static class VideoStatsLine
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <param name="drops">Quantas vezes a fila de envio descartou quadros no período.</param>
    public static string Format(VideoStats previous, VideoStats current, TimeSpan elapsed, int drops, double? rttMs)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var fps = (current.Frames - previous.Frames) / seconds;
        var mbps = (current.Bytes - previous.Bytes) * 8 / seconds / 1_000_000;
        var keyframes = current.Keyframes - previous.Keyframes;
        var codec = current.Codec switch
        {
            VideoCodec.H265 => "H.265",
            VideoCodec.H264 => "H.264",
            _ => "sem encoder",
        };
        var rtt = rttMs is { } value ? string.Format(PtBr, "{0:0.0} ms", value) : "—";
        return string.Format(PtBr,
            "Vídeo: {0} {1}×{2} · {3:0.0} fps · {4:0.0} Mbps · {5} IDR · {6} descartes · encode p95 {7:0.0} ms · RTT {8}",
            codec, current.Width, current.Height, fps, mbps, keyframes, drops, current.EncodeP95Ms, rtt);
    }
}
