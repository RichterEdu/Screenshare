namespace ScreenShare.Display;

/// <summary>
/// A saída do Virtual Display Driver como o Windows a vê agora. Desanexada (fora da área de trabalho) tem posição
/// e tamanho zerados. Modes: as resoluções que o driver oferece (as do XML, lidas quando o driver iniciou).
/// </summary>
public sealed record VddOutput(string DeviceName, bool Attached, int X, int Y, int Width, int Height,
    IReadOnlyList<(int Width, int Height)> Modes);

/// <summary>As operações de tela que o gerenciador usa. Tudo sem administrador.</summary>
public interface IDisplayTopology
{
    /// <summary>A saída do VDD, achada pelo ID de hardware (o nome \\.\DISPLAYn muda a cada reinício do driver); null sem driver.</summary>
    VddOutput? FindVddOutput();

    /// <summary>Põe a saída na área de trabalho, na posição e resolução dadas ("ligar").</summary>
    bool Attach(string deviceName, DisplayPosition position, int width, int height);

    /// <summary>Tira a saída da área de trabalho ("desligar"; o Windows lembra disso depois de reiniciar o PC).</summary>
    bool Detach(string deviceName);

    bool SetMode(string deviceName, int width, int height);

    /// <summary>Escala atual em %, ou null se a saída não está ativa.</summary>
    int? GetScale(string deviceName);

    /// <summary>Aplica a escala (limitada ao máximo que o Windows aceita para o monitor).</summary>
    bool SetScale(string deviceName, int percent);

    /// <summary>À direita da tela mais à direita, no topo: onde o monitor aparece da primeira vez.</summary>
    DisplayPosition DefaultPosition();
}
