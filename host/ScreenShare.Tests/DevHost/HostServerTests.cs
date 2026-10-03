using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public sealed class HostServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing = new(TimeProvider.System);
    private readonly DeviceRegistry _devices;
    private readonly HostServer _server;
    private Task _serving = Task.CompletedTask;

    public HostServerTests()
    {
        _identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste");
        _devices = new DeviceRegistry(Path.Combine(_dir, "paired-devices.json"));
        _server = new HostServer(0, 0, _identity, _pairing, _devices, handshakeTimeout: TimeSpan.FromSeconds(2));
    }

    public Task InitializeAsync()
    {
        _server.Start();
        _serving = _server.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _serving;
        _server.Dispose();
        _identity.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static HelloMessage Hello(ushort version = MessageCodec.ProtocolVersion) =>
        new(version, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265);

    /// <summary>TLS na porta Wi-Fi, aceitando só o certificado do PC de teste (como o celular faz).</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectWifiAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
        var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "screenshare",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && HostIdentity.ComputeFingerprint(certificate.GetRawCertData()) == _identity.Fingerprint,
        }, _cts.Token);
        return (client, tls, new MessageReader(tls));
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MessageReader Reader)> ConnectUsbAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.UsbEndPoint.Port, _cts.Token);
        var stream = client.GetStream();
        return (client, stream, new MessageReader(stream));
    }

    private Task SendAsync(Stream stream, Message message) =>
        stream.WriteAsync(MessageCodec.Encode(message), _cts.Token).AsTask();

    /// <summary>Depois de DENIED o PC fecha: a leitura termina (null) ou a conexão cai (IOException).</summary>
    private async Task AssertClosedAsync(MessageReader reader)
    {
        Message? message = null;
        var error = await Record.ExceptionAsync(async () => message = await reader.ReadAsync(_cts.Token));
        Assert.Null(message);
        Assert.True(error is null or IOException, $"erro inesperado: {error}");
    }

    [Fact]
    public async Task Pairing_then_auth_then_hello_gets_config()
    {
        var secret = _pairing.Begin();
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(secret, "Pixel 8"));
        var token = Assert.IsType<PairedMessage>(await reader.ReadAsync(_cts.Token)).Token;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());

        var config = Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        Assert.Equal(2400, config.Width);
        Assert.Equal("Pixel 8", Assert.Single(_devices.Devices).Name);
        Assert.NotNull(_devices.Authenticate(token));
    }

    [Fact]
    public async Task Paired_device_reconnects_with_its_token_and_pings()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(123456789));

        Assert.Equal(new PongMessage(123456789), await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Wrong_pairing_secret_is_denied_and_nothing_is_registered()
    {
        _pairing.Begin();
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(RandomNumberGenerator.GetBytes(32), "Intruso"));

        Assert.Equal(new DeniedMessage(DeniedReason.InvalidPairingSecret), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task Unknown_token_is_denied()
    {
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Removed_device_is_denied()
    {
        var (device, token) = _devices.Add("Pixel 8");
        _devices.Remove(device.Id);
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, new AuthMessage(token));

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Hello_without_auth_on_wifi_is_denied()
    {
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;

        await SendAsync(stream, Hello());

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Usb_port_serves_hello_without_tls_or_pairing()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(42));

        Assert.Equal(new PongMessage(42), await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public void Usb_port_listens_only_on_loopback() =>
        Assert.Equal(IPAddress.Loopback, _server.UsbEndPoint.Address);

    [Fact]
    public async Task Old_app_hello_v1_on_usb_is_denied_with_incompatible_version()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello(version: 1));

        Assert.Equal(new DeniedMessage(DeniedReason.IncompatibleVersion), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Old_app_plain_tcp_on_wifi_port_gets_no_session_and_server_keeps_working()
    {
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
            await plain.GetStream().WriteAsync(MessageCodec.Encode(Hello(version: 1)), _cts.Token);
            Message? reply = null;
            var error = await Record.ExceptionAsync(async () => reply = await new MessageReader(plain.GetStream()).ReadAsync(_cts.Token));
            Assert.Null(reply);
            Assert.True(error is null or IOException, $"erro inesperado: {error}");
        }

        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Silent_peer_on_wifi_port_is_dropped_after_the_handshake_timeout()
    {
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, _server.WifiPort, _cts.Token);
            var read = await silent.GetStream().ReadAsync(new byte[1], _cts.Token); // o PC desiste e fecha

            Assert.Equal(0, read);
        }

        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        using var _ = client;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Server_accepts_a_new_usb_client_after_the_previous_one_disconnects()
    {
        var first = await ConnectUsbAsync();
        await SendAsync(first.Stream, Hello());
        Assert.IsType<ConfigMessage>(await first.Reader.ReadAsync(_cts.Token));
        first.Client.Dispose();

        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }
}
