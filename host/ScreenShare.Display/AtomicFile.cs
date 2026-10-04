using System.Text;

namespace ScreenShare.Display;

/// <summary>
/// Grava um arquivo inteiro de uma vez: escreve num temporário na mesma pasta e troca pelo definitivo. Quem lê
/// (o driver, ou o próprio host depois de uma queda) nunca vê o arquivo pela metade.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
