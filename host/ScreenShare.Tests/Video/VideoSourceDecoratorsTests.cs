using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.Video;

public sealed class VideoSourceDecoratorsTests : IDisposable
{
    private static readonly VirtualMonitor Primary = new(@"\\.\DISPLAY1", 0, 0, 3440, 1440, 100);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CapturingSource _inner = new();

    public VideoSourceDecoratorsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static VideoRequest Request() =>
        new(VirtualMonitorLease.Without(MonitorRequest.Normalize(2400, 1080, 420)), VideoCodec.H265, VideoLink.Usb);

    [Fact]
    public async Task Override_captures_the_given_monitor_and_keeps_the_rest_of_the_request()
    {
        var source = new MonitorOverrideVideoSource(_inner, () => new FakeMonitor(Primary));

        await source.StartAsync(Request(), new FakeOutput(), CancellationToken.None);

        Assert.Equal(Primary, _inner.Request!.Monitor.Current);
        Assert.Equal((VideoCodec.H265, VideoLink.Usb), (_inner.Request.PhoneCodecs, _inner.Request.Link));
    }

    [Fact]
    public async Task Recording_writes_every_frame_to_the_file_and_passes_everything_on()
    {
        var path = Path.Combine(_dir, "video.h265");
        var output = new FakeOutput();
        var source = new RecordingVideoSource(_inner, path, _ => { });

        var stream = await source.StartAsync(Request(), output, CancellationToken.None);
        _inner.Output!.OnConfig(new ConfigMessage(2400, 1080, VideoCodec.H265, 50_000, []));
        _inner.Output.OnFrame(new FrameMessage(1, true, [0, 0, 0, 1, 0x26]));
        _inner.Output.OnFrame(new FrameMessage(2, false, [0, 0, 0, 1, 0x02]));
        await stream!.DisposeAsync();

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x26, 0, 0, 0, 1, 0x02 }, File.ReadAllBytes(path));
        Assert.Equal(3, output.Sent.Count);
    }

    private sealed class CapturingSource : IVideoSource
    {
        public VideoRequest? Request { get; private set; }
        public IVideoOutput? Output { get; private set; }

        public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
        {
            Request = request;
            Output = output;
            return Task.FromResult<IVideoStream?>(new NothingStream());
        }
    }

    private sealed class NothingStream : IVideoStream
    {
        public VideoStats Stats => VideoStats.Empty;
        public void RequestKeyframe() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
