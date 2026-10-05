using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

/// <summary>Vídeo falso para o HostServer: o teste manda CONFIG e quadros pela saída que a sessão entregou.</summary>
internal sealed class FakeVideoSource : IVideoSource
{
    private readonly List<FakeVideoStream> _started = [];

    /// <summary>false = sem vídeo (StartAsync devolve null), como num PC sem encoder.</summary>
    public bool Enabled { get; set; }

    /// <summary>Chamado quando um stream é encerrado (para conferir a ordem do encerramento).</summary>
    public Action<string>? OnEvent { get; set; }

    public IReadOnlyList<FakeVideoStream> Started
    {
        get
        {
            lock (_started) return [.. _started];
        }
    }

    public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        if (!Enabled) return Task.FromResult<IVideoStream?>(null);
        var stream = new FakeVideoStream(request, output, OnEvent);
        lock (_started) _started.Add(stream);
        return Task.FromResult<IVideoStream?>(stream);
    }
}

internal sealed class FakeVideoStream(VideoRequest request, IVideoOutput output, Action<string>? onEvent) : IVideoStream
{
    private int _keyframeRequests;
    private int _disposed;

    public VideoRequest Request { get; } = request;
    public IVideoOutput Output { get; } = output;
    public int KeyframeRequests => Volatile.Read(ref _keyframeRequests);
    public bool Disposed => Volatile.Read(ref _disposed) == 1;
    public VideoStats Stats => VideoStats.Empty;

    public void RequestKeyframe() => Interlocked.Increment(ref _keyframeRequests);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) onEvent?.Invoke("vídeo encerrado");
        return ValueTask.CompletedTask;
    }
}
