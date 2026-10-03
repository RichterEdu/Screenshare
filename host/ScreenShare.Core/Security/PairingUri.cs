using System.Buffers.Text;

namespace ScreenShare.Core.Security;

/// <summary>Monta a URI que vai no QR de pareamento (formato em docs/protocol-vectors/pairing-uri.txt).</summary>
public static class PairingUri
{
    public static string Build(string host, int port, string fingerprint, ReadOnlySpan<byte> secret, string pcName) =>
        $"screenshare://pair?h={Uri.EscapeDataString(host)}&p={port}&fp={fingerprint}" +
        $"&s={Base64Url.EncodeToString(secret)}&n={Uri.EscapeDataString(pcName)}";
}
