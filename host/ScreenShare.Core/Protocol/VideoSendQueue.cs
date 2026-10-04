using System.Diagnostics.CodeAnalysis;

namespace ScreenShare.Core.Protocol;

/// <summary>O que aconteceu com um FRAME posto na fila.</summary>
public enum FrameEnqueue
{
    Queued,
    /// <summary>Descartado sem pedir nada: o stream já espera um keyframe.</summary>
    Dropped,
    /// <summary>Descartado porque a fila passou do limite: quem produz o vídeo precisa mandar um keyframe.</summary>
    DroppedNeedKeyframe,
}

/// <summary>
/// A fila de vídeo de uma sessão (CONFIG e FRAMEs), segura entre threads. Um P-frame não pode ser descartado sozinho
/// (o seguinte depende dele): ao passar do limite, a fila descarta os P-frames até o próximo keyframe e pede um.
/// Um keyframe torna obsoletos os quadros que ainda esperam; um CONFIG começa um stream novo.
/// </summary>
public sealed class VideoSendQueue(int maxPendingFrames = 2)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Message> _items = new();
    private int _pendingFrames;
    private bool _awaitingKeyframe;

    public int PendingFrames
    {
        get
        {
            lock (_gate) return _pendingFrames;
        }
    }

    /// <summary>Começa um stream novo: os quadros e o CONFIG do stream anterior que ainda não saíram são descartados.</summary>
    public void EnqueueConfig(ConfigMessage config)
    {
        lock (_gate)
        {
            _items.Clear();
            _pendingFrames = 0;
            _items.AddLast(config);
            _awaitingKeyframe = true;
        }
    }

    public FrameEnqueue EnqueueFrame(FrameMessage frame)
    {
        lock (_gate)
        {
            if (frame.IsKeyframe)
            {
                RemoveFrames();
                _awaitingKeyframe = false;
                AddFrame(frame);
                return FrameEnqueue.Queued;
            }
            if (_awaitingKeyframe) return FrameEnqueue.Dropped;
            if (_pendingFrames >= maxPendingFrames)
            {
                _awaitingKeyframe = true;
                return FrameEnqueue.DroppedNeedKeyframe;
            }
            AddFrame(frame);
            return FrameEnqueue.Queued;
        }
    }

    public bool TryDequeue([NotNullWhen(true)] out Message? message)
    {
        lock (_gate)
        {
            if (_items.First is not { } first)
            {
                message = null;
                return false;
            }
            _items.RemoveFirst();
            if (first.Value is FrameMessage) _pendingFrames--;
            message = first.Value;
            return true;
        }
    }

    private void AddFrame(FrameMessage frame)
    {
        _items.AddLast(frame);
        _pendingFrames++;
    }

    private void RemoveFrames()
    {
        for (var node = _items.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value is FrameMessage)
            {
                _items.Remove(node);
                _pendingFrames--;
            }
            node = next;
        }
    }
}
