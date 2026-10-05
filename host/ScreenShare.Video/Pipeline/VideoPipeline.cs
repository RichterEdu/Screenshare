using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Display;

namespace ScreenShare.Video.Pipeline;

/// <summary>
/// Captura → encoder → saída, um passo por vez (Step), sempre na mesma thread (a de captura). O hardware fica atrás de
/// IVideoBackend; os testes chamam Step com um relógio falso.
/// - Abre a captura com espera crescente (100 ms até 2 s); se a saída sumiu, relê o monitor.
/// - Tamanho novo da captura = encoder novo = stream novo: IDR, depois CONFIG (com os parâmetros tirados do IDR), depois quadros.
/// - Só captura com o ritmo de fps permitindo, o encoder pedindo entrada e nenhum quadro esperando a rede: assim a
///   captura junta as mudanças enquanto a rede está ocupada, e o quadro seguinte já é a imagem mais nova.
/// - Tela parada: refinamentos em +100, +300 e +700 ms e, se pedido, IDR da última imagem.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(2);

    /// <summary>Espera máxima por imagem nova numa chamada: limita quanto um pedido de keyframe espera com a tela parada.</summary>
    public static readonly TimeSpan AcquireTimeout = TimeSpan.FromMilliseconds(20);

    /// <summary>Quanto o Step pede para esperar quando a rede ou o encoder ainda não liberaram.</summary>
    public static readonly TimeSpan BusyWait = TimeSpan.FromMilliseconds(1);

    /// <summary>Recodificações da última imagem depois que a tela para (o controle de bitrate deixa o primeiro quadro borrado).</summary>
    public static readonly TimeSpan[] Refinements =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(700)];

    private const int MaxTrackedSubmits = 1000;

    private readonly Lock _gate = new();
    private readonly Func<IVideoBackend> _createBackend;
    private readonly IMonitorSource _monitor;
    private readonly VideoCodec _phoneCodecs;
    private readonly VideoOptions _options;
    private readonly int _bitrateKbps;
    private readonly CodecHealth _health;
    private readonly IVideoOutput _output;
    private readonly TimeProvider _time;
    private readonly Func<ulong> _clockUs;
    private readonly Action<string> _log;
    private readonly Action? _wake;
    private readonly long _start;
    private readonly FramePacer _pacer;
    private readonly KeyframeScheduler _keyframes = new();

    // Só a thread de captura (Step) mexe nestes.
    private IVideoBackend? _backend;
    private IScreenCapture? _capture;
    private TimeSpan _retryAt;
    private TimeSpan _retryDelay = FirstRetry;
    private bool _haveImage;
    private TimeSpan? _lastChange;
    private int _refinementsDone;
    private string? _lastProblem;

    // Compartilhados com a thread do encoder e com a sessão: sob _gate.
    private readonly Dictionary<ulong, long> _submittedAt = [];
    private readonly RecentDurations _encodeTimes = new(300);
    private IVideoEncoder? _encoder;
    private int _generation;
    private bool _needConfig;
    private bool _encoderFailed;
    private bool _monitorChanged;
    private ulong _lastTimestamp;
    private long _frames;
    private long _bytes;
    private long _keyframesSent;
    private bool _disposed;

    /// <param name="clockUs">Relógio do PC em µs (PcClock.NowUs): timestamp dos quadros sem horário de apresentação.</param>
    /// <param name="wake">Acorda a thread de captura (pedido de keyframe, monitor mudou, encoder falhou).</param>
    public VideoPipeline(Func<IVideoBackend> createBackend, IMonitorSource monitor, VideoCodec phoneCodecs,
        VideoOptions options, int bitrateKbps, CodecHealth health, IVideoOutput output, TimeProvider time,
        Func<ulong> clockUs, Action<string> log, Action? wake = null)
    {
        _createBackend = createBackend;
        _monitor = monitor;
        _phoneCodecs = phoneCodecs;
        _options = options;
        _bitrateKbps = bitrateKbps;
        _health = health;
        _output = output;
        _time = time;
        _clockUs = clockUs;
        _log = log;
        _wake = wake;
        _start = time.GetTimestamp();
        _pacer = new FramePacer(options.Fps);
        monitor.Changed += OnMonitorChanged;
    }

    private TimeSpan Now => _time.GetElapsedTime(_start);

    public VideoStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new VideoStats(_encoder?.Width ?? 0, _encoder?.Height ?? 0, _encoder?.Codec ?? VideoCodec.None,
                    _frames, _bytes, _keyframesSent, _encodeTimes.P95().TotalMilliseconds);
            }
        }
    }

    /// <summary>Pedido de keyframe (celular, fila de envio cheia). Qualquer thread; nunca lança.</summary>
    public void RequestKeyframe()
    {
        _keyframes.Request();
        _wake?.Invoke();
    }

    /// <summary>Um passo. Devolve quanto esperar antes do próximo (zero = chamar de novo já).</summary>
    public TimeSpan Step()
    {
        lock (_gate)
        {
            if (_disposed) return MaxRetry;
        }
        var now = Now;
        if (now < _retryAt) return _retryAt - now;

        try
        {
            if (!EnsureBackend() || !EnsureCapture() || !EnsureEncoder()) return Backoff(now);
            return Capture(now);
        }
        catch (CaptureLostException e)
        {
            Problem($"Captura perdida ({e.Message}); reabrindo.");
            CloseCapture();
            return Backoff(now);
        }
        catch (DeviceLostException e)
        {
            Problem($"A placa de vídeo foi reiniciada ({e.Message}); recriando a captura e o encoder.");
            CloseAll();
            return Backoff(now);
        }
    }

    /// <summary>Chamado na thread de captura, depois do último Step.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _monitor.Changed -= OnMonitorChanged;
        CloseAll();
    }

    private TimeSpan Backoff(TimeSpan now)
    {
        var wait = _retryDelay;
        _retryAt = now + wait;
        _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetry.Ticks));
        return wait;
    }

    private bool EnsureBackend()
    {
        if (_backend is not null) return true;
        try
        {
            _backend = _createBackend();
            return true;
        }
        catch (Exception e)
        {
            Problem($"Vídeo indisponível: {e.Message}");
            return false;
        }
    }

    private bool EnsureCapture()
    {
        bool changed;
        lock (_gate)
        {
            changed = _monitorChanged;
            _monitorChanged = false;
        }
        if (changed) CloseCapture(); // nome ou tamanho novo: a captura antiga não vale mais
        if (_capture is not null) return true;

        var monitor = _monitor.Current ?? _monitor.Refresh();
        if (monitor is null)
        {
            Problem("Monitor para capturar não encontrado; tentando de novo.");
            return false;
        }
        try
        {
            _capture = _backend!.OpenCapture(monitor.DeviceName);
            _retryDelay = FirstRetry;
            return true;
        }
        catch (CaptureLostException e)
        {
            // UAC ou Win+L (área de trabalho segura), ou driver reiniciando (nome novo): relê o monitor e tenta de novo.
            Problem($"Captura de {monitor.DeviceName} indisponível ({e.Message}); tentando de novo.");
            _monitor.Refresh();
            return false;
        }
    }

    private bool EnsureEncoder()
    {
        bool failed;
        lock (_gate)
        {
            failed = _encoderFailed;
            _encoderFailed = false;
        }
        if (failed) CloseEncoder();

        var capture = _capture!;
        if (_encoder is { } current && current.Width == capture.Width && current.Height == capture.Height) return true;
        CloseEncoder();

        while (true)
        {
            var codec = CodecChooser.Choose(_phoneCodecs, _backend!.HardwareCodecs & ~_health.Failed, _options.Codec);
            if (codec == VideoCodec.None)
            {
                Problem("Nenhum encoder disponível para os codecs que o celular decodifica; sem vídeo.");
                return false;
            }
            try
            {
                StartStream(_backend.CreateEncoder(new EncoderSettings(codec, capture.Width, capture.Height, _options.Fps,
                    _bitrateKbps, _bitrateKbps * 2)));
                return true;
            }
            catch (EncoderUnavailableException e)
            {
                _log($"Encoder {CodecChooser.Name(codec)} indisponível ({e.Message}); ele fica de fora até o host reiniciar.");
                _health.MarkFailed(codec);
            }
        }
    }

    private void StartStream(IVideoEncoder encoder)
    {
        lock (_gate)
        {
            var generation = ++_generation;
            encoder.Output += frame => OnOutput(generation, frame);
            encoder.Failed += error => OnFailed(generation, error);
            _encoder = encoder;
            _needConfig = true;
            _submittedAt.Clear();
        }
        _keyframes.RequestNow();
        _log($"Vídeo: {CodecChooser.Name(encoder.Codec)} {encoder.Width}×{encoder.Height}, até {_options.Fps} fps, {_bitrateKbps / 1000} Mbps.");
    }

    private TimeSpan Capture(TimeSpan now)
    {
        var capture = _capture!;
        var encoder = _encoder!;
        if (_output.PendingFrames > 0 || !encoder.CanAccept) return BusyWait;
        var pace = _pacer.Delay(now);
        if (pace > TimeSpan.Zero) return pace;

        var keyframeDue = _haveImage && _keyframes.IsDue(now);
        var refinementDue = _haveImage && RefinementDue(now);
        var result = capture.TryAcquire(keyframeDue || refinementDue ? TimeSpan.Zero : AcquireTimeout, out var presentUs);
        now = Now; // a espera do TryAcquire conta
        if (result == AcquireResult.NewImage)
        {
            _haveImage = true;
            _lastChange = now;
            _refinementsDone = 0;
            Submit(encoder, capture.Last, presentUs, now);
        }
        else if (keyframeDue || refinementDue)
        {
            if (refinementDue) _refinementsDone++;
            Submit(encoder, capture.Last, 0, now);
        }
        return TimeSpan.Zero;
    }

    private bool RefinementDue(TimeSpan now) =>
        _lastChange is { } changed && _refinementsDone < Refinements.Length && now - changed >= Refinements[_refinementsDone];

    private void Submit(IVideoEncoder encoder, IVideoImage image, ulong presentUs, TimeSpan now)
    {
        var timestamp = presentUs != 0 ? presentUs : _clockUs();
        lock (_gate)
        {
            if (timestamp <= _lastTimestamp) timestamp = _lastTimestamp + 1; // estritamente crescente dentro do stream
            _lastTimestamp = timestamp;
            if (_submittedAt.Count >= MaxTrackedSubmits) _submittedAt.Clear();
            _submittedAt[timestamp] = _time.GetTimestamp();
        }
        var force = _keyframes.TakeDue(now);
        _pacer.MarkSent(now);
        _lastProblem = null;
        try
        {
            encoder.Submit(image, timestamp, force);
        }
        catch (Exception e) when (e is not DeviceLostException)
        {
            _log($"O encoder recusou o quadro ({e.Message}); recriando.");
            CloseEncoder();
        }
    }

    /// <summary>Saída do encoder, na thread dele. A emissão fica sob a trava para um stream novo nunca se misturar ao antigo.</summary>
    private void OnOutput(int generation, EncodedFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return; // encoder antigo: o stream dele acabou
            if (_submittedAt.Remove(frame.TimestampUs, out var submitted)) _encodeTimes.Add(_time.GetElapsedTime(submitted));
            if (_needConfig)
            {
                if (!frame.IsKeyframe)
                {
                    _keyframes.RequestNow(); // o celular só começa a decodificar num IDR
                    return;
                }
                var encoder = _encoder!;
                _output.OnConfig(new ConfigMessage((ushort)encoder.Width, (ushort)encoder.Height, encoder.Codec,
                    (uint)_bitrateKbps, AnnexB.ExtractParameterSets(frame.Data, encoder.Codec) ?? []));
                _needConfig = false;
            }
            _frames++;
            _bytes += frame.Data.Length;
            if (frame.IsKeyframe) _keyframesSent++;
            _output.OnFrame(new FrameMessage(frame.TimestampUs, frame.IsKeyframe, frame.Data));
        }
    }

    private void OnFailed(int generation, Exception error)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            _encoderFailed = true;
        }
        _log($"O encoder falhou ({error.Message}); recriando.");
        _wake?.Invoke();
    }

    private void OnMonitorChanged()
    {
        lock (_gate) _monitorChanged = true;
        _wake?.Invoke();
    }

    private void CloseEncoder()
    {
        IVideoEncoder? encoder;
        lock (_gate)
        {
            _generation++; // saídas atrasadas do encoder antigo passam a ser ignoradas
            encoder = _encoder;
            _encoder = null;
        }
        encoder?.Dispose();
    }

    private void CloseCapture()
    {
        _capture?.Dispose();
        _capture = null;
        _haveImage = false;
    }

    private void CloseAll()
    {
        CloseEncoder();
        CloseCapture();
        _backend?.Dispose();
        _backend = null;
    }

    /// <summary>Registra um problema uma vez só, até um quadro sair de novo (retentativas não enchem o console).</summary>
    private void Problem(string message)
    {
        if (message == _lastProblem) return;
        _lastProblem = message;
        _log(message);
    }
}

/// <summary>As últimas N durações (do encode), para o p95 das estatísticas.</summary>
internal sealed class RecentDurations(int capacity)
{
    private readonly Queue<TimeSpan> _items = new();

    public void Add(TimeSpan duration)
    {
        _items.Enqueue(duration);
        if (_items.Count > capacity) _items.Dequeue();
    }

    public TimeSpan P95()
    {
        if (_items.Count == 0) return TimeSpan.Zero;
        var sorted = _items.Order().ToArray();
        return sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
    }
}
