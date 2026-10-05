using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Tests.Protocol;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

internal sealed class FakeImage(int width, int height) : IVideoImage
{
    public int Width { get; } = width;
    public int Height { get; } = height;
}

/// <summary>Captura falsa: a primeira imagem já está pronta; depois, a tela muda a cada Period (null = nunca).</summary>
internal sealed class FakeCapture(string deviceName, int width, int height, Func<TimeSpan> now) : IScreenCapture
{
    private TimeSpan? _nextChange = TimeSpan.Zero;

    public string DeviceName { get; } = deviceName;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IVideoImage Last { get; } = new FakeImage(width, height);
    public TimeSpan? Period { get; init; }
    public Exception? ThrowOnAcquire { get; set; }
    public int Acquires { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>A tela muda agora: a próxima TryAcquire devolve imagem nova.</summary>
    public void Change() => _nextChange = now();

    public AcquireResult TryAcquire(TimeSpan timeout, out ulong presentUs)
    {
        Acquires++;
        presentUs = 0;
        if (ThrowOnAcquire is { } error) throw error;
        var t = now();
        if (_nextChange is not { } next || t < next) return AcquireResult.Timeout;
        if (Period is { } period)
        {
            do next += period;
            while (next <= t);
            _nextChange = next;
        }
        else
        {
            _nextChange = null;
        }
        return AcquireResult.NewImage;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Encoder falso: responde na hora, na mesma thread, com um IDR real (vetor) ou um P-frame pequeno.</summary>
internal sealed class FakeEncoder(EncoderSettings settings) : IVideoEncoder
{
    private static readonly byte[] H264Idr = Vectors.Load("annexb/h264-idr.hex");
    private static readonly byte[] H265Idr = Vectors.Load("annexb/h265-idr.hex");

    public EncoderSettings Settings { get; } = settings;
    public VideoCodec Codec => Settings.Codec;
    public int Width => Settings.Width;
    public int Height => Settings.Height;
    public bool CanAccept { get; set; } = true;

    /// <summary>Simula um encoder cuja primeira saída não é IDR.</summary>
    public bool FirstOutputIsP { get; set; }

    /// <summary>Quantos pedidos de IDR o encoder ainda vai ignorar (sai P-frame no lugar).</summary>
    public int IgnoreForce { get; set; }

    /// <summary>Simula um encoder que abre mas recusa todo quadro.</summary>
    public bool ThrowOnSubmit { get; set; }

    public List<(ulong Timestamp, bool Forced)> Submitted { get; } = [];
    public bool Disposed { get; private set; }

    public event Action<EncodedFrame>? Output;
    public event Action<Exception>? Failed;

    public static byte[] Idr(VideoCodec codec) => codec == VideoCodec.H265 ? H265Idr : H264Idr;

    public void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe)
    {
        Submitted.Add((timestampUs, forceKeyframe));
        if (ThrowOnSubmit) throw new InvalidOperationException("quadro recusado (falso)");
        var key = forceKeyframe && !(FirstOutputIsP && Submitted.Count == 1);
        if (key && IgnoreForce > 0)
        {
            IgnoreForce--;
            key = false;
        }
        byte[] p = Codec == VideoCodec.H265 ? [0, 0, 0, 1, 0x02, 0x01, 0xD0] : [0, 0, 0, 1, 0x41, 0x9A];
        Output?.Invoke(new EncodedFrame(timestampUs, key, key ? Idr(Codec) : p));
    }

    public void Fail(Exception error) => Failed?.Invoke(error);

    public void Emit(EncodedFrame frame) => Output?.Invoke(frame);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeBackend : IVideoBackend
{
    public VideoCodec HardwareCodecs { get; set; } = VideoCodec.H264 | VideoCodec.H265;
    public Func<string, FakeCapture> CaptureFactory { get; set; } = name => throw new InvalidOperationException("sem captura");
    public Queue<Exception> OpenFailures { get; } = new();
    public VideoCodec FailingCodecs { get; set; }
    public Action<FakeEncoder>? EncoderSetup { get; set; }
    public List<string> Opened { get; } = [];
    public List<FakeCapture> Captures { get; } = [];
    public List<FakeEncoder> Encoders { get; } = [];
    public bool Disposed { get; private set; }

    public IScreenCapture OpenCapture(string deviceName)
    {
        Opened.Add(deviceName);
        if (OpenFailures.TryDequeue(out var error)) throw error;
        var capture = CaptureFactory(deviceName);
        Captures.Add(capture);
        return capture;
    }

    public IVideoEncoder CreateEncoder(EncoderSettings settings)
    {
        if ((FailingCodecs & settings.Codec) != 0) throw new EncoderUnavailableException("falso");
        var encoder = new FakeEncoder(settings);
        EncoderSetup?.Invoke(encoder);
        Encoders.Add(encoder);
        return encoder;
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeOutput : IVideoOutput
{
    private readonly List<Message> _sent = [];

    public int PendingFrames { get; set; }

    public List<Message> Sent
    {
        get
        {
            lock (_sent) return [.. _sent];
        }
    }

    public List<ConfigMessage> Configs => [.. Sent.OfType<ConfigMessage>()];
    public List<FrameMessage> Frames => [.. Sent.OfType<FrameMessage>()];

    public void OnConfig(ConfigMessage config)
    {
        lock (_sent) _sent.Add(config);
    }

    public void OnFrame(FrameMessage frame)
    {
        lock (_sent) _sent.Add(frame);
    }
}

internal sealed class FakeMonitor(VirtualMonitor? current) : IMonitorSource
{
    public VirtualMonitor? Current { get; private set; } = current;
    public int Refreshes { get; private set; }

    public event Action? Changed;

    public VirtualMonitor? Refresh()
    {
        Refreshes++;
        return Current;
    }

    public void Change(VirtualMonitor monitor)
    {
        Current = monitor;
        Changed?.Invoke();
    }
}
