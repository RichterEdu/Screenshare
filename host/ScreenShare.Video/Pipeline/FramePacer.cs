namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Ritmo de até fps quadros por segundo. Tem uma folga de 1/4 de intervalo para o jitter do vsync: um quadro de 60 Hz
/// que chega 1 ms antes não espera um intervalo inteiro, e a tela a 60 Hz continua saindo a 60 fps.
/// </summary>
public sealed class FramePacer
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _slack;
    private TimeSpan? _next;

    public FramePacer(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _interval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / fps);
        _slack = _interval / 4;
    }

    /// <summary>Quanto falta para o próximo quadro poder sair (zero = já pode).</summary>
    public TimeSpan Delay(TimeSpan now) =>
        _next is { } next && now < next - _slack ? next - _slack - now : TimeSpan.Zero;

    public void MarkSent(TimeSpan now)
    {
        // Dentro da folga, mantém a cadência; bem atrasado (tela parada), recomeça de agora.
        var basis = _next is { } next && (now - next).Duration() <= _slack ? next : now;
        _next = basis + _interval;
    }
}
