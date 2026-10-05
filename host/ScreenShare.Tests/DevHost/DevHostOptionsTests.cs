using ScreenShare.Core.Protocol;
using ScreenShare.DevHost;
using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

public sealed class DevHostOptionsTests
{
    [Fact]
    public void No_arguments_give_the_defaults()
    {
        var options = DevHostOptions.Parse([]);

        Assert.Equal(new DevHostOptions(false, false, false, null, new VideoOptions()), options);
        Assert.Equal(60, options.Video.Fps);
        Assert.Null(options.Video.BitrateMbps);
        Assert.Equal(VideoCodec.None, options.Video.Codec);
    }

    [Fact]
    public void Every_option_is_read()
    {
        var options = DevHostOptions.Parse(["--sem-monitor", "--capturar", "principal", "--gravar", "teste.h265",
            "--fps", "30", "--bitrate", "12", "--codec", "h264"]);

        Assert.True(options.NoMonitor);
        Assert.True(options.CapturePrimary);
        Assert.Equal("teste.h265", options.RecordPath);
        Assert.Equal(new VideoOptions(30, 12, VideoCodec.H264), options.Video);
    }

    [Fact]
    public void No_video_is_read()
    {
        Assert.True(DevHostOptions.Parse(["--sem-video"]).NoVideo);
    }

    [Theory]
    [InlineData("--fps", "0")]
    [InlineData("--fps", "121")]
    [InlineData("--fps", "sessenta")]
    [InlineData("--bitrate", "0")]
    [InlineData("--codec", "av1")]
    [InlineData("--capturar", "segundo")]
    [InlineData("--monitor", "1")]
    public void Invalid_options_are_rejected_with_a_message(string option, string value)
    {
        var error = Assert.Throws<OptionsException>(() => DevHostOptions.Parse([option, value]));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Option_without_its_value_is_rejected()
    {
        Assert.Throws<OptionsException>(() => DevHostOptions.Parse(["--gravar"]));
    }

    [Fact]
    public void Stats_line_shows_rates_for_the_period_in_portuguese()
    {
        var before = new VideoStats(2520, 1080, VideoCodec.H265, 100, 1_000_000, 1, 2.5);
        var after = new VideoStats(2520, 1080, VideoCodec.H265, 400, 8_500_000, 3, 3.14);

        var line = VideoStatsLine.Format(before, after, TimeSpan.FromSeconds(5), drops: 1, rttMs: 4.25);

        Assert.Equal("Vídeo: H.265 2520×1080 · 60,0 fps · 12,0 Mbps · 2 IDR · 1 descartes · encode p95 3,1 ms · RTT 4,3 ms", line);
    }

    [Fact]
    public void Stats_line_without_rtt_shows_a_dash()
    {
        var line = VideoStatsLine.Format(VideoStats.Empty, VideoStats.Empty, TimeSpan.FromSeconds(5), 0, null);

        Assert.EndsWith("RTT —", line);
    }
}
