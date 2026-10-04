namespace ScreenShare.Display;

/// <summary>
/// Escala do Windows para o monitor virtual a partir do DPI do celular: DPI ÷ 160 × 100 (160 é a densidade de
/// referência do Android), arredondada ao degrau de 25% mais próximo e limitada a 100–200%.
/// </summary>
public static class ScaleCalculator
{
    public const int MinPercent = 100;
    public const int MaxPercent = 200;

    public static int FromDpi(int dpi)
    {
        var raw = dpi * 100.0 / 160;
        var rounded = (int)(Math.Round(raw / 25, MidpointRounding.AwayFromZero) * 25);
        return Math.Clamp(rounded, MinPercent, MaxPercent);
    }
}
