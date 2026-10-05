namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Junta os pedidos de keyframe (celular, descarte na fila de envio) com no mínimo 200 ms entre IDRs: um IDR do
/// monitor inteiro tem centenas de KB. Um pedido só é adiado, nunca perdido. Seguro entre threads.
/// </summary>
public sealed class KeyframeScheduler
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);

    private readonly Lock _gate = new();
    private bool _pending;
    private TimeSpan? _last;

    public void Request()
    {
        lock (_gate) _pending = true;
    }

    /// <summary>Pedido que não espera o intervalo mínimo: stream novo, cujo primeiro quadro tem de ser IDR.</summary>
    public void RequestNow()
    {
        lock (_gate)
        {
            _pending = true;
            _last = null;
        }
    }

    public bool IsDue(TimeSpan now)
    {
        lock (_gate) return Due(now);
    }

    /// <summary>true = o próximo quadro deve ser IDR (e o pedido é consumido).</summary>
    public bool TakeDue(TimeSpan now)
    {
        lock (_gate)
        {
            if (!Due(now)) return false;
            _pending = false;
            _last = now;
            return true;
        }
    }

    private bool Due(TimeSpan now) => _pending && (_last is not { } last || now - last >= MinInterval);
}
