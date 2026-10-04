using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class DisplayTopologyTests
{
    // O Windows informa a escala em passos relativos à recomendada, na lista 100, 125, 150, 175, 200, 225, 250, 300...
    // O índice da recomendada é |min|; o spike viu min 0 e max 3 (100% recomendada, 175% no máximo).

    [Theory]
    [InlineData(0, 3, 100, 0)]
    [InlineData(0, 3, 150, 2)]
    [InlineData(0, 3, 200, 3)]   // limitado ao máximo do Windows (175%)
    [InlineData(0, 5, 200, 4)]
    [InlineData(-1, 3, 100, -1)] // recomendada 125%
    [InlineData(-1, 3, 125, 0)]
    [InlineData(0, 3, 110, 0)]   // degrau mais alto que não passa do pedido
    public void ToRelativeStep_finds_the_step_within_the_windows_limits(int min, int max, int percent, int expected)
    {
        Assert.Equal(expected, DisplayTopology.ToRelativeStep(min, max, percent));
    }

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(0, 2, 150)]
    [InlineData(-1, 0, 125)]
    [InlineData(0, 99, 500)]   // fora da lista: fica no último degrau
    [InlineData(0, -5, 100)]
    public void ToPercent_reads_the_current_scale(int min, int current, int expected)
    {
        Assert.Equal(expected, DisplayTopology.ToPercent(min, current));
    }
}
