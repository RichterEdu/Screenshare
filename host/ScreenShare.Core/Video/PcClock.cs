using System.Diagnostics;

namespace ScreenShare.Core.Video;

/// <summary>
/// O relógio do PC em microssegundos, a partir do QPC (o mesmo relógio do LastPresentTime do DXGI). É o valor do
/// FRAME.timestampUs e do PING que o PC envia; o celular usa os dois para medir a latência de ponta a ponta.
/// </summary>
public static class PcClock
{
    public static ulong NowUs => ToMicroseconds(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    /// <summary>Converte ticks do QPC em µs sem estourar 64 bits (ticks × 1 000 000 estoura depois de uns 10 dias).</summary>
    public static ulong ToMicroseconds(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        var seconds = ticks / frequency;
        var remainder = ticks % frequency;
        return (ulong)seconds * 1_000_000UL + (ulong)(remainder * 1_000_000 / frequency);
    }
}
