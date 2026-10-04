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

    private static IPAddress[] Ips(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();

    [Fact]
    public void PickForPairing_prefers_a_private_address_even_when_listed_after_a_public_one()
    {
        var result = LanAddressSelector.PickForPairing(Ips("26.200.197.205", "192.168.3.70"));

        Assert.Equal(IPAddress.Parse("192.168.3.70"), result);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.10")]
    public void PickForPairing_recognizes_the_private_ranges(string privateAddress)
    {
        var result = LanAddressSelector.PickForPairing(Ips("26.200.197.205", privateAddress));

        Assert.Equal(IPAddress.Parse(privateAddress), result);
    }

    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("11.0.0.1")]
    public void PickForPairing_does_not_treat_neighbors_of_the_private_ranges_as_private(string publicAddress)
    {
        var result = LanAddressSelector.PickForPairing(Ips(publicAddress, "192.168.3.70"));

        Assert.Equal(IPAddress.Parse("192.168.3.70"), result);
    }

    [Fact]
    public void PickForPairing_returns_the_first_private_address_when_there_are_several()
    {
        var result = LanAddressSelector.PickForPairing(Ips("26.200.197.205", "192.168.3.70", "10.0.0.5"));

        Assert.Equal(IPAddress.Parse("192.168.3.70"), result);
    }

    [Fact]
    public void PickForPairing_returns_the_first_address_when_none_is_private()
    {
        var result = LanAddressSelector.PickForPairing(Ips("26.200.197.205", "54.232.189.113"));

        Assert.Equal(IPAddress.Parse("26.200.197.205"), result);
    }

    [Fact]
    public void PickForPairing_returns_null_for_an_empty_list()
    {
        Assert.Null(LanAddressSelector.PickForPairing([]));
    }
}
