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

    /// <summary>Se definido, StartAsync só termina quando o teste completar (vídeo que demora a abrir).</summary>
    public TaskCompletionSource? StartGate { get; set; }

    public Exception? ThrowOnStart { get; set; }

    public bool ThrowOnDispose { get; set; }

    public IReadOnlyList<FakeVideoStream> Started
    {
        get
        {
            lock (_started) return [.. _started];
        }
    }

    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        if (ThrowOnStart is { } error) throw error;
        if (!Enabled) return null;
        var stream = new FakeVideoStream(request, output, OnEvent, ThrowOnDispose);
        lock (_started) _started.Add(stream);
        if (StartGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        return stream;
    }
}

internal sealed class FakeVideoStream(VideoRequest request, IVideoOutput output, Action<string>? onEvent, bool throwOnDispose) : IVideoStream
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
        if (throwOnDispose) throw new InvalidOperationException("falha falsa ao encerrar");
        return ValueTask.CompletedTask;
    }
}
