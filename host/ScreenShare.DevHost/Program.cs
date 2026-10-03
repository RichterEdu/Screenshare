using Makaretu.Dns;
using ScreenShare.DevHost;

// Host de desenvolvimento: escuta TCP, anuncia o serviço por mDNS e responde ao handshake e ao PING.
// Uso: ScreenShare.DevHost [porta]   (padrão 38700, ver docs/protocol.md)
const int DefaultPort = 38700;
var port = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : DefaultPort;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenShare");
using var identity = ScreenShare.Core.Security.HostIdentity.LoadOrCreate(dataDirectory, Environment.MachineName);
var devices = new ScreenShare.Core.Security.DeviceRegistry(Path.Combine(dataDirectory, "paired-devices.json"));
using var server = new HostServer(port, 38701, identity, new ScreenShare.Core.Security.PairingSession(TimeProvider.System), devices,
    message => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"));
server.Start();

// Anuncia só IPs da LAN real; sem nenhum (ex.: sem gateway), cai no padrão da biblioteca (todos os IPs).
var lanAddresses = LanAddressSelector.Select(AdapterInfo.FromSystem());
using var discovery = new ServiceDiscovery();
discovery.Advertise(new ServiceProfile(
    Environment.MachineName, "_screenshare._tcp", (ushort)server.WifiPort, lanAddresses.Count > 0 ? lanAddresses : null));

Console.WriteLine($"ScreenShare DevHost escutando na porta {server.WifiPort} como \"{Environment.MachineName}\" (_screenshare._tcp).");
Console.WriteLine($"IPs anunciados: {(lanAddresses.Count > 0 ? string.Join(", ", lanAddresses) : "todos")}. Ctrl+C para sair.");
await server.RunAsync(cts.Token);
