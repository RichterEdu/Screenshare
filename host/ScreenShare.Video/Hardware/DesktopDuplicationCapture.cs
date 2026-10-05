using System.Diagnostics;
using ScreenShare.Core.Video;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>Imagem BGRA na placa de vídeo (a cópia própria da captura).</summary>
internal sealed class GpuImage(ID3D11Texture2D texture, int width, int height) : IVideoImage
{
    public ID3D11Texture2D Texture { get; } = texture;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

/// <summary>
/// Captura de uma saída com DXGI Desktop Duplication. Cada imagem nova é copiada para uma textura própria (Last) e o
/// quadro do Windows é liberado na hora, para o Windows continuar juntando as mudanças. Precisa de DPI por monitor na
/// thread que a cria e usa (CaptureThread.Prepare).
/// </summary>
internal sealed class DesktopDuplicationCapture : IScreenCapture
{
    private readonly GpuContext _gpu;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _copy;

    public DesktopDuplicationCapture(GpuContext gpu, IDXGIOutput output)
    {
        _gpu = gpu;
        _duplication = Duplicate(gpu, output);
        var mode = _duplication.Description.ModeDescription;
        Width = (int)mode.Width;
        Height = (int)mode.Height;
        _copy = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)Width, (uint)Height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        Last = new GpuImage(_copy, Width, Height);
    }

    public int Width { get; }
    public int Height { get; }
    public IVideoImage Last { get; }

    public AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs)
    {
        presentUs = 0;
        var result = _duplication.AcquireNextFrame((uint)timeout.TotalMilliseconds, out var info, out var resource);
        if (result.Code == DxgiErrors.WaitTimeout) return AcquireResult.Timeout;
        if (result.Failure) throw DxgiErrors.ToException(result.Code, "AcquireNextFrame");
        try
        {
            if (info.LastPresentTime == 0) return AcquireResult.PointerOnly; // só o mouse mexeu
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            _gpu.Context.CopyResource(_copy, texture);
            presentUs = PcClock.ToMicroseconds(info.LastPresentTime, Stopwatch.Frequency);
            return AcquireResult.NewImage;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame(); // se falhar (ACCESS_LOST), a próxima AcquireNextFrame avisa
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        _copy.Dispose();
    }

    private static IDXGIOutputDuplication Duplicate(GpuContext gpu, IDXGIOutput output)
    {
        try
        {
            try
            {
                using var output5 = output.QueryInterface<IDXGIOutput5>();
                return output5.DuplicateOutput1(gpu.Device, [Format.B8G8R8A8_UNorm]);
            }
            catch (SharpGenException e) when (e.HResult == DxgiErrors.Unsupported)
            {
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                return output1.DuplicateOutput(gpu.Device);
            }
        }
        catch (SharpGenException e)
        {
            throw DxgiErrors.ToException(e.HResult, "duplicar a saída", e);
        }
    }
}
