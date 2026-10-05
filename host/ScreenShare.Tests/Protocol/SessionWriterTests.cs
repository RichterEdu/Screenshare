using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class SessionWriterTests
{
    private static readonly ConfigMessage Config = new(2520, 1080, VideoCodec.H265, 50_000, [0, 0, 0, 1, 0x40, 0x01, 0x0C]);
    private static readonly byte[] KeyData = [0, 0, 0, 1, 0x26, 0x01, 0xAF];

    private static FrameMessage Key(ulong ts) => new(ts, true, KeyData);

    private static FrameMessage P(ulong ts) => new(ts, false, [0, 0, 0, 1, 0x02, 0x01, 0x80]);

    [Fact]
    public async Task Frame_is_written_as_header_plus_data_exactly_like_the_vector()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(new FrameMessage(1_000_000, true, KeyData));

        await RunUntilAsync(writer, stream, messages => messages.Count == 2);

        Assert.Equal(MessageCodec.Encode(Config).Concat(Vectors.Load("frame.hex")).ToArray(), stream.Written);
    }

    [Fact]
    public async Task Control_messages_go_before_pending_video()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(Key(1));
        writer.Send(new PongMessage(7));

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 3);

        Assert.Equal(new PongMessage(7), messages[0]);
        Assert.IsType<ConfigMessage>(messages[1]);
        Assert.IsType<FrameMessage>(messages[2]);
    }

    [Fact]
    public async Task Ping_value_is_read_when_written_not_when_queued()
    {
        ulong clock = 5;
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => clock);
        writer.SendPing();
        clock = 9;

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 1);

        Assert.Equal(new PingMessage(9), messages[0]);
    }

    [Fact]
    public async Task Too_many_pending_frames_drop_until_keyframe_and_ask_for_one()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0, maxPendingFrames: 2);
        var keyframeRequests = 0;
        writer.KeyframeNeeded += () => keyframeRequests++;
        writer.SendVideoConfig(Config);
        writer.SendVideoFrame(Key(1));
        writer.SendVideoFrame(P(2));
        writer.SendVideoFrame(P(3)); // terceiro pendente: descartado, pede keyframe
        writer.SendVideoFrame(P(4)); // ainda esperando keyframe: descartado sem pedir de novo
        writer.SendVideoFrame(Key(5)); // substitui o que ainda esperava

        var messages = await RunUntilAsync(writer, stream, m => m.Count == 2);

        Assert.Equal(1, keyframeRequests);
        Assert.IsType<ConfigMessage>(messages[0]);
        var frame = Assert.IsType<FrameMessage>(messages[1]);
        Assert.Equal(5UL, frame.TimestampUs);
    }

    [Fact]
    public async Task Concurrent_producers_never_overlap_writes_and_keep_the_stream_decodable()
    {
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);
        writer.KeyframeNeeded += () => { };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = writer.RunAsync(cts.Token);

        var video = Task.Run(async () =>
        {
            writer.SendVideoConfig(Config);
            for (var i = 0; i < 1000; i++)
            {
                writer.SendVideoFrame(new FrameMessage((ulong)i, i % 50 == 0, KeyData));
                if (i % 7 == 0) await Task.Yield();
            }
        });
        var control = Task.Run(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                writer.Send(new PongMessage((ulong)i));
                if (i % 5 == 0) await Task.Yield();
            }
        });
        await Task.WhenAll(video, control);
        writer.Send(new PongMessage(ulong.MaxValue)); // marcador: quando ele sai, todos os PONGs saíram

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!(DecodeComplete(stream.Written).Contains(new PongMessage(ulong.MaxValue)) && writer.PendingFrames == 0))
        {
            Assert.False(run.IsFaulted, run.Exception?.ToString());
            Assert.True(DateTime.UtcNow < deadline, "o escritor não terminou de escrever");
            await Task.Delay(10);
        }
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var messages = DecodeComplete(stream.Written);
        var pongs = messages.OfType<PongMessage>().Select(p => p.TimestampUs).ToList();
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (ulong)i).Append(ulong.MaxValue), pongs);
        var frames = messages.OfType<FrameMessage>().ToList();
        Assert.True(frames[0].IsKeyframe);
        for (var i = 1; i < frames.Count; i++)
        {
            Assert.True(frames[i].TimestampUs > frames[i - 1].TimestampUs);
            if (frames[i].TimestampUs != frames[i - 1].TimestampUs + 1)
                Assert.True(frames[i].IsKeyframe, $"depois de um pulo (t={frames[i].TimestampUs}) o quadro tem de ser keyframe");
        }
    }

    [Fact]
    public async Task Write_error_ends_the_writer()
    {
        var writer = new SessionWriter(new FailingStream(), () => 0);
        writer.Send(new PongMessage(1));

        await Assert.ThrowsAsync<IOException>(() => writer.RunAsync(CancellationToken.None));
    }

    [Fact]
    public void ConfigSent_after_a_video_or_a_fallback_config()
    {
        var video = new SessionWriter(new GuardedStream(), () => 0);
        var fallback = new SessionWriter(new GuardedStream(), () => 0);
        Assert.False(video.ConfigSent);

        video.SendVideoConfig(Config);
        fallback.Send(Config);

        Assert.True(video.ConfigSent);
        Assert.True(fallback.ConfigSent);
    }

    [Fact]
    public async Task Fallback_config_goes_out_only_when_no_config_was_queued_before()
    {
        var fallback = Config with { CodecConfig = [] };
        var stream = new GuardedStream();
        var writer = new SessionWriter(stream, () => 0);

        Assert.True(writer.SendFallbackConfig(fallback));
        Assert.False(writer.SendFallbackConfig(fallback));
        writer.SendVideoConfig(Config); // o vídeo começou depois: o CONFIG dele substitui o de fallback na fila
        writer.SendVideoFrame(Key(1));
        var messages = await RunUntilAsync(writer, stream, m => m.Count == 2);

        Assert.Equal(Config.CodecConfig, Assert.IsType<ConfigMessage>(messages[0]).CodecConfig);
        Assert.IsType<FrameMessage>(messages[1]);
    }

    [Fact]
    public void Fallback_config_after_a_video_config_is_not_sent()
    {
        var writer = new SessionWriter(new GuardedStream(), () => 0);
        writer.SendVideoConfig(Config);

        Assert.False(writer.SendFallbackConfig(Config with { CodecConfig = [] }));
        Assert.Equal(0, writer.PendingFrames);
    }

    /// <summary>Roda o escritor até as mensagens escritas satisfazerem a condição, para e devolve tudo o que saiu.</summary>
    private static async Task<List<Message>> RunUntilAsync(SessionWriter writer, GuardedStream stream, Func<List<Message>, bool> done)
    {
        using var cts = new CancellationTokenSource();
        var run = writer.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done(DecodeComplete(stream.Written)))
        {
            Assert.False(run.IsFaulted, run.Exception?.ToString());
            Assert.True(DateTime.UtcNow < deadline, "o escritor não escreveu o esperado em 5 s");
            await Task.Delay(5);
        }
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return DecodeComplete(stream.Written);
    }

    /// <summary>Decodifica as mensagens completas; uma mensagem cortada no fim (ainda sendo escrita) fica de fora.</summary>
    private static List<Message> DecodeComplete(byte[] bytes)
    {
        var messages = new List<Message>();
        var offset = 0;
        while (bytes.Length - offset >= MessageCodec.HeaderSize)
        {
            var length = (int)BitConverter.ToUInt32(bytes, offset + 1);
            if (bytes.Length - offset - MessageCodec.HeaderSize < length) break;
            messages.Add(MessageCodec.Decode(bytes[offset], bytes.AsSpan(offset + MessageCodec.HeaderSize, length)));
            offset += MessageCodec.HeaderSize + length;
        }
        return messages;
    }

    /// <summary>Guarda o que foi escrito e falha se duas escritas se sobrepõem, como o SslStream.</summary>
    private sealed class GuardedStream : Stream
    {
        private readonly MemoryStream _data = new();
        private int _writing;

        public byte[] Written
        {
            get
            {
                lock (_data) return _data.ToArray();
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _writing, 1) == 1) throw new NotSupportedException("Duas escritas ao mesmo tempo.");
            try
            {
                await Task.Yield();
                lock (_data) _data.Write(buffer.Span);
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("conexão caiu"));

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
