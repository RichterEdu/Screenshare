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
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
                // Vai para o disco antes da troca: uma queda de energia logo depois não deixa o arquivo vazio.
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string temporary)
    {
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // o temporário é só lixo: não pode esconder a falha original da gravação
        }
    }
}
