namespace ScreenShare.Tests.Protocol;

/// <summary>Carrega os vetores compartilhados de docs/protocol-vectors (pares hex; '#' inicia comentário).</summary>
internal static class Vectors
{
    public static byte[] Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol-vectors", name);
        return File.ReadLines(path)
            .Select(line => line.Split('#')[0])
            .SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(token => Convert.ToByte(token, 16))
            .ToArray();
    }
}
