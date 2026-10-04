using System.Runtime.InteropServices;

namespace ScreenShare.Display.Native;

/// <summary>Chamadas de tela do Windows (user32): saídas, modos, anexar/desanexar e escala via CCD.</summary>
internal static class DisplayApi
{
    public const uint AttachedToDesktop = 0x1;

    private const int EnumCurrentSettings = -1;
    private const uint DmPosition = 0x20, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000;
    private const uint CdsUpdateRegistry = 0x1, CdsNoReset = 0x10000000;
    private const int DispChangeSuccessful = 0;
    private const uint QdcOnlyActivePaths = 0x2;
    private const int GetSourceName = 1, GetDpiScale = -3, SetDpiScale = -4; // -3/-4: não documentados, usados por Configurações

    public sealed record Adapter(string DeviceName, string DeviceId, uint StateFlags);

    public static IEnumerable<Adapter> Adapters()
    {
        for (uint index = 0; ; index++)
        {
            var device = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevicesW(null, index, ref device, 0)) yield break;
            yield return new Adapter(device.DeviceName, device.DeviceID, device.StateFlags);
        }
    }

    public static (int X, int Y, int Width, int Height)? CurrentMode(string deviceName)
    {
        var mode = NewDevMode();
        return EnumDisplaySettingsExW(deviceName, EnumCurrentSettings, ref mode, 0)
            ? (mode.dmPositionX, mode.dmPositionY, (int)mode.dmPelsWidth, (int)mode.dmPelsHeight)
            : null;
    }

    public static IReadOnlyList<(int Width, int Height)> Modes(string deviceName)
    {
        var modes = new List<(int Width, int Height)>();
        for (var index = 0; ; index++)
        {
            var mode = NewDevMode();
            if (!EnumDisplaySettingsExW(deviceName, index, ref mode, 0)) break;
            var size = ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            if (!modes.Contains(size)) modes.Add(size);
        }
        return modes;
    }

    /// <summary>Anexa (posição + tamanho) ou desanexa (0×0) e aplica, como "Estender"/"Desconectar" em Configurações.</summary>
    public static bool SetPlacement(string deviceName, int x, int y, int width, int height)
    {
        var mode = NewDevMode();
        mode.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
        mode.dmPositionX = x;
        mode.dmPositionY = y;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        if (ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero) != DispChangeSuccessful)
            return false;
        return ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == DispChangeSuccessful;
    }

    public static bool SetSize(string deviceName, int width, int height)
    {
        var mode = NewDevMode();
        mode.dmFields = DmPelsWidth | DmPelsHeight;
        mode.dmPelsWidth = (uint)width;
        mode.dmPelsHeight = (uint)height;
        return ChangeDisplaySettingsExW(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero) == DispChangeSuccessful;
    }

    /// <summary>Escala em passos relativos à recomendada (min, atual, max); null se a saída não está ativa.</summary>
    public static (int Min, int Current, int Max)? GetScaleSteps(string deviceName)
    {
        if (FindSource(deviceName) is not { } source) return null;
        var info = new DPI_GET { header = Header(GetDpiScale, Marshal.SizeOf<DPI_GET>(), source) };
        return DisplayConfigGetDeviceInfo(ref info) == 0 ? (info.minScaleRel, info.curScaleRel, info.maxScaleRel) : null;
    }

    public static bool SetScaleStep(string deviceName, int relativeStep)
    {
        if (FindSource(deviceName) is not { } source) return false;
        var info = new DPI_SET { header = Header(SetDpiScale, Marshal.SizeOf<DPI_SET>(), source), scaleRel = relativeStep };
        return DisplayConfigSetDeviceInfo(ref info) == 0;
    }

    private static (LUID Adapter, uint Id)? FindSource(string deviceName)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return null;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return null;
        for (var index = 0; index < pathCount; index++)
        {
            var source = (paths[index].sourceInfo.adapterId, paths[index].sourceInfo.id);
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = Header(GetSourceName, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), source),
            };
            if (DisplayConfigGetDeviceInfo(ref name) == 0 &&
                string.Equals(name.viewGdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return source;
        }
        return null;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(int type, int size, (LUID Adapter, uint Id) source) =>
        new() { type = type, size = size, adapterId = source.Adapter, id = source.Id };

    private static DEVMODEW NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>(), dmDeviceName = "", dmFormName = "" };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public int size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DPI_GET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int minScaleRel, curScaleRel, maxScaleRel; }
    [StructLayout(LayoutKind.Sequential)] private struct DPI_SET { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public int scaleRel; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint index, ref DISPLAY_DEVICEW displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string device, int modeIndex, ref DEVMODEW mode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, ref DEVMODEW mode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DPI_GET info);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DPI_SET info);
}
