using ScreenShare.Core.Video;

namespace ScreenShare.Tests.Video;

public sealed class PcClockTests
{
    [Theory]
    [InlineData(10_000_000L, 10_000_000L, 1_000_000UL)]
    [InlineData(15L, 10_000_000L, 1UL)]                       // 1,5 µs arredonda para baixo
    [InlineData(0L, 10_000_000L, 0UL)]
    [InlineData(3_000_000_000_000_000L, 10_000_000L, 300_000_000_000_000UL)] // anos de QPC sem estourar
    public void ToMicroseconds_converts_qpc_ticks(long ticks, long frequency, ulong expected)
    {
        Assert.Equal(expected, PcClock.ToMicroseconds(ticks, frequency));
    }

    [Theory]
    [InlineData(-1L, 10_000_000L)]
    [InlineData(10L, 0L)]
    public void ToMicroseconds_rejects_invalid_input(long ticks, long frequency)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcClock.ToMicroseconds(ticks, frequency));
    }

    [Fact]
    public void NowUs_never_goes_backwards()
    {
        var previous = PcClock.NowUs;
        for (var i = 0; i < 10_000; i++)
        {
            var now = PcClock.NowUs;
            Assert.True(now >= previous);
            previous = now;
        }
    }
}
