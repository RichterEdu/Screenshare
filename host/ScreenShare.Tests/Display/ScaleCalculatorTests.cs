using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class ScaleCalculatorTests
{
    [Theory]
    [InlineData(160, 100)]
    [InlineData(200, 125)]
    [InlineData(240, 150)]
    [InlineData(280, 175)]
    [InlineData(300, 200)] // 187,5 arredonda para 200
    [InlineData(320, 200)]
    [InlineData(420, 200)] // 262,5 limitado a 200
    [InlineData(72, 100)]  // 45 limitado a 100
    [InlineData(1000, 200)]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    public void FromDpi_rounds_to_steps_of_25_within_limits(int dpi, int expected)
    {
        Assert.Equal(expected, ScaleCalculator.FromDpi(dpi));
    }
}
