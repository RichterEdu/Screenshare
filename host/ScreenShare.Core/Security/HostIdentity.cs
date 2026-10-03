using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ScreenShare.Core.Security;

/// <summary>
/// Certificado autoassinado do PC (ECDSA P-256), criado na primeira execução e reaproveitado depois.
/// O celular fixa a digital (SHA-256 do certificado) no pareamento e recusa qualquer outro certificado.
/// </summary>
public sealed class HostIdentity : IDisposable
{
    private const string CertFileName = "host-cert.pfx";
    private const string KeyFileName = "host-cert.key";

    private HostIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        Fingerprint = ComputeFingerprint(certificate.RawData);
        MdnsId = MdnsIdOf(Fingerprint);
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 do certificado em DER, em base64url sem padding (vai no QR).</summary>
    public string Fingerprint { get; }

    /// <summary>16 primeiros caracteres hex do SHA-256 (vai no TXT `fp` do mDNS).</summary>
    public string MdnsId { get; }

    /// <summary>Carrega o certificado de <paramref name="directory"/> ou cria um novo se não existir.</summary>
    public static HostIdentity LoadOrCreate(string directory, string commonName)
    {
        Directory.CreateDirectory(directory);
        var certPath = Path.Combine(directory, CertFileName);
        var keyPath = Path.Combine(directory, KeyFileName);
        if (!File.Exists(certPath) || !File.Exists(keyPath))
            Create(certPath, keyPath, commonName);

        var password = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(keyPath)));
        return new HostIdentity(X509CertificateLoader.LoadPkcs12FromFile(certPath, password));
    }

    public static string ComputeFingerprint(ReadOnlySpan<byte> der) => Base64Url.EncodeToString(SHA256.HashData(der));

    public static string MdnsIdOf(string fingerprint) =>
        Convert.ToHexStringLower(Base64Url.DecodeFromChars(fingerprint).AsSpan(0, 8));

    public void Dispose() => Certificate.Dispose();

    private static void Create(string certPath, string keyPath, string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(commonName);
        var request = new CertificateRequest(name.Build(), key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        // Senha aleatória para o PFX, guardada protegida pelo DPAPI do usuário atual.
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(certPath, certificate.Export(X509ContentType.Pfx, password));
        File.WriteAllBytes(keyPath, Protect(Encoding.UTF8.GetBytes(password)));
    }

    private static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O host do ScreenShare só roda no Windows.");
        return ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O host do ScreenShare só roda no Windows.");
        return ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
