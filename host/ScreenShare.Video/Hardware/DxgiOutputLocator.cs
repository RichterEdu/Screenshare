using Vortice.DXGI;

namespace ScreenShare.Video.Hardware;

/// <summary>Uma saída DXGI e a placa dela; Dispose solta as duas.</summary>
internal sealed class LocatedOutput(IDXGIAdapter1 adapter, IDXGIOutput output) : IDisposable
{
    public IDXGIAdapter1 Adapter { get; } = adapter;
    public IDXGIOutput Output { get; } = output;
    public long AdapterLuid => GpuContext.LuidOf(Adapter.Description1.Luid);

    public void Dispose()
    {
        Output.Dispose();
        Adapter.Dispose();
    }
}

/// <summary>
/// Acha uma saída pelo nome (\\.\DISPLAYn) entre todas as placas. Usa uma factory nova a cada busca: depois de um
/// reinício do driver a lista de uma factory antiga não tem a saída nova.
/// </summary>
internal static class DxgiOutputLocator
{
    public static LocatedOutput? Find(string deviceName) =>
        Find(description => string.Equals(description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>O monitor principal: a saída na origem (0,0) da área de trabalho.</summary>
    public static LocatedOutput? FindPrimary() =>
        Find(description => description.AttachedToDesktop && description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0);

    private static LocatedOutput? Find(Func<OutputDescription, bool> match)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (match(output.Description)) return new LocatedOutput(adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        return null;
    }
}
