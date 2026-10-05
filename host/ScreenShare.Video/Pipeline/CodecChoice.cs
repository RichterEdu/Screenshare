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

/// <summary>
/// Os codecs que já produziram vídeo nesta execução do host e os que falharam (estes ficam de fora até o host
/// reiniciar). Um codec que já funcionou nunca é marcado como falho: a falha dele é da placa ou passageira.
/// Compartilhado entre sessões.
/// </summary>
public sealed class CodecHealth
{
    private int _failed;
    private int _worked;

    public VideoCodec Failed => (VideoCodec)Volatile.Read(ref _failed);

    public void MarkFailed(VideoCodec codec) => Add(ref _failed, codec);

    public void MarkWorked(VideoCodec codec) => Add(ref _worked, codec);

    public bool HasWorked(VideoCodec codec) => ((VideoCodec)Volatile.Read(ref _worked) & codec) == codec;

    private static void Add(ref int field, VideoCodec codec)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref field);
        }
        while (Interlocked.CompareExchange(ref field, seen | (int)codec, seen) != seen);
    }
}
