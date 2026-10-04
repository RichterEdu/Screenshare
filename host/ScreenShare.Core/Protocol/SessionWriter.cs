using System.Collections.Concurrent;

namespace ScreenShare.Core.Protocol;

/// <summary>
/// O único que escreve no stream de uma sessão: o SslStream não aceita duas escritas ao mesmo tempo, e o vídeo (thread
/// do encoder), os PONGs (laço de leitura) e os PINGs (timer) chegam de lugares diferentes. Mensagens de controle saem
/// antes do vídeo e nunca são descartadas; o vídeo passa pela VideoSendQueue. Um erro de escrita encerra RunAsync com
/// a exceção, o que encerra a sessão.
/// </summary>
public sealed class SessionWriter
{
    private readonly Stream _stream;
    private readonly Func<ulong> _clockUs;
    private readonly VideoSendQueue _video;
    private readonly ConcurrentQueue<Message?> _control = new(); // null = PING carimbado na hora de escrever
    private readonly SemaphoreSlim _signal = new(0);
    private readonly byte[] _frameHeader = new byte[MessageCodec.FrameHeaderSize];
    private volatile bool _configSent;

    /// <param name="clockUs">Relógio do PC em µs (PcClock.NowUs), lido quando o PING é escrito.</param>
    public SessionWriter(Stream stream, Func<ulong> clockUs, int maxPendingFrames = 2)
    {
        _stream = stream;
        _clockUs = clockUs;
        _video = new VideoSendQueue(maxPendingFrames);
    }

    /// <summary>A fila descartou quadros por excesso: quem produz o vídeo precisa mandar um keyframe.</summary>
    public event Action? KeyframeNeeded;

    /// <summary>Já foi posto na fila algum CONFIG (de vídeo ou de fallback).</summary>
    public bool ConfigSent => _configSent;

    public int PendingFrames => _video.PendingFrames;

    /// <summary>Mensagem de controle (PONG, CONFIG de fallback...): sai antes do vídeo e nunca é descartada.</summary>
    public void Send(Message control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control is ConfigMessage) _configSent = true;
        _control.Enqueue(control);
        _signal.Release();
    }

    /// <summary>PING do PC: o valor (relógio do PC) é lido na hora de escrever, para a fila não atrasar a medida.</summary>
    public void SendPing()
    {
        _control.Enqueue(null);
        _signal.Release();
    }

    /// <summary>CONFIG de um stream de vídeo novo: descarta o que ainda restava do stream anterior.</summary>
    public void SendVideoConfig(ConfigMessage config)
    {
        _configSent = true;
        _video.EnqueueConfig(config);
        _signal.Release();
    }

    public void SendVideoFrame(FrameMessage frame)
    {
        switch (_video.EnqueueFrame(frame))
        {
            case FrameEnqueue.Queued:
                _signal.Release();
                break;
            case FrameEnqueue.DroppedNeedKeyframe:
                KeyframeNeeded?.Invoke();
                break;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);
            while (TryNext(out var message))
                await WriteAsync(message, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
    }

    private bool TryNext(out Message message)
    {
        if (_control.TryDequeue(out var control))
        {
            message = control ?? new PingMessage(_clockUs());
            return true;
        }
        if (_video.TryDequeue(out var video))
        {
            message = video;
            return true;
        }
        message = null!;
        return false;
    }

    private async Task WriteAsync(Message message, CancellationToken cancellationToken)
    {
        if (message is FrameMessage frame)
        {
            MessageCodec.WriteFrameHeader(_frameHeader, frame.TimestampUs, frame.IsKeyframe, frame.Data.Length);
            await _stream.WriteAsync(_frameHeader, cancellationToken);
            await _stream.WriteAsync(frame.Data, cancellationToken);
        }
        else
        {
            await _stream.WriteAsync(MessageCodec.Encode(message), cancellationToken);
        }
    }
}
