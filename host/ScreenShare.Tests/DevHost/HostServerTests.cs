using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.Core.Video;
using ScreenShare.DevHost;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.Tests.DevHost;

public sealed class HostServerTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screenshare-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing = new(TimeProvider.System);
    private readonly DeviceRegistry _devices;
    private readonly FakeMonitorManager _monitors = new();
    private readonly FakeVideoSource _video = new();
    private readonly HostServer _server;
    private Task _serving = Task.CompletedTask;

    public HostServerTests()
    {
        _identity = HostIdentity.LoadOrCreate(_dir, "PC de Teste");
        _devices = new DeviceRegistry(Path.Combine(_dir, "paired-devices.json"));
        _server = new HostServer(0, 0, _identity, _pairing, _devices,
            handshakeTimeout: TimeSpan.FromSeconds(2), idleTimeout: TimeSpan.FromSeconds(2), monitors: _monitors,
            video: _video, pingInterval: TimeSpan.FromMilliseconds(200), videoConfigTimeout: TimeSpan.FromMilliseconds(500));
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

    /// <summary>TLS numa porta do servidor, aceitando só o certificado do PC de teste (como o celular faz).</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectSecureAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, _cts.Token);
        var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "screenshare",
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && HostIdentity.ComputeFingerprint(certificate.GetRawCertData()) == _identity.Fingerprint,
        }, _cts.Token);
        return (client, tls, new MessageReader(tls));
    }

    private Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectWifiAsync() =>
        ConnectSecureAsync(_server.WifiPort);

    private Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectUsbAsync() =>
        ConnectSecureAsync(_server.UsbEndPoint.Port);

    /// <summary>Conecta na porta USB (TLS) e já manda o AUTH de um aparelho recém-adicionado.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader)> ConnectUsbAuthedAsync()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var connection = await ConnectUsbAsync();
        await SendAsync(connection.Stream, new AuthMessage(token));
        return connection;
    }

    private Task SendAsync(Stream stream, Message message) =>
        stream.WriteAsync(MessageCodec.Encode(message), _cts.Token).AsTask();

    /// <summary>O PC fecha: a leitura termina (null) ou a conexão cai (IOException). Os PINGs do PC antes disso não contam.</summary>
    private async Task AssertClosedAsync(MessageReader reader)
    {
        Message? message = null;
        var error = await Record.ExceptionAsync(async () => message = await ReadSkippingPingsAsync(reader));
        Assert.Null(message);
        Assert.True(error is null or IOException, $"erro inesperado: {error}");
    }

    /// <summary>A próxima mensagem que não seja o PING periódico do PC.</summary>
    private async Task<Message?> ReadSkippingPingsAsync(MessageReader reader)
    {
        while (true)
        {
            var message = await reader.ReadAsync(_cts.Token);
            if (message is not PingMessage) return message;
        }
    }

    [Fact]
    public void SanitizeStripsControlCharacters()
    {
        Assert.Equal("Pixel X", HostServer.Sanitize("Pixel \u001bX\r\n\t"));
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

        Assert.Equal(new PongMessage(123456789), await ReadSkippingPingsAsync(reader));
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
        await AssertClosedAsync(reader);
    }

    [Fact]
    public async Task Removed_device_sending_auth_and_hello_separately_still_gets_denied()
    {
        var (device, token) = _devices.Add("Pixel 8");
        _devices.Remove(device.Id);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (client, stream, reader) = await ConnectWifiAsync();
            using (client)
            {
                // o app manda AUTH e HELLO em escritas separadas (dois registros TLS)
                await SendAsync(stream, new AuthMessage(token));
                await SendAsync(stream, Hello());

                Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
            }
        }
    }

    [Fact]
    public async Task Registry_write_failure_during_pairing_does_not_kill_the_wifi_listener()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var registryFile = Path.Combine(_dir, "paired-devices.json");
        File.SetAttributes(registryFile, FileAttributes.ReadOnly); // Add vai falhar ao gravar
        try
        {
            var secret = _pairing.Begin();
            var (broken, brokenStream, brokenReader) = await ConnectWifiAsync();
            using (broken)
            {
                await SendAsync(brokenStream, new PairMessage(secret, "Pixel 9"));
                await AssertClosedAsync(brokenReader);
            }

            // a porta Wi-Fi continua atendendo
            var (client, stream, reader) = await ConnectWifiAsync();
            using var _ = client;
            await SendAsync(stream, new AuthMessage(token));
            await SendAsync(stream, Hello());
            Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        }
        finally
        {
            File.SetAttributes(registryFile, FileAttributes.Normal);
        }
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
    public async Task Usb_port_requires_tls_and_auth_then_serves_hello()
    {
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;

        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        await SendAsync(stream, new PingMessage(42));

        Assert.Equal(new PongMessage(42), await ReadSkippingPingsAsync(reader));
    }

    [Fact]
    public async Task Usb_port_rejects_plain_tcp_hello()
    {
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, _server.UsbEndPoint.Port, _cts.Token);
            await plain.GetStream().WriteAsync(MessageCodec.Encode(Hello()), _cts.Token);
            Message? reply = null;
            var error = await Record.ExceptionAsync(async () => reply = await new MessageReader(plain.GetStream()).ReadAsync(_cts.Token));
            // o servidor só derruba depois do prazo de handshake (2 s no fixture); alerta TLS vira ProtocolException
            Assert.True(reply is not (ConfigMessage or DeniedMessage), $"resposta inesperada: {reply}");
            Assert.True(error is null or IOException or ProtocolException, $"erro inesperado: {error}");
        }

        // a porta continua servindo um cliente de verdade
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Usb_port_denies_hello_without_auth()
    {
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, Hello());

        Assert.Equal(new DeniedMessage(DeniedReason.UnknownDevice), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task Pairing_over_usb_works()
    {
        var secret = _pairing.Begin();
        var (client, stream, reader) = await ConnectUsbAsync();
        using var _ = client;

        await SendAsync(stream, new PairMessage(secret, "Pixel 8"));
        var token = Assert.IsType<PairedMessage>(await reader.ReadAsync(_cts.Token)).Token;
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        Assert.Equal("Pixel 8", Assert.Single(_devices.Devices).Name);
    }

    [Fact]
    public void Usb_port_listens_only_on_loopback() =>
        Assert.Equal(IPAddress.Loopback, _server.UsbEndPoint.Address);

    [Fact]
    public async Task Old_app_hello_v1_on_usb_is_denied_with_incompatible_version()
    {
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;

        await SendAsync(stream, Hello(version: 1));

        Assert.Equal(new DeniedMessage(DeniedReason.IncompatibleVersion), await reader.ReadAsync(_cts.Token));
        await AssertClosedAsync(reader);
        Assert.Equal(0, _monitors.Acquired); // versão errada: nenhum monitor é ligado
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
            // o servidor só derruba depois do prazo de handshake (2 s no fixture); alerta TLS vira ProtocolException
            Assert.True(reply is not (ConfigMessage or DeniedMessage), $"resposta inesperada: {reply}");
            Assert.True(error is null or IOException or ProtocolException, $"erro inesperado: {error}");
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
    public async Task Silent_client_after_config_is_dropped_after_the_idle_timeout_and_a_new_one_is_served()
    {
        var (silent, silentStream, silentReader) = await ConnectUsbAuthedAsync();
        using (silent)
        {
            await SendAsync(silentStream, Hello());
            Assert.IsType<ConfigMessage>(await silentReader.ReadAsync(_cts.Token));

            await AssertClosedAsync(silentReader); // o aparelho sumiu: sem nada por 2 s, o PC derruba a conexão
        }

        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Server_accepts_a_new_usb_client_after_the_previous_one_disconnects()
    {
        var first = await ConnectUsbAuthedAsync();
        await SendAsync(first.Stream, Hello());
        Assert.IsType<ConfigMessage>(await first.Reader.ReadAsync(_cts.Token));
        first.Client.Dispose();

        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        using var _ = client;
        await SendAsync(stream, Hello());

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }

    /// <summary>Espera uma condição que outro fio (o servidor) vai tornar verdadeira.</summary>
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "a condição não ficou verdadeira em 5 s");
            await Task.Delay(20, _cts.Token);
        }
    }

    /// <summary>Wi-Fi + AUTH de um aparelho recém-adicionado + HELLO, até receber o CONFIG.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader, ConfigMessage Config)> ConnectWithHelloAsync()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, reader) = await ConnectWifiAsync();
        await SendAsync(stream, new AuthMessage(token));
        await SendAsync(stream, Hello());
        var config = Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        return (client, stream, reader, config);
    }

    [Fact]
    public async Task Config_carries_the_virtual_monitor_resolution()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 1920, 1080, 175);

        var (client, _, _, config) = await ConnectWithHelloAsync();
        using var _ = client;

        Assert.Equal((1920, 1080), (config.Width, config.Height));
        Assert.Equal((2400, 1080, 420), Assert.Single(_monitors.Requests));
    }

    [Fact]
    public async Task Monitor_is_released_when_the_phone_disconnects()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithHelloAsync();

        client.Dispose();

        await WaitUntilAsync(() => _monitors.Released == 1);
    }

    [Fact]
    public async Task Monitor_is_released_when_the_session_dies_by_timeout()
    {
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithHelloAsync();
        using var _ = client;

        // calado além do idleTimeout (2 s): o servidor derruba a sessão
        await WaitUntilAsync(() => _monitors.Released == 1);
    }

    [Fact]
    public async Task Session_that_ends_before_hello_takes_no_monitor()
    {
        var (_, token) = _devices.Add("Pixel 8");
        var (client, stream, _) = await ConnectWifiAsync();
        await SendAsync(stream, new AuthMessage(token));
        client.Dispose();

        // A porta atende um cliente por vez: quando o próximo recebe CONFIG, o anterior já acabou.
        var (next, _, _, _) = await ConnectWithHelloAsync();
        using var _ = next;

        Assert.Equal(1, _monitors.Acquired);
    }

    /// <summary>Cabo + AUTH + HELLO com o vídeo falso ligado; devolve o stream que a sessão começou.</summary>
    private async Task<(TcpClient Client, SslStream Stream, MessageReader Reader, FakeVideoStream Video)> ConnectWithVideoAsync()
    {
        _video.Enabled = true;
        var (client, stream, reader) = await ConnectUsbAuthedAsync();
        var before = _video.Started.Count;
        await SendAsync(stream, Hello());
        await WaitUntilAsync(() => _video.Started.Count == before + 1);
        return (client, stream, reader, _video.Started[^1]);
    }

    [Fact]
    public async Task Pc_pings_with_its_own_clock_after_the_config()
    {
        var (client, _, reader, _) = await ConnectWithHelloAsync();
        using var _ = client;
        var afterConfig = PcClock.NowUs;

        var first = Assert.IsType<PingMessage>(await reader.ReadAsync(_cts.Token));
        var second = Assert.IsType<PingMessage>(await reader.ReadAsync(_cts.Token));

        Assert.InRange(first.TimestampUs, afterConfig, PcClock.NowUs);
        Assert.True(second.TimestampUs - first.TimestampUs >= 150_000, "o fixture pinga a cada 200 ms");
    }

    [Fact]
    public async Task Video_config_and_frames_reach_the_phone_in_order()
    {
        var (client, _, reader, video) = await ConnectWithVideoAsync();
        using var _ = client;
        var config = new ConfigMessage(2400, 1080, VideoCodec.H265, 25_000, [0, 0, 0, 1, 0x40, 0x01]);

        video.Output.OnConfig(config);
        video.Output.OnFrame(new FrameMessage(10, true, [0, 0, 0, 1, 0x26, 0x01]));
        video.Output.OnFrame(new FrameMessage(20, false, [0, 0, 0, 1, 0x02, 0x01]));

        var received = Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader));
        Assert.Equal(VideoCodec.H265, received.Codec);
        Assert.Equal(config.CodecConfig, received.CodecConfig);
        var key = Assert.IsType<FrameMessage>(await ReadSkippingPingsAsync(reader));
        var p = Assert.IsType<FrameMessage>(await ReadSkippingPingsAsync(reader));
        Assert.Equal((10UL, true), (key.TimestampUs, key.IsKeyframe));
        Assert.Equal((20UL, false), (p.TimestampUs, p.IsKeyframe));
    }

    [Fact]
    public async Task Video_request_carries_the_monitor_the_phone_codecs_and_the_link()
    {
        var monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        _monitors.Monitor = monitor;

        var (client, _, _, video) = await ConnectWithVideoAsync();
        using var _ = client;

        Assert.Equal(monitor, video.Request.Monitor.Current);
        Assert.Equal(VideoCodec.H264 | VideoCodec.H265, video.Request.PhoneCodecs);
        Assert.Equal(VideoLink.Usb, video.Request.Link);
    }

    [Fact]
    public async Task Keyframe_request_from_the_phone_reaches_the_video()
    {
        var (client, stream, _, video) = await ConnectWithVideoAsync();
        using var _ = client;

        await SendAsync(stream, new KeyframeRequestMessage());

        await WaitUntilAsync(() => video.KeyframeRequests == 1);
    }

    [Fact]
    public async Task Video_that_sends_nothing_gets_the_fallback_config()
    {
        var (client, _, reader, _) = await ConnectWithVideoAsync();
        using var _ = client;

        var config = Assert.IsType<ConfigMessage>(await ReadSkippingPingsAsync(reader)); // depois de 500 ms no fixture

        Assert.Equal((2400, 1080), (config.Width, config.Height));
        Assert.Empty(config.CodecConfig);
    }

    [Fact]
    public async Task Phone_that_stops_reading_makes_the_queue_drop_and_ask_for_a_keyframe()
    {
        var (client, _, _, video) = await ConnectWithVideoAsync();
        using var _ = client;
        video.Output.OnConfig(new ConfigMessage(2400, 1080, VideoCodec.H264, 25_000, []));
        var maxPending = 0;

        // O celular não lê nada: os buffers do TLS e do TCP enchem e o escritor para de andar.
        for (var i = 0; i < 300 && video.KeyframeRequests == 0; i++)
        {
            video.Output.OnFrame(new FrameMessage((ulong)i + 1, i == 0, new byte[1024 * 1024]));
            maxPending = Math.Max(maxPending, video.Output.PendingFrames);
            await Task.Delay(1, _cts.Token);
        }

        Assert.True(video.KeyframeRequests >= 1, "a fila não pediu keyframe");
        Assert.True(maxPending <= 2, $"a fila chegou a {maxPending} quadros");
    }

    [Fact]
    public async Task Video_stops_before_the_monitor_is_released()
    {
        var events = new List<string>();
        _video.OnEvent = e =>
        {
            lock (events) events.Add(e);
        };
        _monitors.OnRelease = () =>
        {
            lock (events) events.Add("monitor liberado");
        };
        _monitors.Monitor = new VirtualMonitor(@"\\.\DISPLAY9", 3440, 0, 2400, 1080, 175);
        var (client, _, _, _) = await ConnectWithVideoAsync();

        client.Dispose();
        await WaitUntilAsync(() => _monitors.Released == 1);

        lock (events) Assert.Equal(["vídeo encerrado", "monitor liberado"], events);
    }

    [Fact]
    public async Task New_session_takes_over_the_video()
    {
        _video.Enabled = true;
        var (_, token) = _devices.Add("Pixel 8");
        var (wifi, wifiStream, _) = await ConnectWifiAsync();
        using var _ = wifi;
        await SendAsync(wifiStream, new AuthMessage(token));
        await SendAsync(wifiStream, Hello());
        await WaitUntilAsync(() => _video.Started.Count == 1);

        var (usb, _, _, _) = await ConnectWithVideoAsync();
        using var __ = usb;

        Assert.True(_video.Started[0].Disposed);
        Assert.False(_video.Started[1].Disposed);
    }

    private sealed class FakeMonitorManager : IVirtualMonitorManager
    {
        private int _acquired;
        private int _released;

        public VirtualMonitor? Monitor { get; set; }
        public List<(int Width, int Height, int Dpi)> Requests { get; } = [];
        public int Acquired => _acquired;
        public int Released => _released;
        public Action? OnRelease { get; set; }

        public VirtualMonitorLease Acquire(int width, int height, int densityDpi)
        {
            lock (Requests) Requests.Add((width, height, densityDpi));
            Interlocked.Increment(ref _acquired);
            return new VirtualMonitorLease(MonitorRequest.Normalize(width, height, densityDpi), Monitor, () =>
            {
                OnRelease?.Invoke();
                Interlocked.Increment(ref _released);
            });
        }
    }
}
