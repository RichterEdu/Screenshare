using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class PacingTests
{
    [Fact]
    public void Jittery_60hz_frames_all_go_out_at_60fps()
    {
        var pacer = new FramePacer(60);
        var sent = 0;

        for (var i = 0; i < 60; i++)
        {
            var now = TimeSpan.FromMilliseconds(i * 1000.0 / 60 + (i % 2 == 0 ? 2 : -2));
            if (pacer.Delay(now) != TimeSpan.Zero) continue;
            pacer.MarkSent(now);
            sent++;
        }

        Assert.Equal(60, sent);
    }

    [Fact]
    public void After_a_pause_the_next_frame_goes_out_at_once_and_the_cadence_restarts()
    {
        var pacer = new FramePacer(60);
        pacer.MarkSent(TimeSpan.Zero);

        Assert.True(pacer.Delay(TimeSpan.FromMilliseconds(5)) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, pacer.Delay(TimeSpan.FromSeconds(2)));
        pacer.MarkSent(TimeSpan.FromSeconds(2));
        Assert.True(pacer.Delay(TimeSpan.FromMilliseconds(2005)) > TimeSpan.Zero);
    }

    [Fact]
    public void Nothing_is_due_without_a_request()
    {
        Assert.False(new KeyframeScheduler().TakeDue(TimeSpan.Zero));
    }

    [Fact]
    public void Keyframe_requests_are_200ms_apart_joined_and_never_lost()
    {
        var scheduler = new KeyframeScheduler();
        scheduler.Request();
        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(10)));

        scheduler.Request();
        scheduler.Request();
        Assert.False(scheduler.TakeDue(TimeSpan.FromMilliseconds(100)));
        Assert.True(scheduler.IsDue(TimeSpan.FromMilliseconds(210)));
        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(210)));
        Assert.False(scheduler.TakeDue(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void RequestNow_ignores_the_minimum_interval()
    {
        var scheduler = new KeyframeScheduler();
        scheduler.Request();
        scheduler.TakeDue(TimeSpan.Zero);

        scheduler.RequestNow();

        Assert.True(scheduler.TakeDue(TimeSpan.FromMilliseconds(1)));
    }
}
