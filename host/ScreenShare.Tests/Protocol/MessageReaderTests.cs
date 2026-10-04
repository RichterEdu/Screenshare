using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public class MessageReaderTests
{
    [Fact]
    public async Task Reads_consecutive_messages_then_null_at_clean_end()
    {
        var bytes = Vectors.Load("hello.hex")
            .Concat(Vectors.Load("ping.hex"))
            .Concat(Vectors.Load("keyframe_req.hex"))
            .ToArray();
        var reader = new MessageReader(new MemoryStream(bytes));

        Assert.Equal(new HelloMessage(2, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265), await reader.ReadAsync());
        Assert.Equal(new PingMessage(123456789), await reader.ReadAsync());
        Assert.Equal(new KeyframeRequestMessage(), await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Reassembles_messages_delivered_one_byte_at_a_time()
    {
        var reader = new MessageReader(new OneByteAtATimeStream(Vectors.Load("touch.hex")));

        var touch = Assert.IsType<TouchMessage>(await reader.ReadAsync());
        Assert.Equal(2, touch.Pointers.Count);
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public async Task Stream_ending_inside_header_throws_EndOfStream()
    {
        var reader = new MessageReader(new MemoryStream([0x05, 0x08]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Stream_ending_inside_payload_throws_EndOfStream()
    {
        var ping = Vectors.Load("ping.hex");
        var reader = new MessageReader(new MemoryStream(ping[..^3]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => reader.ReadAsync());
    }

    [Fact]
    public async Task Oversized_length_is_rejected_before_reading_payload()
    {
        // FRAME declarando 4 GB de payload
        var reader = new MessageReader(new MemoryStream([0x03, 0xFF, 0xFF, 0xFF, 0xFF]));
        await Assert.ThrowsAsync<ProtocolException>(() => reader.ReadAsync());
    }

    /// <summary>Simula o TCP entregando um byte por leitura.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
