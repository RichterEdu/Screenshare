using ScreenShare.Core.Protocol;

namespace ScreenShare.Video.Pipeline;

/// <summary>Uma imagem capturada (no hardware de verdade, uma textura na placa de vídeo).</summary>
public interface IVideoImage
{
    int Width { get; }
    int Height { get; }
}

public enum AcquireResult
{
    /// <summary>A tela mudou: Last tem a imagem nova.</summary>
    NewImage,

    /// <summary>Só o ponteiro do mouse mudou (sem cursor no vídeo, é o mesmo que Timeout).</summary>
    PointerOnly,

    Timeout,
}

/// <summary>A captura parou (ACCESS_LOST, área de trabalho segura, saída sumida): reabrir com espera.</summary>
public sealed class CaptureLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A placa de vídeo foi removida ou reiniciada (ou a saída mudou de placa): recriar o backend inteiro.</summary>
public sealed class DeviceLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Não há encoder para este codec, ou ele não abriu.</summary>
public sealed class EncoderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A captura de uma saída. Só a thread de captura usa.</summary>
public interface IScreenCapture : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>A última imagem nova, numa cópia própria: vale até a próxima NewImage.</summary>
    IVideoImage Last { get; }

    /// <summary>
    /// Espera até timeout por uma imagem nova. presentUs = quando ela foi apresentada, no relógio do PC (µs), ou 0.
    /// Lança CaptureLostException (reabrir) ou DeviceLostException (recriar tudo).
    /// </summary>
    AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs);
}

public sealed record EncoderSettings(VideoCodec Codec, int Width, int Height, int Fps, int BitrateKbps, int PeakBitrateKbps);

/// <summary>Um access unit em Annex-B. Data é uma cópia, de quem recebe.</summary>
public sealed record EncodedFrame(ulong TimestampUs, bool IsKeyframe, byte[] Data);

public interface IVideoEncoder : IDisposable
{
    VideoCodec Codec { get; }
    int Width { get; }
    int Height { get; }

    /// <summary>O encoder pediu entrada: Submit pode ser chamado.</summary>
    bool CanAccept { get; }

    void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe);

    /// <summary>Saída codificada, na thread do encoder.</summary>
    event Action<EncodedFrame>? Output;

    event Action<Exception>? Failed;
}

/// <summary>A placa de vídeo: captura e encoder no mesmo device.</summary>
public interface IVideoBackend : IDisposable
{
    /// <summary>Codecs que têm encoder de hardware.</summary>
    VideoCodec HardwareCodecs { get; }

    /// <summary>Abre a captura da saída; CaptureLostException se ela não está disponível agora.</summary>
    IScreenCapture OpenCapture(string deviceName);

    /// <summary>EncoderUnavailableException se não há encoder ou ele não abre.</summary>
    IVideoEncoder CreateEncoder(EncoderSettings settings);
}
