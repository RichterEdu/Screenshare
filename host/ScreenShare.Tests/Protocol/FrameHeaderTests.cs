using ScreenShare.Core.Protocol;

namespace ScreenShare.Tests.Protocol;

public sealed class FrameHeaderTests
{
    [Fact]
    public void Header_plus_data_is_the_frame_vector()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x01, 0x26, 0x01, 0xAF];
        var header = new byte[MessageCodec.FrameHeaderSize];

        MessageCodec.WriteFrameHeader(header, 1_000_000, isKeyframe: true, data.Length);

        Assert.Equal(Vectors.Load("frame.hex"), header.Concat(data).ToArray());
    }

    [Fact]
    public void Payload_over_the_limit_is_rejected()
    {
        var header = new byte[MessageCodec.FrameHeaderSize];

        Assert.Throws<ProtocolException>(() =>
            MessageCodec.WriteFrameHeader(header, 0, false, MessageCodec.MaxPayloadLength - 8));
    }

    [Fact]
    public void Destination_too_small_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MessageCodec.WriteFrameHeader(new byte[MessageCodec.FrameHeaderSize - 1], 0, false, 0));
    }
}
