using System.Security.Cryptography;

namespace ScreenShare.Display.Driver;

/// <summary>O pacote do Virtual Display Driver que o ScreenShare instala (versão fixada; veja o spec da Parte 2).</summary>
public static class VddPackage
{
    /// <summary>Release 25.7.23, asset "Driver Only": apesar do nome x86, traz o driver 24.12.24 para x64.</summary>
    public static readonly Uri Url = new("https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip");

    public const string ReleasePage = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23";
    public const string Sha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a";
    public const string HardwareId = @"Root\MttVDD";
    public const string InfRelativePath = @"VirtualDisplayDriver\MttVDD.inf";
    public const string CatalogRelativePath = @"VirtualDisplayDriver\mttvdd.cat";

    /// <summary>A pasta da configuração do driver (C:\VirtualDisplayDriver).</summary>
    public static string SettingsDirectory { get; } = Path.GetDirectoryName(VddSettingsFile.DefaultPath)!;

    public static bool HasHash(byte[] data, string expectedSha256) =>
        Convert.ToHexString(SHA256.HashData(data)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
}
