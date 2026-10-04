using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using ScreenShare.Display.Native;

namespace ScreenShare.Display.Driver;

/// <summary>As operações reais do Windows para instalar o Virtual Display Driver (veja <see cref="IDriverSystem"/>).</summary>
public sealed class WindowsDriverSystem : IDriverSystem
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public bool IsInstalled() => SetupApi.FindDevices(VddPackage.HardwareId, presentOnly: true).Count > 0;

    public Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken) => Http.GetByteArrayAsync(url, cancellationToken);

    public string Extract(byte[] zip)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ScreenShare-VDD-{Guid.NewGuid():N}");
        using var stream = new MemoryStream(zip);
        ZipFile.ExtractToDirectory(stream, directory);
        return directory;
    }

    public void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pasta temporária: se não sair agora, o Windows limpa depois
        }
    }

    public IReadOnlySet<string> TrustedPublisherThumbprints()
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Select(certificate => certificate.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> CatalogThumbprints(string catalogPath)
    {
        // O .cat é um PKCS#7 assinado: os certificados (editor, cadeia, carimbo de tempo) vêm junto.
        var catalog = new SignedCms();
        catalog.Decode(File.ReadAllBytes(catalogPath));
        return catalog.Certificates.Select(certificate => certificate.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void RemoveTrustedPublishers(IEnumerable<string> thumbprints)
    {
        using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var thumbprint in thumbprints)
            store.RemoveRange(store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false));
    }

    public int InstallDevice(string infPath) => SetupApi.InstallRootDevice(VddPackage.HardwareId, Path.GetFullPath(infPath));

    public void PrepareSettings(string settingsPath, string userSid)
    {
        if (!File.Exists(settingsPath))
            VddSettingsFile.WriteMinimal(settingsPath, monitorCount: 1, [(1920, 1080)]);

        // O host (sem admin) acrescenta resoluções; o processo do driver (LOCAL SERVICE) também grava no XML.
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
        var security = directory.GetAccessControl();
        foreach (var sid in new[] { new SecurityIdentifier(userSid), new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        directory.SetAccessControl(security);
    }

    public int UninstallDevices() => SetupApi.RemoveDevicesAndPackages(VddPackage.HardwareId);

    public void DeleteSettings(string settingsDirectory)
    {
        if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, recursive: true);
    }

    public int RestartDevices()
    {
        foreach (var device in SetupApi.FindDevices(VddPackage.HardwareId, presentOnly: true))
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("/restart-device");
            start.ArgumentList.Add(device.InstanceId);
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return process.ExitCode;
        }
        return 0;
    }
}
