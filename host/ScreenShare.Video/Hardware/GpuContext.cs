using ScreenShare.Video.Pipeline;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// O device D3D11 da placa onde está a saída capturada: captura, conversão para NV12 e encoder usam este, sem cópias
/// entre placas. Protegido para várias threads (a de captura e a de eventos do encoder).
/// </summary>
internal sealed class GpuContext : IDisposable
{
    private GpuContext(ID3D11Device device, ID3D11DeviceContext context, uint vendorId, long adapterLuid)
    {
        Device = device;
        Context = context;
        VendorId = vendorId;
        AdapterLuid = adapterLuid;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    /// <summary>Fabricante da placa (0x10DE = NVIDIA): o encoder de hardware é escolhido pelo mesmo fabricante.</summary>
    public uint VendorId { get; }

    public long AdapterLuid { get; }

    /// <summary>adapter null = a placa padrão (testes de GPU sem captura).</summary>
    public static GpuContext Create(IDXGIAdapter1? adapter)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var result = D3D11.D3D11CreateDevice(adapter, adapter is null ? DriverType.Hardware : DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels, out ID3D11Device device,
            out ID3D11DeviceContext context);
        if (result.Failure) throw new DeviceLostException($"D3D11CreateDevice: 0x{result.Code:X8}");

        using (var multithread = device.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var actual = dxgiDevice.GetAdapter();
        var description = actual.Description;
        return new GpuContext(device, context, description.VendorId, LuidOf(description.Luid));
    }

    public static long LuidOf(Vortice.Luid luid) => ((long)luid.HighPart << 32) | luid.LowPart;

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}
