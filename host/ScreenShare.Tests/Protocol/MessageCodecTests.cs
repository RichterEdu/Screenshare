using System.Buffers.Binary;
using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public class MessageCodecTests
{
    private static Message DecodeVector(byte[] vector) =>
        MessageCodec.Decode(vector[0], vector.AsSpan(MessageCodec.HeaderSize));

    [Fact]
    public void Hello_matches_vector()
    {
        var vector = Vectors.Load("hello.hex");
        var hello = new HelloMessage(1, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265);

        Assert.Equal(vector, MessageCodec.Encode(hello));
        Assert.Equal(hello, DecodeVector(vector));
    }

    [Fact]
    public void Ping_matches_vector()
    {
        var vector = Vectors.Load("ping.hex");
        var ping = new PingMessage(123456789);

        Assert.Equal(vector, MessageCodec.Encode(ping));
        Assert.Equal(ping, DecodeVector(vector));
    }

    [Fact]
    public void Pong_matches_vector()
    {
        var vector = Vectors.Load("pong.hex");
        var pong = new PongMessage(123456789);

        Assert.Equal(vector, MessageCodec.Encode(pong));
        Assert.Equal(pong, DecodeVector(vector));
    }

    [Fact]
    public void KeyframeRequest_matches_vector()
    {
        var vector = Vectors.Load("keyframe_req.hex");

        Assert.Equal(vector, MessageCodec.Encode(new KeyframeRequestMessage()));
        Assert.Equal(new KeyframeRequestMessage(), DecodeVector(vector));
    }

    [Fact]
    public void Unknown_type_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode(0x63, ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Truncated_payload_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Hello, new byte[8]));

    [Fact]
    public void Trailing_bytes_are_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Ping, new byte[9]));

    [Theory]
    [InlineData(0)] // nenhum codec
    [InlineData(4)] // bit desconhecido
    public void Hello_with_invalid_codec_flags_is_rejected(byte flags)
    {
        var payload = new byte[] { 1, 0, 0x60, 0x09, 0x38, 0x04, 0xA4, 0x01, flags };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Hello, payload));
    }
}
