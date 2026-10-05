using ScreenShare.Core.Protocol;

namespace ScreenShare.Video;

/// <summary>Ajustes do vídeo vindos da linha de comando do DevHost. Codec None = automático.</summary>
public sealed record VideoOptions(int Fps = 60, int? BitrateMbps = null, VideoCodec Codec = VideoCodec.None)
{
    /// <summary>Bitrate médio: o pedido, ou 50 Mbps no cabo e 25 no Wi-Fi. O pico é o dobro.</summary>
    public int BitrateKbpsFor(VideoLink link) => (BitrateMbps ?? (link == VideoLink.Usb ? 50 : 25)) * 1000;
}
