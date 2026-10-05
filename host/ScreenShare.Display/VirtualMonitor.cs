namespace ScreenShare.Display;

/// <summary>O monitor virtual como a captura (Parte 3) e o toque (Parte 5) o veem: saída, retângulo na área de trabalho e escala.</summary>
public sealed record VirtualMonitor(string DeviceName, int X, int Y, int Width, int Height, int ScalePercent);

/// <summary>O pedido do celular já dentro dos limites; largura e altura pares (o encoder da Parte 3 exige).</summary>
public readonly record struct MonitorRequest(int Width, int Height, int DensityDpi)
{
    public const int MinWidth = 640, MaxWidth = 7680, MinHeight = 360, MaxHeight = 4320, MinDpi = 72, MaxDpi = 1000;

    public static MonitorRequest Normalize(int width, int height, int densityDpi) => new(
        Math.Clamp(width, MinWidth, MaxWidth) & ~1,
        Math.Clamp(height, MinHeight, MaxHeight) & ~1,
        Math.Clamp(densityDpi, MinDpi, MaxDpi));
}

/// <summary>
/// O monitor que a captura segue. Current muda quando o driver reinicia (o nome \\.\DISPLAYn muda) ou quando a
/// resolução exata é aplicada; Changed avisa depois da mudança. Refresh relê a saída agora (ex.: a captura não a achou).
/// </summary>
public interface IMonitorSource
{
    VirtualMonitor? Current { get; }

    event Action? Changed;

    VirtualMonitor? Refresh();
}

/// <summary>
/// O monitor que uma sessão está usando. Dispose libera (uma vez só); sem monitor, Width/Height são os do pedido.
/// O gerenciador atualiza Current e dispara Changed fora da trava dele.
/// </summary>
public sealed class VirtualMonitorLease(MonitorRequest request, VirtualMonitor? monitor, Action? release,
    Func<VirtualMonitor?>? refresh = null) : IMonitorSource, IDisposable
{
    private Action? _release = release;
    private VirtualMonitor? _current = monitor;

    public static VirtualMonitorLease Without(MonitorRequest request) => new(request, null, null);

    public MonitorRequest Request { get; } = request;

    /// <summary>O monitor no momento do Acquire.</summary>
    public VirtualMonitor? Monitor { get; } = monitor;

    /// <summary>O monitor agora: muda depois de um reinício do driver ou da resolução exata aplicada.</summary>
    public VirtualMonitor? Current => Volatile.Read(ref _current);

    public int Width => Current?.Width ?? Request.Width;
    public int Height => Current?.Height ?? Request.Height;

    public event Action? Changed;

    public VirtualMonitor? Refresh() => refresh is null ? Current : refresh();

    /// <summary>Troca o monitor atual; true se mudou (quem chama dispara Changed depois, fora da trava).</summary>
    internal bool Update(VirtualMonitor monitor)
    {
        if (Equals(Current, monitor)) return false;
        Volatile.Write(ref _current, monitor);
        return true;
    }

    internal void RaiseChanged() => Changed?.Invoke();

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public interface IVirtualMonitorManager
{
    /// <summary>Monitor para uma sessão que acabou de mandar HELLO. Nunca lança: sem monitor, o lease vem vazio.</summary>
    VirtualMonitorLease Acquire(int width, int height, int densityDpi);
}

/// <summary>Sem monitor virtual (--sem-monitor ou driver ausente): o CONFIG leva a resolução pedida.</summary>
public sealed class NullVirtualMonitorManager : IVirtualMonitorManager
{
    public static NullVirtualMonitorManager Instance { get; } = new();

    public VirtualMonitorLease Acquire(int width, int height, int densityDpi) =>
        VirtualMonitorLease.Without(MonitorRequest.Normalize(width, height, densityDpi));
}

/// <summary>Reinicia o driver (exige administrador: UAC) para ele reler a lista de resoluções. true = reiniciou.</summary>
public interface IDriverRestarter
{
    Task<bool> RestartAsync();
}
