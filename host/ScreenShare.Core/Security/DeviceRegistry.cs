using System.Security.Cryptography;
using System.Text.Json;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Core.Security;

/// <summary>Um celular pareado. Só o SHA-256 (hex) da chave de acesso é guardado.</summary>
public sealed record PairedDevice(string Id, string Name, string TokenSha256, DateTimeOffset PairedAt);

/// <summary>
/// Lista de celulares pareados, persistida em JSON. Thread-safe.
/// Arquivo corrompido: registra no log, guarda uma cópia em ".bak" e começa vazio.
/// </summary>
public sealed class DeviceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private readonly List<PairedDevice> _devices;

    public DeviceRegistry(string path, TimeProvider? clock = null, Action<string>? log = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        _devices = Load(log);
    }

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_lock) return _devices.ToArray();
        }
    }

    /// <summary>Registra um celular novo e devolve a chave de acesso (a única vez em que ela existe em claro no PC).</summary>
    public (PairedDevice Device, byte[] Token) Add(string name)
    {
        var token = RandomNumberGenerator.GetBytes(MessageCodec.TokenLength);
        var device = new PairedDevice(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), name,
            Convert.ToHexStringLower(SHA256.HashData(token)), _clock.GetUtcNow());
        lock (_lock)
        {
            _devices.Add(device);
            Save();
        }
        return (device, token);
    }

    /// <summary>O aparelho dono da chave, ou null. Compara os hashes em tempo constante.</summary>
    public PairedDevice? Authenticate(ReadOnlySpan<byte> token)
    {
        if (token.Length != MessageCodec.TokenLength) return null;
        var hash = SHA256.HashData(token);
        lock (_lock)
        {
            PairedDevice? match = null;
            foreach (var device in _devices)
            {
                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(device.TokenSha256), hash))
                    match = device;
            }
            return match;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (_devices.RemoveAll(d => d.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    private List<PairedDevice> Load(Action<string>? log)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException e)
        {
            File.Copy(_path, _path + ".bak", overwrite: true);
            log?.Invoke($"Lista de aparelhos pareados corrompida ({e.Message}). Começando vazia; cópia em {_path}.bak");
            return [];
        }
    }

    /// <summary>Grava num arquivo temporário e renomeia, para nunca deixar o JSON pela metade.</summary>
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, JsonOptions));
        File.Move(temporary, _path, overwrite: true);
    }
}
