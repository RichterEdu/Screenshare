using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ScreenShare.Display;

/// <summary>O vdd_settings.xml não pôde ser lido ou não tem o formato do VDD.</summary>
public sealed class VddSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lê e grava o vdd_settings.xml do Virtual Display Driver. Só mexe no que precisa (a lista de resoluções) e
/// preserva o resto do arquivo, inclusive comentários e elementos que não conhece. Toda gravação é atômica.
/// </summary>
public static class VddSettingsFile
{
    public const string DefaultPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    /// <summary>Taxa de atualização usada nos modos que o ScreenShare acrescenta.</summary>
    private const int RefreshRate = 60;

    /// <summary>
    /// Garante que a resolução <paramref name="width"/>×<paramref name="height"/> está na lista (qualquer taxa
    /// conta). Devolve true se precisou acrescentar. Sem arquivo, cria o mínimo com um monitor e esse modo.
    /// </summary>
    public static bool EnsureMode(string path, int width, int height)
    {
        if (!File.Exists(path))
        {
            WriteMinimal(path, monitorCount: 1, [(width, height)]);
            return true;
        }

        var doc = Load(path);
        if (ReadModes(doc).Contains((width, height))) return false;

        var root = doc.Root!;
        var resolutions = root.Element("resolutions");
        if (resolutions is null)
        {
            resolutions = new XElement("resolutions");
            root.Add(resolutions);
        }
        resolutions.Add(Resolution(width, height));
        Save(doc, path);
        return true;
    }

    /// <summary>Cria (ou substitui) o arquivo com o mínimo que o driver precisa.</summary>
    public static void WriteMinimal(string path, int monitorCount, IEnumerable<(int Width, int Height)> modes)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("vdd_settings",
                new XElement("monitors", new XElement("count", monitorCount.ToString(CultureInfo.InvariantCulture))),
                new XElement("gpu", new XElement("friendlyname", "default")),
                new XElement("global", new XElement("g_refresh_rate", RefreshRate.ToString(CultureInfo.InvariantCulture))),
                new XElement("resolutions", modes.Select(m => Resolution(m.Width, m.Height)))));
        Save(doc, path);
    }

    /// <summary>As resoluções da lista, na ordem do arquivo; entradas que não são números são ignoradas.</summary>
    public static IReadOnlyList<(int Width, int Height)> ReadModes(string path) => ReadModes(Load(path));

    private static List<(int Width, int Height)> ReadModes(XDocument doc)
    {
        var modes = new List<(int Width, int Height)>();
        foreach (var resolution in doc.Root!.Element("resolutions")?.Elements("resolution") ?? [])
        {
            if (int.TryParse(resolution.Element("width")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(resolution.Element("height")?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h))
                modes.Add((w, h));
        }
        return modes;
    }

    private static XElement Resolution(int width, int height) => new("resolution",
        new XElement("width", width.ToString(CultureInfo.InvariantCulture)),
        new XElement("height", height.ToString(CultureInfo.InvariantCulture)),
        new XElement("refresh_rate", RefreshRate.ToString(CultureInfo.InvariantCulture)));

    private static XDocument Load(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            throw new VddSettingsException($"O arquivo de configuração do driver de monitor virtual está corrompido: {path} ({e.Message})", e);
        }
        if (doc.Root?.Name.LocalName != "vdd_settings")
            throw new VddSettingsException($"O arquivo {path} não é uma configuração do Virtual Display Driver (falta <vdd_settings>).");
        return doc;
    }

    private static void Save(XDocument doc, string path)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
            doc.Save(writer);
        // StringWriter declara utf-16; o arquivo é gravado em UTF-8, então a declaração tem de dizer utf-8.
        var text = buffer.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
        AtomicFile.WriteAllText(path, text);
    }
}
