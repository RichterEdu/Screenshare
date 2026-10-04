using ScreenShare.DevHost;
using ScreenShare.Display.Driver;

namespace ScreenShare.Tests.DevHost;

public sealed class DriverCommandsTests
{
    [Theory]
    [InlineData(new[] { "install-driver" }, true)]
    [InlineData(new[] { "install-driver", "S-1-5-21-1" }, true)]
    [InlineData(new[] { "uninstall-driver" }, true)]
    [InlineData(new[] { "restart-driver" }, true)]
    [InlineData(new[] { "--sem-monitor" }, false)]
    [InlineData(new string[0], false)]
    public void Recognizes_only_the_driver_commands(string[] args, bool expected)
    {
        Assert.Equal(expected, DriverCommands.IsDriverCommand(args));
    }

    [Theory]
    [InlineData("install-driver", 0, "instalado")]
    [InlineData("uninstall-driver", 0, "removido")]
    [InlineData("restart-driver", 0, "reiniciado")]
    [InlineData("install-driver", 2, "SHA-256")]
    [InlineData("install-driver", 5, "recusada")]
    [InlineData("install-driver", 4, "código 4")]
    [InlineData("install-driver", 1, "código 1")]
    public void Describes_each_exit_code(string command, int code, string expected)
    {
        Assert.Contains(expected, DriverCommands.Describe(command, code));
    }

    [Fact]
    public void Download_failure_mentions_the_release_page()
    {
        Assert.Contains(VddPackage.ReleasePage, DriverCommands.Describe("install-driver", 3));
    }
}
