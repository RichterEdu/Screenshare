using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Video;

/// <summary>Uma NAL unit dentro de um access unit: onde começa o conteúdo (sem o start code), o tamanho e o tipo.</summary>
public readonly record struct NalUnit(int Offset, int Length, int Type);

/// <summary>
/// Leitura de vídeo H.264/H.265 em Annex-B (NAL units separadas por 00 00 01 ou 00 00 00 01), o formato que o
/// encoder entrega e que o FRAME carrega.
/// </summary>
public static class AnnexB
{
    public const int H264Idr = 5;
    public const int H264Sps = 7;
    public const int H264Pps = 8;
    public const int H265IdrWRadl = 19;
    public const int H265IdrNLp = 20;
    public const int H265Cra = 21;
    public const int H265Vps = 32;
    public const int H265Sps = 33;
    public const int H265Pps = 34;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    /// <summary>As NAL units do access unit, na ordem. Bytes antes do primeiro start code são ignorados.</summary>
    public static IReadOnlyList<NalUnit> Split(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        RequireSingleCodec(codec);
        var units = new List<NalUnit>();
        var start = -1;
        var i = 0;
        while (i + 2 < accessUnit.Length)
        {
            if (accessUnit[i] == 0 && accessUnit[i + 1] == 0 && accessUnit[i + 2] == 1)
            {
                if (start >= 0) Add(units, accessUnit, start, i, codec);
                start = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0) Add(units, accessUnit, start, accessUnit.Length, codec);
        return units;
    }

    /// <summary>O tipo da NAL a partir do primeiro byte do cabeçalho (H.264: 5 bits baixos; H.265: bits 1 a 6).</summary>
    public static int NalType(byte header, VideoCodec codec) =>
        RequireSingleCodec(codec) == VideoCodec.H264 ? header & 0x1F : (header >> 1) & 0x3F;

    /// <summary>O access unit tem um IDR (H.264) ou um IDR/CRA (H.265), por onde um decoder pode começar.</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        foreach (var nal in Split(accessUnit, codec))
        {
            if (codec == VideoCodec.H264 ? nal.Type == H264Idr : nal.Type is H265IdrWRadl or H265IdrNLp or H265Cra)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Os parâmetros do access unit (H.264: SPS e PPS; H.265: VPS, SPS e PPS), cada um com start code de 4 bytes,
    /// na ordem em que aparecem — o que vai no codecConfig do CONFIG. Null se faltar algum.
    /// </summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> accessUnit, VideoCodec codec)
    {
        int[] required = codec == VideoCodec.H264 ? [H264Sps, H264Pps] : [H265Vps, H265Sps, H265Pps];
        var found = new HashSet<int>();
        using var output = new MemoryStream();
        foreach (var nal in Split(accessUnit, codec))
        {
            if (Array.IndexOf(required, nal.Type) < 0) continue;
            found.Add(nal.Type);
            output.Write(StartCode);
            output.Write(accessUnit.Slice(nal.Offset, nal.Length));
        }
        return found.Count == required.Length ? output.ToArray() : null;
    }

    private static void Add(List<NalUnit> units, ReadOnlySpan<byte> data, int start, int end, VideoCodec codec)
    {
        // Uma NAL nunca termina em 0x00: os zeros antes de um start code são o zero extra do start code de 4 bytes
        // ou trailing_zero_8bits.
        while (end > start && data[end - 1] == 0) end--;
        if (end > start) units.Add(new NalUnit(start, end - start, NalType(data[start], codec)));
    }

    private static VideoCodec RequireSingleCodec(VideoCodec codec) =>
        codec is VideoCodec.H264 or VideoCodec.H265
            ? codec
            : throw new ArgumentException($"O codec precisa ser H264 ou H265, recebeu {codec}.", nameof(codec));
}
