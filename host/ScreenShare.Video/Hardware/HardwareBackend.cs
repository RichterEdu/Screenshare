using ScreenShare.Core.Protocol;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// A placa de vídeo de verdade: captura DXGI e encoder Media Foundation no mesmo device D3D11, criado na placa da
/// primeira saída aberta. Se a saída aparecer em outra placa, pede para recriar tudo (DeviceLostException).
/// </summary>
internal sealed class HardwareBackend : IVideoBackend
{
    private GpuContext? _gpu;

    public VideoCodec HardwareCodecs => EncoderCatalog.HardwareCodecs;

    public IScreenCapture OpenCapture(string deviceName)
    {
        using var located = DxgiOutputLocator.Find(deviceName) ?? throw new CaptureLostException($"saída {deviceName} não encontrada");
        if (_gpu is null) _gpu = GpuContext.Create(located.Adapter);
        else if (_gpu.AdapterLuid != located.AdapterLuid) throw new DeviceLostException("a saída passou para outra placa de vídeo");
        try
        {
            return new DesktopDuplicationCapture(_gpu, located.Output);
        }
        catch (SharpGenException e)
        {
            throw DxgiErrors.ToException(e.HResult, "abrir a captura", e);
        }
    }

    public IVideoEncoder CreateEncoder(EncoderSettings settings)
    {
        var gpu = _gpu ?? throw new InvalidOperationException("a captura abre antes do encoder");
        var activate = EncoderCatalog.Find(settings.Codec, gpu.VendorId)
            ?? throw new EncoderUnavailableException($"a placa não tem encoder {CodecChooser.Name(settings.Codec)}");
        try
        {
            return new MediaFoundationEncoder(gpu, activate, settings);
        }
        catch (SharpGenException e) when (DxgiErrors.IsDeviceLost(e.HResult))
        {
            throw new DeviceLostException($"{EncoderCatalog.NameOf(activate)}: 0x{e.HResult:X8}", e);
        }
        catch (SharpGenException e)
        {
            throw new EncoderUnavailableException($"{EncoderCatalog.NameOf(activate)}: 0x{e.HResult:X8}", e);
        }
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
    }
}
