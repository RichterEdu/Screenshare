using System.Text.Json;

namespace ScreenShare.Display;

/// <summary>Posição do monitor virtual na área de trabalho, em pixels (a tela principal começa em 0,0).</summary>
public readonly record struct DisplayPosition(int X, int Y);

/// <summary>
/// O que o host lembra do monitor virtual entre execuções (display.json): as resoluções que já receberam a escala
/// automática, para não sobrescrever a escala que o usuário escolher depois, e a última posição, para respeitar o
/// arranjo feito em Configurações › Tela. Arquivo ilegível conta como vazio: no pior caso a escala automática é
/// aplicada de novo uma vez e o monitor volta à posição padrão, o que nunca impede a conexão.
/// </summary>
public sealed class DisplayStateStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenShare", "display.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class Contents
    {
        public List<string>? Scaled { get; set; }
        public DisplayPosition? LastPosition { get; set; }
    }

    public bool IsScaled(int width, int height) => Load().Scaled!.Contains(Key(width, height));

    public void MarkScaled(int width, int height)
    {
        var contents = Load();
        var key = Key(width, height);
        if (contents.Scaled!.Contains(key)) return;
        contents.Scaled.Add(key);
        contents.Scaled.Sort(StringComparer.Ordinal);
        Save(contents);
    }

    public DisplayPosition? LastPosition => Load().LastPosition;

    public void SaveLastPosition(DisplayPosition position)
    {
        var contents = Load();
        if (contents.LastPosition == position) return;
        contents.LastPosition = position;
        Save(contents);
    }

    private static string Key(int width, int height) => $"{width}x{height}";

    private Contents Load()
    {
        Contents? contents = null;
        try
        {
            contents = JsonSerializer.Deserialize<Contents>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // FileNotFound e DirectoryNotFound são IOException: sem arquivo, nada foi guardado ainda.
        }
        contents ??= new Contents();
        contents.Scaled ??= [];
        return contents;
    }

    private void Save(Contents contents) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(contents, Options));
}
