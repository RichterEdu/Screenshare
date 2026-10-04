using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "paired-devices.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Added_device_authenticates_with_its_token()
    {
        var clock = new ManualClock();
        var registry = new DeviceRegistry(FilePath, clock);

        var (device, token) = registry.Add("Pixel 8");

        Assert.Equal(32, token.Length);
        Assert.Equal(device, registry.Authenticate(token));
        Assert.Equal("Pixel 8", device.Name);
        Assert.Equal(clock.Now, device.PairedAt);
    }

    [Fact]
    public void Unknown_or_malformed_token_does_not_authenticate()
    {
        var registry = new DeviceRegistry(FilePath);
        registry.Add("Pixel 8");

        Assert.Null(registry.Authenticate(new byte[32]));
        Assert.Null(registry.Authenticate(new byte[31]));
    }

    [Fact]
    public void Removed_device_no_longer_authenticates()
    {
        var registry = new DeviceRegistry(FilePath);
        var (device, token) = registry.Add("Pixel 8");

        Assert.True(registry.Remove(device.Id));

        Assert.Null(registry.Authenticate(token));
        Assert.Empty(registry.Devices);
        Assert.False(registry.Remove(device.Id));
    }

    [Fact]
    public void Devices_survive_a_restart()
    {
        var (device, token) = new DeviceRegistry(FilePath).Add("Pixel 8");

        var reloaded = new DeviceRegistry(FilePath);

        Assert.Equal(device, reloaded.Authenticate(token));
        Assert.Equal(new[] { device }, reloaded.Devices);
    }

    [Fact]
    public void File_stores_only_the_token_hash()
    {
        var (_, token) = new DeviceRegistry(FilePath).Add("Pixel 8");

        var json = File.ReadAllText(FilePath);

        Assert.DoesNotContain(Convert.ToHexString(token), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(token), json);
        Assert.Contains("tokenSha256", json);
    }

    [Fact]
    public void Corrupted_file_starts_empty_keeps_a_backup_and_logs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ isso não é json");
        var logs = new List<string>();

        var registry = new DeviceRegistry(FilePath, log: logs.Add);

        Assert.Empty(registry.Devices);
        Assert.Equal("{ isso não é json", File.ReadAllText(FilePath + ".bak"));
        Assert.Single(logs);
        var (device, token) = registry.Add("Pixel 8"); // continua funcionando depois
        Assert.Equal(device, new DeviceRegistry(FilePath).Authenticate(token));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"a\",\"name\":\"x\",\"pairedAt\":\"2026-10-03T12:00:00+00:00\"}]")]
    [InlineData("[{\"id\":\"a\",\"name\":\"x\",\"tokenSha256\":\"zz\",\"pairedAt\":\"2026-10-03T12:00:00+00:00\"}]")]
    [InlineData("[{\"id\":\"a\",\"name\":\"x\",\"tokenSha256\":\"abc\",\"pairedAt\":\"2026-10-03T12:00:00+00:00\"}]")]
    public void Malformed_entries_treated_as_corruption(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        var logs = new List<string>();

        var registry = new DeviceRegistry(FilePath, log: logs.Add);

        Assert.Empty(registry.Devices);
        Assert.Equal(json, File.ReadAllText(FilePath + ".bak"));
        Assert.Single(logs);
        var (device, token) = registry.Add("Pixel 8");
        Assert.Equal(device, new DeviceRegistry(FilePath).Authenticate(token));
    }

    [Fact]
    public void Removal_survives_a_restart()
    {
        var (device, token) = new DeviceRegistry(FilePath).Add("Pixel 8");

        new DeviceRegistry(FilePath).Remove(device.Id);

        var reloaded = new DeviceRegistry(FilePath);
        Assert.Null(reloaded.Authenticate(token));
        Assert.Empty(reloaded.Devices);
    }

    [Fact]
    public void Failed_save_on_remove_keeps_the_device()
    {
        var registry = new DeviceRegistry(FilePath);
        var (device, token) = registry.Add("Pixel 8");

        File.SetAttributes(FilePath, FileAttributes.ReadOnly);
        try
        {
            var ex = Record.Exception(() => registry.Remove(device.Id));
            Assert.NotNull(ex);
            Assert.True(ex is IOException or UnauthorizedAccessException, $"Expected IOException or UnauthorizedAccessException, got {ex?.GetType().Name}");

            Assert.Equal(device, registry.Authenticate(token));
            var reloaded = new DeviceRegistry(FilePath);
            Assert.Equal(device, reloaded.Authenticate(token));
        }
        finally
        {
            File.SetAttributes(FilePath, FileAttributes.Normal);
        }
    }
}
