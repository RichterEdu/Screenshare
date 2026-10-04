using System.Security.Cryptography;
using System.Text;
using ScreenShare.Display;
using ScreenShare.Display.Driver;

namespace ScreenShare.Tests.Display;

public sealed class DriverInstallerTests
{
    private readonly FakeDriverSystem _system = new();
    private readonly List<string> _log = [];

    private DriverInstaller Installer() =>
        new(_system, _log.Add, Convert.ToHexString(SHA256.HashData(_system.DownloadBytes)));

    [Fact]
    public async Task Already_installed_only_prepares_settings()
    {
        _system.Installed = true;

        Assert.Equal(DriverExitCode.Ok, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.False(_system.Downloaded);
        Assert.Null(_system.InstalledInf);
        Assert.Equal("S-1-5-21-1", _system.PreparedSid);
        Assert.Equal(VddSettingsFile.DefaultPath, _system.PreparedPath);
    }

    [Fact]
    public async Task Download_failure_points_to_manual_install()
    {
        _system.DownloadError = new HttpRequestException("sem rede");

        Assert.Equal(DriverExitCode.DownloadFailed, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.InstalledInf);
        Assert.Contains(_log, line => line.Contains(VddPackage.ReleasePage));
    }

    [Fact]
    public async Task Wrong_hash_installs_nothing()
    {
        var installer = new DriverInstaller(_system, _log.Add, VddPackage.Sha256); // os bytes falsos não batem com o hash real

        Assert.Equal(DriverExitCode.HashMismatch, await installer.InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.ExtractedTo);
        Assert.Null(_system.InstalledInf);
        Assert.Null(_system.PreparedSid);
    }

    [Fact]
    public async Task Successful_install_prepares_settings_and_cleans_up()
    {
        Assert.Equal(DriverExitCode.Ok, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Equal(Path.Combine(FakeDriverSystem.TempDirectory, @"VirtualDisplayDriver\MttVDD.inf"), _system.InstalledInf);
        Assert.Equal("S-1-5-21-1", _system.PreparedSid);
        Assert.True(_system.SettingsPreparedBeforeInstall);
        Assert.True(_system.TempDeleted);
    }

    [Fact]
    public async Task Publisher_trusted_by_the_install_is_removed_and_others_stay()
    {
        _system.Trusted.Add("ANTIGO");
        _system.Catalog.UnionWith(["SIGNPATH", "ANTIGO"]);
        _system.TrustedByInstall.UnionWith(["SIGNPATH", "OUTRO"]);

        await Installer().InstallAsync("S-1-5-21-1");

        Assert.Equal(new[] { "SIGNPATH" }, _system.Removed);
        Assert.Contains("ANTIGO", _system.Trusted);
        Assert.Contains("OUTRO", _system.Trusted);
    }

    [Theory]
    [InlineData(1223)]                           // ERROR_CANCELLED
    [InlineData(unchecked((int)0xE0000243))]     // ERROR_AUTHENTICODE_PUBLISHER_NOT_TRUSTED
    [InlineData(unchecked((int)0xE0000242))]     // ERROR_AUTHENTICODE_TRUST_NOT_ESTABLISHED
    public async Task Declined_confirmation_is_reported_as_user_declined(int error)
    {
        _system.InstallError = error;
        _system.Catalog.Add("SIGNPATH");
        _system.TrustedByInstall.Add("SIGNPATH");

        Assert.Equal(DriverExitCode.UserDeclined, await Installer().InstallAsync("S-1-5-21-1"));

        // O XML vem antes do driver; se a instalação é recusada, a pasta fica (uninstall-driver a apaga).
        Assert.Equal("S-1-5-21-1", _system.PreparedSid);
        Assert.True(_system.TempDeleted);
        Assert.Equal(new[] { "SIGNPATH" }, _system.Removed);
        Assert.Contains(_log, line => line.Contains($"0x{error:X8}"));
    }

    [Fact]
    public async Task Signature_mismatch_is_a_setup_failure_not_a_refusal()
    {
        _system.InstallError = unchecked((int)0xE0000244); // ERROR_SIGNATURE_OSATTRIBUTE_MISMATCH

        Assert.Equal(DriverExitCode.SetupFailed, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Contains(_log, line => line.Contains("0xE0000244"));
    }

    [Fact]
    public async Task Catalog_is_read_before_installing()
    {
        await Installer().InstallAsync("S-1-5-21-1");

        Assert.True(_system.CatalogReadBeforeInstall);
    }

    [Fact]
    public async Task Unreadable_catalog_installs_nothing()
    {
        _system.CatalogError = new CryptographicException("catálogo ilegível");

        await Assert.ThrowsAsync<CryptographicException>(() => Installer().InstallAsync("S-1-5-21-1"));

        Assert.Null(_system.InstalledInf);
        Assert.Null(_system.PreparedSid);
        Assert.True(_system.TempDeleted);
    }

    [Fact]
    public async Task Other_setup_errors_are_reported_with_the_windows_code()
    {
        _system.InstallError = 5;

        Assert.Equal(DriverExitCode.SetupFailed, await Installer().InstallAsync("S-1-5-21-1"));

        Assert.Contains(_log, line => line.Contains("0x00000005"));
        Assert.True(_system.TempDeleted);
    }

    [Fact]
    public void Uninstall_removes_devices_and_settings_folder()
    {
        Assert.Equal(DriverExitCode.Ok, Installer().Uninstall());

        Assert.True(_system.Uninstalled);
        Assert.Equal(VddPackage.SettingsDirectory, _system.DeletedSettings);
    }

    [Fact]
    public void Failed_uninstall_keeps_settings_folder()
    {
        _system.UninstallError = 5;

        Assert.Equal(DriverExitCode.SetupFailed, Installer().Uninstall());

        Assert.Null(_system.DeletedSettings);
    }

    [Theory]
    [InlineData(0, DriverExitCode.Ok)]
    [InlineData(3010, DriverExitCode.SetupFailed)]
    public void Restart_maps_pnputil_result(int code, DriverExitCode expected)
    {
        _system.RestartCode = code;

        Assert.Equal(expected, Installer().Restart());
    }

    [Fact]
    public void HasHash_compares_sha256_ignoring_case()
    {
        var abc = Encoding.ASCII.GetBytes("abc");

        Assert.True(VddPackage.HasHash(abc, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.True(VddPackage.HasHash(abc, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        Assert.False(VddPackage.HasHash(abc, VddPackage.Sha256));
    }

    private sealed class FakeDriverSystem : IDriverSystem
    {
        public const string TempDirectory = @"C:\temp\screenshare-vdd-teste";

        public bool Installed { get; set; }
        public byte[] DownloadBytes { get; } = [1, 2, 3];
        public Exception? DownloadError { get; set; }
        public Exception? CatalogError { get; set; }
        public int InstallError { get; set; }
        public int UninstallError { get; set; }
        public int RestartCode { get; set; }
        public HashSet<string> Trusted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Catalog { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TrustedByInstall { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; } = [];
        public bool Downloaded { get; private set; }
        public bool CatalogReadBeforeInstall { get; private set; }
        public bool SettingsPreparedBeforeInstall { get; private set; }
        public string? ExtractedTo { get; private set; }
        public bool TempDeleted { get; private set; }
        public string? InstalledInf { get; private set; }
        public string? PreparedSid { get; private set; }
        public string? PreparedPath { get; private set; }
        public bool Uninstalled { get; private set; }
        public string? DeletedSettings { get; private set; }

        public bool IsInstalled() => Installed;

        public Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken)
        {
            Downloaded = true;
            Assert.Equal(VddPackage.Url, url);
            return DownloadError is { } error ? Task.FromException<byte[]>(error) : Task.FromResult(DownloadBytes);
        }

        public string Extract(byte[] zip) => ExtractedTo = TempDirectory;

        public void DeleteDirectory(string path) => TempDeleted = path == TempDirectory;

        public IReadOnlySet<string> TrustedPublisherThumbprints() => new HashSet<string>(Trusted, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> CatalogThumbprints(string catalogPath)
        {
            Assert.Equal(Path.Combine(TempDirectory, @"VirtualDisplayDriver\mttvdd.cat"), catalogPath);
            if (CatalogError is { } error) throw error;
            CatalogReadBeforeInstall = InstalledInf is null;
            return Catalog;
        }

        public void RemoveTrustedPublishers(IEnumerable<string> thumbprints)
        {
            foreach (var thumbprint in thumbprints)
            {
                Removed.Add(thumbprint);
                Trusted.Remove(thumbprint);
            }
        }

        public int InstallDevice(string infPath)
        {
            InstalledInf = infPath;
            Trusted.UnionWith(TrustedByInstall); // a confirmação do Windows vem com "Sempre confiar" marcado
            return InstallError;
        }

        public void PrepareSettings(string settingsPath, string userSid)
        {
            PreparedPath = settingsPath;
            PreparedSid = userSid;
            SettingsPreparedBeforeInstall = InstalledInf is null;
        }

        public int UninstallDevices()
        {
            Uninstalled = true;
            return UninstallError;
        }

        public void DeleteSettings(string settingsDirectory) => DeletedSettings = settingsDirectory;

        public int RestartDevices() => RestartCode;
    }
}
