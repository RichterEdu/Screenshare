using ScreenShare.Core.Protocol;

namespace ScreenShare.Video.Pipeline;

public static class CodecChooser
{
    /// <summary>H.265 se o celular e o host tiverem; senão H.264; None = sem vídeo. forced (--codec) restringe a um só.</summary>
    public static VideoCodec Choose(VideoCodec phone, VideoCodec host, VideoCodec forced = VideoCodec.None)
    {
        var both = phone & host;
        if (forced != VideoCodec.None) both &= forced;
        if (both.HasFlag(VideoCodec.H265)) return VideoCodec.H265;
        if (both.HasFlag(VideoCodec.H264)) return VideoCodec.H264;
        return VideoCodec.None;
    }

    public static string Name(VideoCodec codec) => codec == VideoCodec.H265 ? "H.265" : "H.264";
}

/// <summary>Codecs cujo encoder falhou ao abrir: ficam de fora até o host reiniciar. Compartilhado entre sessões.</summary>
public sealed class CodecHealth
{
    private int _failed;

    public VideoCodec Failed => (VideoCodec)Volatile.Read(ref _failed);

    public void MarkFailed(VideoCodec codec)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref _failed);
        }
        while (Interlocked.CompareExchange(ref _failed, seen | (int)codec, seen) != seen);
    }
}
