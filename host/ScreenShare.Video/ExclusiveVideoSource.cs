namespace ScreenShare.Video;

/// <summary>
/// O Windows permite uma duplicação por saída por processo: só uma sessão tem vídeo por vez. Uma sessão nova (por
/// exemplo, o cabo enquanto a do Wi-Fi ainda não caiu) assume o vídeo; o stream da anterior é encerrado antes de o
/// novo começar, e ela fica sem vídeo.
/// </summary>
public sealed class ExclusiveVideoSource(IVideoSource inner) : IVideoSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Handle? _current;

    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } previous)
            {
                _current = null;
                await previous.StopAsync();
            }
            var stream = await inner.StartAsync(request, output, cancellationToken);
            if (stream is null) return null;
            _current = new Handle(this, stream);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask ReleaseAsync(Handle handle)
    {
        await _gate.WaitAsync();
        try
        {
            if (ReferenceEquals(_current, handle)) _current = null;
        }
        finally
        {
            _gate.Release();
        }
        await handle.StopAsync();
    }

    /// <summary>O stream entregue à sessão: depois de parado (pela própria sessão ou por uma nova), não faz mais nada.</summary>
    private sealed class Handle(ExclusiveVideoSource owner, IVideoStream inner) : IVideoStream
    {
        private int _stopped;

        public VideoStats Stats => inner.Stats;

        public void RequestKeyframe()
        {
            if (Volatile.Read(ref _stopped) == 0) inner.RequestKeyframe();
        }

        public ValueTask DisposeAsync() => owner.ReleaseAsync(this);

        public async ValueTask StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0) await inner.DisposeAsync();
        }
    }
}
