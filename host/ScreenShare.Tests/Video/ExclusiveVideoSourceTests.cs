using ScreenShare.Core.Protocol;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.Video;

public sealed class ExclusiveVideoSourceTests
{
    private static readonly VideoRequest Request =
        new(VirtualMonitorLease.Without(MonitorRequest.Normalize(2400, 1080, 420)), VideoCodec.H264, VideoLink.Wifi);

    private readonly RecordingSource _inner = new();

    [Fact]
    public async Task New_session_stops_the_previous_stream_before_starting_its_own()
    {
        var source = new ExclusiveVideoSource(_inner);

        await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        Assert.Equal(["start1", "stop1", "start2"], _inner.Events);
    }

    [Fact]
    public async Task Disposing_a_stream_that_was_taken_over_does_not_stop_the_new_one()
    {
        var source = new ExclusiveVideoSource(_inner);
        var first = await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        await first!.DisposeAsync();

        Assert.Equal(["start1", "stop1", "start2"], _inner.Events);
    }

    [Fact]
    public async Task Keyframe_request_to_a_stopped_stream_is_ignored()
    {
        var source = new ExclusiveVideoSource(_inner);
        var first = await source.StartAsync(Request, null!, CancellationToken.None);
        await source.StartAsync(Request, null!, CancellationToken.None);

        first!.RequestKeyframe();

        Assert.Equal(0, _inner.Streams[0].KeyframeRequests);
    }

    private sealed class RecordingSource : IVideoSource
    {
        public List<string> Events { get; } = [];
        public List<RecordingStream> Streams { get; } = [];

        public Task<IVideoStream?> StartAsync(VideoRequest request, IVideoOutput output, CancellationToken cancellationToken)
        {
            var stream = new RecordingStream(Streams.Count + 1, Events);
            Streams.Add(stream);
            Events.Add($"start{stream.Id}");
            return Task.FromResult<IVideoStream?>(stream);
        }
    }

    private sealed class RecordingStream(int id, List<string> events) : IVideoStream
    {
        public int Id { get; } = id;
        public int KeyframeRequests { get; private set; }
        public VideoStats Stats => VideoStats.Empty;

        public void RequestKeyframe() => KeyframeRequests++;

        public ValueTask DisposeAsync()
        {
            events.Add($"stop{Id}");
            return ValueTask.CompletedTask;
        }
    }
}
