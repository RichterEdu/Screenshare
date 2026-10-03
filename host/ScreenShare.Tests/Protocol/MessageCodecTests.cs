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

    [Fact]
    public void Config_matches_vector()
    {
        var vector = Vectors.Load("config.hex");
        byte[] codecConfig = [0x00, 0x00, 0x00, 0x01, 0x40, 0x01, 0x0C, 0x01];
        var config = new ConfigMessage(2400, 1080, VideoCodec.H265, 20000, codecConfig);

        Assert.Equal(vector, MessageCodec.Encode(config));
        var decoded = Assert.IsType<ConfigMessage>(DecodeVector(vector));
        // Records comparam arrays por referência: compara o resto pelo record e o array pelo conteúdo.
        Assert.Equal(config with { CodecConfig = decoded.CodecConfig }, decoded);
        Assert.Equal(codecConfig, decoded.CodecConfig);
    }

    [Fact]
    public void Frame_matches_vector()
    {
        var vector = Vectors.Load("frame.hex");
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF];
        var frame = new FrameMessage(1_000_000, true, data);

        Assert.Equal(vector, MessageCodec.Encode(frame));
        var decoded = Assert.IsType<FrameMessage>(DecodeVector(vector));
        Assert.Equal(frame with { Data = decoded.Data }, decoded);
        Assert.Equal(data, decoded.Data);
    }

    [Fact]
    public void Touch_matches_vector()
    {
        var vector = Vectors.Load("touch.hex");
        var touch = new TouchMessage([
            new TouchPointer(0, TouchAction.Move, 0.25f, 0.5f, 1.0f),
            new TouchPointer(1, TouchAction.Down, 0.75f, 0.125f, 0.5f),
        ]);

        Assert.Equal(vector, MessageCodec.Encode(touch));
        var decoded = Assert.IsType<TouchMessage>(DecodeVector(vector));
        Assert.Equal(touch.Pointers, decoded.Pointers);
    }

    [Fact]
    public void Config_with_unknown_codec_is_rejected()
    {
        // codec=3 (H264|H265) não é um codec único
        var payload = new byte[] { 0x60, 0x09, 0x38, 0x04, 3, 0x20, 0x4E, 0, 0, 0, 0 };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Config, payload));
    }

    [Fact]
    public void Config_with_codec_config_longer_than_payload_is_rejected()
    {
        // declara 8 bytes de codec config mas traz só 2
        var payload = new byte[] { 0x60, 0x09, 0x38, 0x04, 2, 0x20, 0x4E, 0, 0, 8, 0, 0xAA, 0xBB };
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Config, payload));
    }

    [Fact]
    public void Frame_shorter_than_its_fixed_fields_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Frame, new byte[8]));

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Touch_with_invalid_pointer_count_is_rejected(int count)
    {
        var payload = new byte[1 + 14 * count];
        payload[0] = (byte)count;
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Touch_with_unknown_action_is_rejected()
    {
        var payload = new byte[1 + 14];
        payload[0] = 1; // count
        payload[2] = 9; // action do primeiro ponteiro
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Touch_with_non_finite_value_is_rejected()
    {
        var payload = new byte[1 + 14];
        payload[0] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(3), float.NaN); // x do primeiro ponteiro
        Assert.Throws<ProtocolException>(() => MessageCodec.Decode((byte)MessageType.Touch, payload));
    }

    [Fact]
    public void Encoding_touch_without_pointers_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(new TouchMessage([])));

    [Fact]
    public void Encoding_codec_config_over_65535_bytes_is_rejected() =>
        Assert.Throws<ProtocolException>(() => MessageCodec.Encode(
            new ConfigMessage(1920, 1080, VideoCodec.H264, 8000, new byte[70_000])));
}
