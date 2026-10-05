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

    /// <summary>MFT_ENUM_ADAPTER_LUID: a placa a que o encoder de hardware pertence.</summary>
    private static readonly Guid AdapterLuidKey = new("1d39518c-e220-4da8-a07f-ba172552d6b1");

    /// <summary>
    /// O encoder do codec na mesma placa (LUID) da captura; se o driver não informar a placa, o primeiro do mesmo
    /// fabricante (0x10DE = NVIDIA). null se não houver.
    /// </summary>
    public static IMFActivate? Find(VideoCodec codec, uint vendorId, long adapterLuid)
    {
        var vendor = $"VEN_{vendorId:X4}";
        var sameVendor = List(codec)
            .Where(activate => string.Equals(VendorOf(activate), vendor, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return sameVendor.FirstOrDefault(activate => LuidOf(activate) == adapterLuid) ?? sameVendor.FirstOrDefault();
    }

    private static long? LuidOf(IMFActivate activate)
    {
        try
        {
            return (long)activate.GetUInt64(AdapterLuidKey);
        }
        catch (Exception)
        {
            return null;
        }
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
