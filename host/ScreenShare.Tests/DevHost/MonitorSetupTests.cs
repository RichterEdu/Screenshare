using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public sealed class MonitorSetupTests
{
    [Theory]
    [InlineData("", true)]     // Enter = sim (o padrão é [S/n])
    [InlineData("s", true)]
    [InlineData(" S ", true)]
    [InlineData("sim", true)]
    [InlineData("y", true)]
    [InlineData("n", false)]
    [InlineData("não", false)]
    [InlineData("talvez", false)]
    public void IsYes_accepts_enter_and_yes(string answer, bool expected)
    {
        Assert.Equal(expected, MonitorSetup.IsYes(answer));
    }
}
