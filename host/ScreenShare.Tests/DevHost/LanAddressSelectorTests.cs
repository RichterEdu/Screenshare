using System.Net;
using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public class LanAddressSelectorTests
{
    private static AdapterInfo Adapter(bool isUp = true, bool hasGateway = true, params string[] addresses) =>
        new(isUp, hasGateway, addresses.Select(IPAddress.Parse).ToList());

    [Fact]
    public void Keeps_the_ipv4_addresses_of_an_active_adapter_with_gateway()
    {
        var result = LanAddressSelector.Select([Adapter(addresses: "192.168.3.70")]);

        Assert.Equal([IPAddress.Parse("192.168.3.70")], result);
    }

    [Fact]
    public void Ignores_virtual_adapters_without_gateway()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter(hasGateway: false, addresses: "54.232.189.113"), // loopback virtual
            Adapter(hasGateway: false, addresses: "26.200.197.205"), // VPN
            Adapter(hasGateway: false, addresses: "172.28.16.1"),    // Hyper-V
            Adapter(addresses: "192.168.3.70"),
        ]);

        Assert.Equal([IPAddress.Parse("192.168.3.70")], result);
    }

    [Fact]
    public void Ignores_adapters_that_are_down()
    {
        var result = LanAddressSelector.Select([Adapter(isUp: false, addresses: "192.168.3.70")]);

        Assert.Empty(result);
    }

    [Fact]
    public void Ignores_ipv6_addresses()
    {
        var result = LanAddressSelector.Select([Adapter(addresses: ["fe80::1", "192.168.3.70"])]);

        Assert.Equal([IPAddress.Parse("192.168.3.70")], result);
    }

    [Fact]
    public void Keeps_the_addresses_of_every_qualifying_adapter()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter(addresses: "192.168.3.70"),  // cabo
            Adapter(addresses: "192.168.3.99"),  // Wi-Fi
        ]);

        Assert.Equal([IPAddress.Parse("192.168.3.70"), IPAddress.Parse("192.168.3.99")], result);
    }
}
