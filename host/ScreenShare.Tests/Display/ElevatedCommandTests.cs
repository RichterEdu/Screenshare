using ScreenShare.Display.Driver;

namespace ScreenShare.Tests.Display;

public sealed class ElevatedCommandTests
{
    [Theory]
    [InlineData(@"C:\repo\host\ScreenShare.DevHost\bin\Debug\net10.0-windows\ScreenShare.DevHost.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE", false)]
    [InlineData(null, false)]
    public void Relaunches_only_the_devhost_executable(string? processPath, bool expected)
    {
        Assert.Equal(expected, ElevatedCommand.CanRelaunch(processPath));
    }
}
