namespace ScreenShare.Tests.Display;

/// <summary>Teste que mexe nas telas de verdade: só roda com SCREENSHARE_VDD_TESTS=1 e o driver instalado.</summary>
public sealed class VddFactAttribute : FactAttribute
{
    public VddFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCREENSHARE_VDD_TESTS") != "1")
            Skip = "Precisa do driver de monitor virtual instalado: defina SCREENSHARE_VDD_TESTS=1 para rodar.";
    }
}
