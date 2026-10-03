using ScreenShare.Core.Security;
using ScreenShare.Tests.Protocol;

namespace ScreenShare.Tests.Security;

public class PairingUriTests
{
    [Fact]
    public void Builds_the_shared_example()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var uri = PairingUri.Build("192.168.0.10", 38700, "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8", secret, "PC da Sala");

        Assert.Equal(Vectors.Text("pairing-uri.txt"), uri);
    }

    [Fact]
    public void Escapes_special_characters_in_the_pc_name() =>
        Assert.EndsWith("&n=PC%26Casa%3DSala",
            PairingUri.Build("10.0.0.2", 38700, "fp", new byte[32], "PC&Casa=Sala"));
}
