using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;

namespace ScreenShare.Video.Pipeline;

/// <summary>
/// O IVideoSource de verdade: cada sessão ganha um VideoPipeline numa thread própria (a thread de captura, com
/// prioridade acima do normal). O backend vem de createBackend, então os testes usam falsos. prepareThread roda no
/// começo da thread (DPI por monitor, timer de 1 ms) e o que devolve é descartado no fim dela.
/// </summary>
public sealed class PipelineVideoSource : IVideoSource
{
    private readonly Func<IVideoBackend> _createBackend;
    private readonly VideoCodec _hostCodecs;
    private readonly VideoOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly Func<IDisposable>? _prepareThread;
    private readonly CodecHealth _health = new();

    public PipelineVideoSource(Func<IVideoBackend> createBackend, VideoCodec hostCodecs, VideoOptions options,
        TimeProvider time, Action<string> log, Func<IDisposable>? prepareThread = null)
    {
        _createBackend = createBackend;
        _hostCodecs = hostCodecs;
        _options = options;
        _time = time;
        _log = log;
        _prepareThread = prepareThread;
    }

    public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
    {
        if (CodecChooser.Choose(request.PhoneCodecs, _hostCodecs & ~_health.Failed, _options.Codec) == VideoCodec.None)
        {
            _log("Nenhum encoder de hardware para os codecs que o celular decodifica: sessão sem vídeo.");
            return Task.FromResult<IVideoStream?>(null);
        }
        return Task.FromResult<IVideoStream?>(new PipelineStream(this, request, output));
    }

    private sealed class PipelineStream : IVideoStream
    {
        private readonly PipelineVideoSource _owner;
        private readonly VideoPipeline _pipeline;
        private readonly AutoResetEvent _wake = new(false);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _stopping;

        public PipelineStream(PipelineVideoSource owner, VideoRequest request, IVideoOutput output)
        {
            _owner = owner;
            _pipeline = new VideoPipeline(owner._createBackend, request.Monitor, request.PhoneCodecs, owner._options,
                owner._options.BitrateKbpsFor(request.Link), owner._health, output, owner._time, () => PcClock.NowUs,
                owner._log, () => _wake.Set());
            new Thread(Run) { IsBackground = true, Name = "ScreenShare captura", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public VideoStats Stats => _pipeline.Stats;

        public void RequestKeyframe() => _pipeline.RequestKeyframe();

        public async ValueTask DisposeAsync()
        {
            _stopping = true;
            _wake.Set();
            await _stopped.Task;
        }

        private void Run()
        {
            IDisposable? prepared = null;
            try
            {
                prepared = _owner._prepareThread?.Invoke();
                while (!_stopping)
                {
                    var wait = _pipeline.Step();
                    if (wait > TimeSpan.Zero) _wake.WaitOne(wait);
                }
            }
            catch (Exception e)
            {
                _owner._log($"O vídeo parou por um erro inesperado: {e.Message}");
            }
            finally
            {
                _pipeline.Dispose();
                prepared?.Dispose();
                _stopped.TrySetResult();
            }
        }
    }
}
