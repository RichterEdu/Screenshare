using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public sealed class HostIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Creates_a_certificate_once_and_reloads_the_same_fingerprint()
    {
        string first;
        using (var identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste"))
        {
            first = identity.Fingerprint;
            Assert.True(identity.Certificate.HasPrivateKey);
            Assert.Equal("CN=PC de Teste", identity.Certificate.Subject);
        }

        using var reloaded = HostIdentity.LoadOrCreate(_dir, "PC de Teste");

        Assert.Equal(first, reloaded.Fingerprint);
        Assert.True(File.Exists(Path.Combine(_dir, "host-cert.pfx")));
        Assert.True(File.Exists(Path.Combine(_dir, "host-cert.key")));
    }

    [Fact]
    public void Deleting_the_certificate_creates_a_new_identity()
    {
        string first;
        using (var identity = HostIdentity.LoadOrCreate(_dir, "PC")) first = identity.Fingerprint;
        File.Delete(Path.Combine(_dir, "host-cert.pfx"));

        using var regenerated = HostIdentity.LoadOrCreate(_dir, "PC");

        Assert.NotEqual(first, regenerated.Fingerprint);
    }

    [Fact]
    public void Fingerprint_is_base64url_of_the_sha256_of_the_certificate()
    {
        using var identity = HostIdentity.LoadOrCreate(_dir, "PC");

        Assert.Equal(43, identity.Fingerprint.Length); // 32 bytes em base64url sem padding
        Assert.Equal(identity.Fingerprint, HostIdentity.ComputeFingerprint(identity.Certificate.RawData));
        Assert.DoesNotContain('+', identity.Fingerprint);
        Assert.DoesNotContain('/', identity.Fingerprint);
        Assert.DoesNotContain('=', identity.Fingerprint);
    }

    [Fact]
    public void Mdns_id_is_the_lowercase_hex_of_the_first_eight_bytes()
    {
        // bytes 0x20..0x3F em base64url (mesmo exemplo do teste Kotlin)
        Assert.Equal("2021222324252627", HostIdentity.MdnsIdOf("ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8"));

        using var identity = HostIdentity.LoadOrCreate(_dir, "PC");
        Assert.Equal(HostIdentity.MdnsIdOf(identity.Fingerprint), identity.MdnsId);
    }
}
