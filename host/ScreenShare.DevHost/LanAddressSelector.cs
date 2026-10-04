using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenShare.DevHost;

/// <summary>Resumo de um adaptador de rede: só o que importa para decidir se o celular consegue alcançá-lo.</summary>
public sealed record AdapterInfo(bool IsUp, bool HasGateway, IReadOnlyList<IPAddress> Addresses)
{
    public static IEnumerable<AdapterInfo> FromSystem() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n =>
            {
                var props = n.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && g.Address.AddressFamily == AddressFamily.InterNetwork);
                return new AdapterInfo(
                    n.OperationalStatus == OperationalStatus.Up,
                    hasGateway,
                    props.UnicastAddresses.Select(a => a.Address).ToList());
            });
}

/// <summary>
/// Escolhe quais IPs anunciar por mDNS. O PC costuma ter adaptadores virtuais (VPN, Hyper-V, loopback) cujos IPs o celular
/// não alcança; o celular pega o primeiro anunciado, então anunciamos só adaptadores ativos com gateway (os que saem para a LAN).
/// </summary>
public static class LanAddressSelector
{
    public static IReadOnlyList<IPAddress> Select(IEnumerable<AdapterInfo> adapters) =>
        adapters
            .Where(a => a.IsUp && a.HasGateway)
            .SelectMany(a => a.Addresses)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ToList();

    /// <summary>
    /// Escolhe o IP que vai no QR de pareamento: o primeiro IPv4 de rede privada (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16),
    /// que é o que um celular na mesma rede alcança; sem nenhum, o primeiro da lista; lista vazia, null.
    /// Não depende da ordem dos adaptadores (um adaptador de VPN com gateway pode vir antes do da LAN).
    /// </summary>
    public static IPAddress? PickForPairing(IReadOnlyList<IPAddress> addresses) =>
        addresses.FirstOrDefault(IsPrivateIPv4) ?? addresses.FirstOrDefault();

    private static bool IsPrivateIPv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
