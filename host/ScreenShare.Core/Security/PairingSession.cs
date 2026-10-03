using System.Security.Cryptography;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Security;

/// <summary>
/// Segredo de pareamento mostrado no QR: 32 bytes aleatórios, válido por <see cref="Lifetime"/>, uso único.
/// Só existe uma sessão por vez: <see cref="Begin"/> de novo invalida a anterior.
/// </summary>
public sealed class PairingSession(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Lock _lock = new();
    private byte[]? _secret;
    private DateTimeOffset _expiresAt;

    /// <summary>Gera um segredo novo (invalidando o anterior) e devolve uma cópia para pôr no QR.</summary>
    public byte[] Begin()
    {
        lock (_lock)
        {
            _secret = RandomNumberGenerator.GetBytes(MessageCodec.SecretLength);
            _expiresAt = clock.GetUtcNow() + Lifetime;
            return (byte[])_secret.Clone();
        }
    }

    /// <summary>True se <paramref name="secret"/> é o segredo atual e ainda vale; nesse caso a sessão é consumida.</summary>
    public bool TryConsume(ReadOnlySpan<byte> secret)
    {
        lock (_lock)
        {
            if (_secret is null) return false;
            if (clock.GetUtcNow() >= _expiresAt)
            {
                _secret = null;
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(_secret, secret)) return false;
            _secret = null;
            return true;
        }
    }
}
