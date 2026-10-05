using ScreenShare.Video.Pipeline;

namespace ScreenShare.Video.Hardware;

internal enum DxgiErrorKind
{
    Timeout,

    /// <summary>A captura parou mas volta: reabrir com espera (UAC, Win+L, troca de modo, driver reiniciando).</summary>
    CaptureLost,

    /// <summary>A placa de vídeo foi removida ou reiniciada: recriar o device e tudo o que depende dele.</summary>
    DeviceLost,
}

/// <summary>Os HRESULTs que a captura DXGI devolve, e o que o pipeline faz com cada um.</summary>
internal static class DxgiErrors
{
    public const int InvalidCall = unchecked((int)0x887A0001);
    public const int NotFound = unchecked((int)0x887A0002);
    public const int Unsupported = unchecked((int)0x887A0004);
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceHung = unchecked((int)0x887A0006);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int DriverInternalError = unchecked((int)0x887A0020);
    public const int NotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int ModeChangeInProgress = unchecked((int)0x887A0025);
    public const int AccessLost = unchecked((int)0x887A0026);
    public const int WaitTimeout = unchecked((int)0x887A0027);
    public const int SessionDisconnected = unchecked((int)0x887A0028);
    public const int AccessDenied = unchecked((int)0x887A002B);

    /// <summary>E_ACCESSDENIED: a área de trabalho segura está aberta (UAC, Win+L); o spike viu este ao reabrir.</summary>
    public const int GeneralAccessDenied = unchecked((int)0x80070005);

    /// <summary>Qualquer erro que não seja de placa é tratado como captura perdida: tentar de novo não custa nada.</summary>
    public static DxgiErrorKind Classify(int hresult) => hresult switch
    {
        WaitTimeout => DxgiErrorKind.Timeout,
        DeviceRemoved or DeviceHung or DeviceReset or DriverInternalError => DxgiErrorKind.DeviceLost,
        _ => DxgiErrorKind.CaptureLost,
    };

    /// <summary>A exceção que o pipeline entende: CaptureLostException (reabrir) ou DeviceLostException (recriar tudo).</summary>
    public static Exception ToException(int hresult, string what, Exception? inner = null)
    {
        var message = $"{what}: 0x{hresult:X8}";
        return Classify(hresult) == DxgiErrorKind.DeviceLost
            ? new DeviceLostException(message, inner)
            : new CaptureLostException(message, inner);
    }
}
