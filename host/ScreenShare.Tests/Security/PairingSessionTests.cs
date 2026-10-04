using ScreenShare.Core.Security;

namespace ScreenShare.Tests.Security;

public class PairingSessionTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public void Begin_returns_a_32_byte_secret_that_is_accepted_once()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.Equal(32, secret.Length);
        Assert.True(session.TryConsume(secret));
        Assert.False(session.TryConsume(secret)); // uso único
    }

    [Fact]
    public void Wrong_secret_is_rejected_and_the_right_one_still_works()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.False(session.TryConsume(new byte[32]));
        Assert.True(session.TryConsume(secret));
    }

    [Fact]
    public void Secret_expires_after_two_minutes()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        _clock.Now += PairingSession.Lifetime;

        Assert.False(session.TryConsume(secret));
    }

    [Fact]
    public void Secret_is_still_valid_just_before_expiring()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        _clock.Now += PairingSession.Lifetime - TimeSpan.FromSeconds(1);

        Assert.True(session.TryConsume(secret));
    }

    [Fact]
    public void Beginning_again_invalidates_the_previous_secret()
    {
        var session = new PairingSession(_clock);
        var old = session.Begin();
        var current = session.Begin();

        Assert.False(session.TryConsume(old));
        Assert.True(session.TryConsume(current));
    }

    [Fact]
    public void Nothing_is_accepted_before_begin() =>
        Assert.False(new PairingSession(_clock).TryConsume(new byte[32]));

    [Fact]
    public void Secret_of_wrong_length_is_rejected()
    {
        var session = new PairingSession(_clock);
        var secret = session.Begin();

        Assert.False(session.TryConsume(secret.AsSpan(0, 31)));
        Assert.True(session.TryConsume(secret));
    }
}
