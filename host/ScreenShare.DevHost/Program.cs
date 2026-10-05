using System.Security.Cryptography;
using Makaretu.Dns;
using QRCoder;
using ScreenShare.Core.Security;
using ScreenShare.DevHost;
using ScreenShare.Video;
using ScreenShare.Video.Hardware;

// Host de desenvolvimento: Wi-Fi com TLS + pareamento por QR (porta 38700) e USB com TLS + pareamento, só em loopback (porta 38701).
// Comandos no console: p = parear celular (mostra o QR), l = listar pareados, r <id> = remover, Ctrl+C = sair.
// Modos de linha de comando (pedem administrador): install-driver, uninstall-driver, restart-driver.
// Opções: --sem-monitor (não liga o monitor virtual nem mexe no driver), --sem-video, --capturar principal (vídeo do
// monitor principal, para depurar sem o driver), --gravar <arquivo> (o vídeo em Annex-B, para o ffplay),
// --fps <1-120> (padrão 60), --bitrate <Mbps> (padrão 50 no cabo e 25 no Wi-Fi), --codec h264|h265|auto.
if (DriverCommands.IsDriverCommand(args)) return await DriverCommands.RunAsync(args);

DevHostOptions options;
try
{
    options = DevHostOptions.Parse(args);
}
catch (OptionsException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(DevHostOptions.Usage);
    return 2;
}

const int WifiPort = 38700;
const int UsbPort = 38701;

void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenShare");
using var identity = LoadIdentityOrNull(dataDirectory);
if (identity is null) return 1;
var pairing = new PairingSession(TimeProvider.System);
var devices = new DeviceRegistry(Path.Combine(dataDirectory, "paired-devices.json"), log: Log);

// Monitor virtual: liga quando um celular conecta (--sem-monitor desliga o recurso e não mexe no driver).
using var monitors = options.NoMonitor ? null : MonitorSetup.Create(Log);

// Vídeo: captura + encoder de hardware por sessão (--sem-video desliga).
var video = options.NoVideo ? null : VideoSourceFactory.CreateDefault(options.Video, Log);
if (video is not null && options.CapturePrimary) video = new MonitorOverrideVideoSource(video, () => new PrimaryMonitorSource());
if (video is not null && options.RecordPath is { } recordPath) video = new RecordingVideoSource(video, recordPath, Log);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var server = new HostServer(WifiPort, UsbPort, identity, pairing, devices, Log, monitors: monitors, video: video);
server.Start();

// Anuncia só IPs da LAN real; sem nenhum (ex.: sem gateway), cai no padrão da biblioteca (todos os IPs).
var lanAddresses = LanAddressSelector.Select(AdapterInfo.FromSystem());
var profile = new ServiceProfile(
    Environment.MachineName, "_screenshare._tcp", (ushort)server.WifiPort, lanAddresses.Count > 0 ? lanAddresses : null);
profile.AddProperty("fp", identity.MdnsId);
using var discovery = new ServiceDiscovery();
discovery.Advertise(profile);

Console.WriteLine($"ScreenShare DevHost \"{Environment.MachineName}\": Wi-Fi (TLS) na porta {server.WifiPort}, USB (TLS, só loopback) na {server.UsbEndPoint}.");
Console.WriteLine($"IPs anunciados: {(lanAddresses.Count > 0 ? string.Join(", ", lanAddresses) : "todos")}. Digital: {identity.Fingerprint}");
Console.WriteLine(monitors is null
    ? "Monitor virtual: desligado (sem driver ou --sem-monitor); o CONFIG leva a resolução do celular."
    : "Monitor virtual: pronto; liga quando um celular conecta e sai da área de trabalho 10 s depois que ele desconecta.");
Console.WriteLine(video is null
    ? "Vídeo: desligado (sem encoder de hardware ou --sem-video)."
    : $"Vídeo: até {options.Video.Fps} fps{(options.CapturePrimary ? ", capturando o monitor principal" : "")}.");
Console.WriteLine("Comandos: p = parear celular, l = listar pareados, r <id> = remover, Ctrl+C = sair.");

// Uma falha num comando (ex.: erro de disco ao remover um celular) não pode encerrar o laço: p/l/r continuam respondendo.
_ = Task.Run(() =>
{
    while (Console.ReadLine() is { } line)
    {
        try
        {
            HandleCommand(line.Trim());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Falha no comando: {ex.Message}");
        }
    }
});

await server.RunAsync(cts.Token);
return 0;

// Certificado corrompido ou ilegível (ex.: criado por outro usuário do Windows, a DPAPI não decifra): explica o que fazer, sem stack trace.
HostIdentity? LoadIdentityOrNull(string directory)
{
    try
    {
        return HostIdentity.LoadOrCreate(directory, Environment.MachineName);
    }
    catch (CryptographicException ex)
    {
        Console.Error.WriteLine($"Não foi possível abrir a identidade do PC em {directory}: {ex.Message}");
        Console.Error.WriteLine("Apague host-cert.pfx e host-cert.key nessa pasta e rode de novo; será preciso parear os celulares outra vez (a identidade do PC vai mudar).");
        return null;
    }
}

void HandleCommand(string line)
{
    if (line == "p")
    {
        // Prefere IP de rede privada: um adaptador de VPN com gateway pode vir antes do da LAN e o celular não o alcança.
        var pairingIp = LanAddressSelector.PickForPairing(lanAddresses);
        if (pairingIp is null)
        {
            Console.WriteLine("Nenhum IP de rede local encontrado: conecte o PC ao Wi-Fi/rede para parear.");
            return;
        }
        var uri = PairingUri.Build(pairingIp.ToString(), server.WifiPort, identity.Fingerprint, pairing.Begin(), Environment.MachineName);
        using var qr = new QRCodeGenerator().CreateQrCode(uri, QRCodeGenerator.ECCLevel.L);
        Console.WriteLine(new AsciiQRCode(qr).GetGraphicSmall());
        Console.WriteLine($"Escaneie no app (vale {PairingSession.Lifetime.TotalMinutes:0} minutos, uma vez): {uri}");
        var others = lanAddresses.Where(ip => !ip.Equals(pairingIp)).ToList();
        Console.WriteLine($"IP no QR: {pairingIp}{(others.Count > 0 ? $" (outros: {string.Join(", ", others)})" : "")}");
    }
    else if (line == "l")
    {
        var list = devices.Devices;
        Console.WriteLine(list.Count == 0
            ? "Nenhum celular pareado."
            : string.Join(Environment.NewLine, list.Select(d => $"  {d.Id}  {HostServer.Sanitize(d.Name)}  (pareado em {d.PairedAt.ToLocalTime():g})")));
    }
    else if (line.StartsWith("r ", StringComparison.Ordinal))
    {
        var id = line[2..].Trim();
        Console.WriteLine(devices.Remove(id) ? $"Removido: {id}" : $"Nenhum celular com id {id}.");
    }
    else if (line.Length > 0)
    {
        Console.WriteLine("Comandos: p = parear celular, l = listar pareados, r <id> = remover, Ctrl+C = sair.");
    }
}
