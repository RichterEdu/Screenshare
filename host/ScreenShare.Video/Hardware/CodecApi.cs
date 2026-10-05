using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// ICodecAPI por vtable: o Vortice não mapeia o codecapi.h. Só os dois métodos usados (IsSupported e SetValue), com um
/// VARIANT de 24 bytes (VT_UI4 ou VT_BOOL). GUIDs conferidos no codecapi.h do mingw-w64 durante o spike.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    /// <summary>eAVEncCommonRateControlMode_PeakConstrainedVBR: média com teto (o spike escolheu este).</summary>
    public const uint PeakConstrainedVbr = 1;

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;
    private static readonly Guid Iid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private nint _api;

    public CodecApi(ComObject transform)
    {
        var iid = Iid;
        Marshal.QueryInterface(transform.NativePointer, in iid, out _api);
        if (_api == 0) throw new InvalidOperationException("o encoder não tem ICodecAPI");
    }

    private void** Vtable => *(void***)_api;

    public bool IsSupported(Guid property) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Vtable[3])(_api, &property) == 0;

    /// <summary>Devolve o HRESULT (0 = aceitou).</summary>
    public int SetUInt32(Guid property, uint value) => SetValue(property, new Variant24 { Vt = VtUi4, Value = value });

    public int SetBool(Guid property, bool value) =>
        SetValue(property, new Variant24 { Vt = VtBool, Value = value ? 0xFFFFUL : 0UL });

    public void Dispose()
    {
        if (_api == 0) return;
        Marshal.Release(_api);
        _api = 0;
    }

    private int SetValue(Guid property, Variant24 value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant24*, int>)Vtable[9])(_api, &property, &value);

    [StructLayout(LayoutKind.Sequential, Size = 24)]
    private struct Variant24
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public ulong Value;
        public ulong Padding;
    }
}
