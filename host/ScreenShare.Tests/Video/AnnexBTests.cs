using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Tests.Protocol;

namespace ScreenShare.Tests.Video;

public sealed class AnnexBTests
{
    // H.265: VPS, SPS e PPS com start code de 4 bytes; o IDR com start code de 3 bytes.
    private static readonly byte[] H265Idr =
    [
        0, 0, 0, 1, 0x40, 0x01, 0x0C,     // VPS (32)
        0, 0, 0, 1, 0x42, 0x01, 0x01,     // SPS (33)
        0, 0, 0, 1, 0x44, 0x01, 0xC0,     // PPS (34)
        0, 0, 1, 0x26, 0x01, 0xAF, 0x05,  // IDR_W_RADL (19)
    ];

    // H.264 como os encoders costumam entregar: AUD, SPS, PPS, SEI e o IDR.
    private static readonly byte[] H264IdrWithAudAndSei =
    [
        0, 0, 0, 1, 0x09, 0xF0,              // AUD (9)
        0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F,  // SPS (7)
        0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,  // PPS (8)
        0, 0, 0, 1, 0x06, 0x05, 0x01,        // SEI (6)
        0, 0, 0, 1, 0x65, 0x88, 0x84,        // IDR (5)
    ];

    [Fact]
    public void Split_finds_nal_units_with_3_and_4_byte_start_codes()
    {
        var units = AnnexB.Split(H265Idr, VideoCodec.H265);

        Assert.Equal(new[] { 32, 33, 34, 19 }, units.Select(u => u.Type));
        Assert.Equal(new NalUnit(4, 3, 32), units[0]);
        Assert.Equal(new NalUnit(11, 3, 33), units[1]);
        Assert.Equal(new NalUnit(18, 3, 34), units[2]);
        Assert.Equal(new NalUnit(24, 4, 19), units[3]);
    }

    [Fact]
    public void Split_ignores_bytes_before_the_first_start_code()
    {
        var units = AnnexB.Split(new byte[] { 0xFF, 0xEE, 0, 0, 1, 0x65, 0x88 }, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(5, 2, 5) }, units);
    }

    [Fact]
    public void Split_drops_the_zeros_that_precede_a_start_code()
    {
        // O zero extra do start code de 4 bytes e os trailing_zero_8bits não fazem parte da NAL.
        byte[] data = [0, 0, 1, 0x09, 0xF0, 0x00, 0x00, 0, 0, 0, 1, 0x65, 0x88, 0x00];

        var units = AnnexB.Split(data, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(3, 2, 9), new NalUnit(11, 2, 5) }, units);
    }

    [Fact]
    public void Emulation_prevention_bytes_do_not_split_a_nal_unit()
    {
        byte[] data = [0, 0, 0, 1, 0x65, 0x00, 0x00, 0x03, 0x01, 0x42];

        var units = AnnexB.Split(data, VideoCodec.H264);

        Assert.Equal(new[] { new NalUnit(4, 6, 5) }, units);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x65, 0x88, 0x84 })]
    public void Data_without_start_code_has_no_nal_units(byte[] data)
    {
        Assert.Empty(AnnexB.Split(data, VideoCodec.H264));
    }

    [Theory]
    [InlineData(VideoCodec.None)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265)]
    public void Codec_must_be_exactly_one(VideoCodec codec)
    {
        Assert.Throws<ArgumentException>(() => AnnexB.Split(H265Idr, codec));
    }

    [Theory]
    [InlineData(VideoCodec.H264, (byte)0x65, true)]   // IDR
    [InlineData(VideoCodec.H264, (byte)0x41, false)]  // P (tipo 1)
    [InlineData(VideoCodec.H265, (byte)0x26, true)]   // IDR_W_RADL (19)
    [InlineData(VideoCodec.H265, (byte)0x28, true)]   // IDR_N_LP (20)
    [InlineData(VideoCodec.H265, (byte)0x2A, true)]   // CRA (21)
    [InlineData(VideoCodec.H265, (byte)0x02, false)]  // TRAIL_R (1)
    [InlineData(VideoCodec.H265, (byte)0x40, false)]  // VPS sozinho
    public void IsKeyframe_looks_at_the_nal_types(VideoCodec codec, byte header, bool expected)
    {
        byte[] accessUnit = [0, 0, 0, 1, header, 0x01, 0x80];

        Assert.Equal(expected, AnnexB.IsKeyframe(accessUnit, codec));
    }

    [Fact]
    public void Frame_vector_data_is_an_h265_keyframe()
    {
        var data = Vectors.Load("frame.hex")[MessageCodec.FrameHeaderSize..];

        Assert.True(AnnexB.IsKeyframe(data, VideoCodec.H265));
    }

    [Fact]
    public void Config_vector_codec_config_is_an_h265_vps()
    {
        var payload = Vectors.Load("config.hex")[MessageCodec.HeaderSize..];
        var codecConfig = payload[11..];

        Assert.Equal(new[] { AnnexB.H265Vps }, AnnexB.Split(codecConfig, VideoCodec.H265).Select(u => u.Type));
    }

    [Fact]
    public void ExtractParameterSets_returns_vps_sps_pps_with_4_byte_start_codes()
    {
        var sets = AnnexB.ExtractParameterSets(H265Idr, VideoCodec.H265);

        Assert.Equal(new byte[]
        {
            0, 0, 0, 1, 0x40, 0x01, 0x0C,
            0, 0, 0, 1, 0x42, 0x01, 0x01,
            0, 0, 0, 1, 0x44, 0x01, 0xC0,
        }, sets);
    }

    [Fact]
    public void ExtractParameterSets_is_null_when_a_set_is_missing()
    {
        var withoutPps = H265Idr[..14].Concat(H265Idr[21..]).ToArray();

        Assert.Null(AnnexB.ExtractParameterSets(withoutPps, VideoCodec.H265));
    }

    [Fact]
    public void H264_parameter_sets_ignore_aud_and_sei()
    {
        Assert.True(AnnexB.IsKeyframe(H264IdrWithAudAndSei, VideoCodec.H264));
        Assert.Equal(new byte[]
        {
            0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F,
            0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80,
        }, AnnexB.ExtractParameterSets(H264IdrWithAudAndSei, VideoCodec.H264));
    }

    [Theory]
    [InlineData("annexb/h264-idr.hex", VideoCodec.H264)]
    [InlineData("annexb/h265-idr.hex", VideoCodec.H265)]
    public void Real_encoder_idr_from_the_spike_is_a_keyframe_with_parameter_sets(string vector, VideoCodec codec)
    {
        var accessUnit = Vectors.Load(vector);

        Assert.True(AnnexB.IsKeyframe(accessUnit, codec));
        var sets = AnnexB.ExtractParameterSets(accessUnit, codec);
        Assert.NotNull(sets);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, sets[..4]);
    }
}
