using System.Diagnostics;
using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class PipelineVideoSourceTests
{
    private static readonly FakeMonitor Monitor = new(new VirtualMonitor(@"\\.\DISPLAY5", 0, 0, 1920, 1080, 100));

    [Fact]
    public async Task Start_runs_the_pipeline_on_its_own_thread_and_dispose_stops_everything()
    {
        var clock = Stopwatch.StartNew();
        var backend = new FakeBackend
        {
            CaptureFactory = name => new FakeCapture(name, 1920, 1080, () => clock.Elapsed) { Period = TimeSpan.FromMilliseconds(16) },
        };
        var source = new PipelineVideoSource(() => backend, VideoCodec.H264 | VideoCodec.H265, new VideoOptions(),
            TimeProvider.System, _ => { });
        var output = new FakeOutput();

        var stream = await source.StartAsync(new VideoRequest(Monitor, VideoCodec.H265, VideoLink.Usb), output, CancellationToken.None);
        Assert.NotNull(stream);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (output.Frames.Count < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "o vídeo não começou em 5 s");
            await Task.Delay(10);
        }
        await stream.DisposeAsync();

        Assert.True(backend.Disposed);
        Assert.All(backend.Captures, capture => Assert.True(capture.Disposed));
        Assert.All(backend.Encoders, encoder => Assert.True(encoder.Disposed));
        Assert.Equal(50_000u, output.Configs[0].BitrateKbps); // padrão do cabo
    }

    [Fact]
    public async Task Phone_without_a_codec_the_host_encodes_gets_no_video()
    {
        var source = new PipelineVideoSource(() => throw new InvalidOperationException("não deve criar"), VideoCodec.H265,
            new VideoOptions(), TimeProvider.System, _ => { });

        var stream = await source.StartAsync(new VideoRequest(Monitor, VideoCodec.H264, VideoLink.Wifi), new FakeOutput(),
            CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public void Bitrate_defaults_to_50_mbps_on_usb_and_25_on_wifi_unless_set()
    {
        Assert.Equal(50_000, new VideoOptions().BitrateKbpsFor(VideoLink.Usb));
        Assert.Equal(25_000, new VideoOptions().BitrateKbpsFor(VideoLink.Wifi));
        Assert.Equal(8_000, new VideoOptions(BitrateMbps: 8).BitrateKbpsFor(VideoLink.Usb));
    }
}
