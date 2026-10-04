using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class VideoSendQueueTests
{
    private static readonly ConfigMessage Config = new(2520, 1080, VideoCodec.H265, 50_000, [0, 0, 0, 1, 0x40]);

    private static FrameMessage Key(ulong ts) => new(ts, true, [0, 0, 0, 1, 0x26, 0x01]);

    private static FrameMessage P(ulong ts) => new(ts, false, [0, 0, 0, 1, 0x02, 0x01]);

    private static List<Message> Drain(VideoSendQueue queue)
    {
        var messages = new List<Message>();
        while (queue.TryDequeue(out var message)) messages.Add(message);
        return messages;
    }

    [Fact]
    public void Config_then_keyframe_then_frames_come_out_in_order()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);

        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(1)));
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(P(2)));

        Assert.Equal(new Message[] { Config, Key(1), P(2) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void Frames_before_the_first_keyframe_of_a_stream_are_dropped()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);

        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(1)));
        Assert.Equal(0, queue.PendingFrames);
    }

    [Fact]
    public void Third_pending_frame_is_dropped_and_asks_for_a_keyframe_then_waits_for_one()
    {
        var queue = new VideoSendQueue(maxPendingFrames: 2);
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));

        Assert.Equal(FrameEnqueue.DroppedNeedKeyframe, queue.EnqueueFrame(P(3)));
        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(4)));
        Assert.Equal(2, queue.PendingFrames);
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(5)));
    }

    [Fact]
    public void Keyframe_replaces_the_frames_still_waiting()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));

        queue.EnqueueFrame(Key(3));

        Assert.Equal(1, queue.PendingFrames);
        Assert.Equal(new Message[] { Config, Key(3) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void New_config_discards_the_old_stream_and_its_config()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        queue.EnqueueFrame(P(2));
        var newConfig = Config with { Width = 1920 };

        queue.EnqueueConfig(newConfig);

        Assert.Equal(FrameEnqueue.Dropped, queue.EnqueueFrame(P(3)));
        Assert.Equal(FrameEnqueue.Queued, queue.EnqueueFrame(Key(4)));
        Assert.Equal(new Message[] { newConfig, Key(4) }, Drain(queue), new MessageComparer());
    }

    [Fact]
    public void PendingFrames_counts_only_frames()
    {
        var queue = new VideoSendQueue();
        queue.EnqueueConfig(Config);
        queue.EnqueueFrame(Key(1));
        Assert.Equal(1, queue.PendingFrames);

        Assert.True(queue.TryDequeue(out var first));
        Assert.IsType<ConfigMessage>(first);
        Assert.Equal(1, queue.PendingFrames);

        Assert.True(queue.TryDequeue(out _));
        Assert.Equal(0, queue.PendingFrames);
        Assert.False(queue.TryDequeue(out _));
    }

    /// <summary>Records com arrays não comparam o conteúdo: compara pelos bytes codificados.</summary>
    private sealed class MessageComparer : IEqualityComparer<Message>
    {
        public bool Equals(Message? x, Message? y) =>
            x is not null && y is not null && MessageCodec.Encode(x).AsSpan().SequenceEqual(MessageCodec.Encode(y));

        public int GetHashCode(Message obj) => obj.GetType().GetHashCode();
    }
}
