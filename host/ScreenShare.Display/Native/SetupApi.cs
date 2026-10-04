using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenShare.Display.Native;

/// <summary>SetupAPI (setupapi.dll/newdev.dll): criar, achar e remover dispositivos de exibição criados pela raiz.</summary>
internal static class SetupApi
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly DEVPROPKEY DriverInfPath = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };

    private const int DigcfPresent = 0x2;
    private const int DicdGenerateId = 0x1;
    private const int SpdrpHardwareId = 0x1;
    private const int DifRegisterDevice = 0x19;
    private const int DifRemove = 0x05;
    private const int SuoiForceDelete = 0x1;

    public sealed record Device(string InstanceId, string? InfName);

    public static List<Device> FindDevices(string hardwareId, bool presentOnly)
    {
        var devices = new List<Device>();
        ForEachDevice(hardwareId, presentOnly, (set, data) => devices.Add(new Device(InstanceId(set, data), InfName(set, data))));
        return devices;
    }

    /// <summary>Cria o dispositivo pela raiz e instala o driver do .inf. Devolve 0 ou o erro do Windows.</summary>
    public static int InstallRootDevice(string hardwareId, string infPath)
    {
        var classGuid = DisplayClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == InvalidHandle) return Marshal.GetLastWin32Error();
        try
        {
            var data = NewData();
            if (!SetupDiCreateDeviceInfoW(set, "Display", ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref data))
                return Marshal.GetLastWin32Error();
            var ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0"); // multi-sz
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, ids, ids.Length))
                return Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DifRegisterDevice, set, ref data))
                return Marshal.GetLastWin32Error();
            if (UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, 0, out _))
                return 0;
            var error = Marshal.GetLastWin32Error();
            SetupDiCallClassInstaller(DifRemove, set, ref data); // não deixa um dispositivo sem driver para trás
            return error;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>Remove os dispositivos (presentes ou não) e o pacote oemNN.inf. Devolve 0 ou o primeiro erro.</summary>
    public static int RemoveDevicesAndPackages(string hardwareId)
    {
        var infs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var error = 0;
        ForEachDevice(hardwareId, presentOnly: false, (set, data) =>
        {
            if (InfName(set, data) is { } inf) infs.Add(inf);
            if (!SetupDiCallClassInstaller(DifRemove, set, ref data) && error == 0) error = Marshal.GetLastWin32Error();
        });
        foreach (var inf in infs.Where(name => name.StartsWith("oem", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SetupUninstallOEMInfW(inf, SuoiForceDelete, IntPtr.Zero) && error == 0) error = Marshal.GetLastWin32Error();
        }
        return error;
    }

    private delegate void DeviceAction(IntPtr set, SP_DEVINFO_DATA data);

    private static void ForEachDevice(string hardwareId, bool presentOnly, DeviceAction action)
    {
        var classGuid = DisplayClass;
        var set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, presentOnly ? DigcfPresent : 0);
        if (set == InvalidHandle) throw new Win32Exception();
        try
        {
            // Junta antes de agir: remover durante a enumeração por índice pularia dispositivos.
            var matches = new List<SP_DEVINFO_DATA>();
            for (var index = 0; ; index++)
            {
                var data = NewData();
                if (!SetupDiEnumDeviceInfo(set, index, ref data)) break;
                if (HardwareIds(set, data).Contains(hardwareId, StringComparer.OrdinalIgnoreCase)) matches.Add(data);
            }
            foreach (var data in matches) action(set, data);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string[] HardwareIds(IntPtr set, SP_DEVINFO_DATA data)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, out _, buffer, buffer.Length, out var required))
            return [];
        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string? InfName(IntPtr set, SP_DEVINFO_DATA data)
    {
        var key = DriverInfPath;
        var buffer = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buffer, buffer.Length, out var required, 0)) return null;
        return Encoding.Unicode.GetString(buffer, 0, Math.Min(required, buffer.Length)).TrimEnd('\0');
    }

    private static string InstanceId(IntPtr set, SP_DEVINFO_DATA data)
    {
        var id = new StringBuilder(512);
        return SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, out _) ? id.ToString() : "";
    }

    private static SP_DEVINFO_DATA NewData() => new() { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description,
        IntPtr hwndParent, int creationFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property,
        out int registryType, byte[] buffer, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int function, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint propertyType, byte[] buffer, int size, out int required, int flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupUninstallOEMInfW(string infFileName, int flags, IntPtr reserved);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId, string fullInfPath,
        int installFlags, out bool rebootRequired);
}
