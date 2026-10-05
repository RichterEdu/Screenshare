using ScreenShare.Core.Protocol;
using ScreenShare.Display;

namespace ScreenShare.Video;

/// <summary>Captura outro monitor no lugar do da sessão (--capturar principal: depurar sem o monitor virtual).</summary>
public sealed class MonitorOverrideVideoSource(IVideoSource inner, Func<IMonitorSource> monitor) : IVideoSource
{
    public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken) =>
        inner.StartAsync(request with { Monitor = monitor() }, output, cancellationToken);
}

/// <summary>
/// Grava o vídeo de cada sessão num arquivo Annex-B (--gravar), para conferir com o ffplay. Cada sessão recomeça o
/// arquivo. Os keyframes trazem os parâmetros, então o arquivo toca mesmo com CONFIG no meio.
/// </summary>
public sealed class RecordingVideoSource(IVideoSource inner, string path, Action<string> log) : IVideoSource
{
    public async Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        var recorder = new RecordingOutput(output, path, log);
        var stream = await inner.StartAsync(request, recorder, cancellationToken);
        if (stream is null)
        {
            recorder.Close();
            return null;
        }
        log($"Gravando o vídeo em {Path.GetFullPath(path)}.");
        return new RecordingStream(stream, recorder);
    }

    private sealed class RecordingOutput(IVideoOutput inner, string path, Action<string> log) : IVideoOutput
    {
        private readonly Lock _gate = new();
        private FileStream? _file = File.Create(path);

        public int PendingFrames => inner.PendingFrames;

        public void OnConfig(ConfigMessage config) => inner.OnConfig(config);

        public void OnFrame(FrameMessage frame)
        {
            lock (_gate)
            {
                try
                {
                    _file?.Write(frame.Data);
                }
                catch (IOException e)
                {
                    log($"Gravação interrompida: {e.Message}");
                    _file?.Dispose();
                    _file = null;
                }
            }
            inner.OnFrame(frame);
        }

        public void Close()
        {
            lock (_gate)
            {
                _file?.Dispose();
                _file = null;
            }
        }
    }

    private sealed class RecordingStream(IVideoStream inner, RecordingOutput recorder) : IVideoStream
    {
        public VideoStats Stats => inner.Stats;

        public void RequestKeyframe() => inner.RequestKeyframe();

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            recorder.Close();
        }
    }
}
