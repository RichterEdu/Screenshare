using ScreenShare.Core.Protocol;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Os encoders de vídeo de hardware (MFTs) do Windows. A coleção do MFTEnumEx solta os itens quando é descartada, então
/// as listas ficam vivas enquanto o host roda (um IMFActivate pode ser ativado de novo depois de ShutdownObject).
/// </summary>
internal static class EncoderCatalog
{
    private const uint Hardware = 0x4;
    private const uint SortAndFilter = 0x40;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<VideoCodec, IMFActivate[]> Lists = [];
    private static readonly List<IDisposable> KeepAlive = [];
    private static bool _started;

    /// <summary>Codecs com pelo menos um encoder de hardware (de qualquer placa). None se o Media Foundation não existe.</summary>
    public static VideoCodec HardwareCodecs
    {
        get
        {
            try
            {
                return (List(VideoCodec.H264).Length > 0 ? VideoCodec.H264 : VideoCodec.None)
                    | (List(VideoCodec.H265).Length > 0 ? VideoCodec.H265 : VideoCodec.None);
            }
            catch (Exception)
            {
                return VideoCodec.None; // Windows sem Media Foundation (edições N sem o pacote de mídia)
            }
        }
    }

    /// <summary>O encoder do codec feito pelo mesmo fabricante da placa (0x10DE = NVIDIA), ou null.</summary>
    public static IMFActivate? Find(VideoCodec codec, uint vendorId)
    {
        var vendor = $"VEN_{vendorId:X4}";
        return List(codec).FirstOrDefault(activate =>
            string.Equals(VendorOf(activate), vendor, StringComparison.OrdinalIgnoreCase));
    }

    public static string NameOf(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch (Exception)
        {
            return "encoder sem nome";
        }
    }

    private static string? VendorOf(IMFActivate activate)
    {
        try
        {
            return activate.GetString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IMFActivate[] List(VideoCodec codec)
    {
        lock (Gate)
        {
            if (Lists.TryGetValue(codec, out var cached)) return cached;
            if (!_started)
            {
                MediaFactory.MFStartup(true).CheckError();
                _started = true;
            }
            var output = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = codec == VideoCodec.H265 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264,
            };
            var collection = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, Hardware | SortAndFilter, null, output);
            KeepAlive.Add(collection);
            var items = collection.ToArray();
            Lists[codec] = items;
            return items;
        }
    }
}
