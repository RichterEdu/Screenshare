using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;
using ScreenShare.Core.Video;
using ScreenShare.Display;
using ScreenShare.Video;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor de desenvolvimento: liga o monitor virtual e o vídeo por sessão. As duas portas exigem TLS e depois PAIR/AUTH
/// antes do HELLO. A porta USB escuta só em loopback (o `adb reverse` chega por ali). Responde PING com PONG e manda o
/// próprio PING a cada segundo. Atende um cliente por vez em cada porta; um cliente mudo é derrubado por prazo
/// (handshake e ociosidade). O vídeo é de uma sessão por vez: a mais nova assume.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const uint StubBitrateKbps = 8000;

    /// <summary>Depois de um DENIED o PC espera o cliente terminar de falar por no máximo isto antes de fechar.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);

    private readonly TcpListener _wifi;
    private readonly TcpListener _usb;
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing;
    private readonly DeviceRegistry _devices;
    private readonly Action<string>? _log;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly IVirtualMonitorManager _monitors;
    private readonly IVideoSource? _video;
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _videoConfigTimeout;

    /// <param name="handshakeTimeout">Prazo para TLS + PAIR/AUTH em qualquer porta (padrão 10 s).</param>
    /// <param name="idleTimeout">
    /// Silêncio máximo numa sessão, em qualquer porta (padrão 10 s). O app manda PING a cada segundo,
    /// então 10 s sem nada é conexão morta (celular sem Wi-Fi, fora de alcance) e não pode prender a porta.
    /// </param>
    /// <param name="monitors">Monitor virtual por sessão (padrão: nenhum; o CONFIG leva a resolução pedida pelo celular, já normalizada).</param>
    /// <param name="video">Vídeo por sessão (padrão: nenhum; o celular recebe só o CONFIG de fallback).</param>
    /// <param name="pingInterval">Intervalo do PING do PC depois do CONFIG (padrão 1 s).</param>
    /// <param name="videoConfigTimeout">Sem CONFIG do vídeo nesse prazo, sai o de fallback (padrão 2 s).</param>
    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null,
        IVirtualMonitorManager? monitors = null, IVideoSource? video = null, TimeSpan? pingInterval = null,
        TimeSpan? videoConfigTimeout = null)
    {
        _wifi = new TcpListener(IPAddress.Any, wifiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        _identity = identity;
        _pairing = pairing;
        _devices = devices;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(10);
        _monitors = monitors ?? NullVirtualMonitorManager.Instance;
        _video = video is null ? null : new ExclusiveVideoSource(video);
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(1);
        _videoConfigTimeout = videoConfigTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Porta Wi-Fi (TLS) em que está escutando.</summary>
    public int WifiPort => ((IPEndPoint)_wifi.LocalEndpoint).Port;

    /// <summary>Endereço da porta USB: sempre 127.0.0.1.</summary>
    public IPEndPoint UsbEndPoint => (IPEndPoint)_usb.LocalEndpoint;

    public void Start()
    {
        _wifi.Start();
        _usb.Start();
    }

    /// <summary>Atende as duas portas até o token ser cancelado.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => Task.WhenAll(
        AcceptLoopAsync(_wifi, "Wi-Fi", VideoLink.Wifi, cancellationToken),
        AcceptLoopAsync(_usb, "USB", VideoLink.Usb, cancellationToken));

    private async Task AcceptLoopAsync(TcpListener listener, string portLabel, VideoLink link, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                try
                {
                    client.NoDelay = true;
                    // Buffer de envio fixo: o ajuste automático do Windows cresce até vários MB e esconde a rede travada
                    // (os quadros envelheceriam lá dentro). Com 512 KiB, a fila do SessionWriter percebe e descarta até um IDR.
                    client.Client.SendBufferSize = 512 * 1024;
                    _log?.Invoke($"Cliente conectado ({portLabel}): {client.Client.RemoteEndPoint}");
                    await ServeSecureAsync(client.GetStream(), link, cancellationToken);
                }
                catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    // Qualquer falha ao atender UM cliente (rede, TLS, protocolo, prazo estourado, disco ao gravar o
                    // registro...) encerra só essa conexão: a porta continua atendendo os próximos.
                    _log?.Invoke($"Conexão encerrada: {e.Message}");
                }
                _log?.Invoke("Cliente desconectado.");
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento normal
        }
    }

    private async Task ServeSecureAsync(NetworkStream network, VideoLink link, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        // TLS e PAIR/AUTH precisam terminar dentro do prazo: um cliente calado não pode prender a porta.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_handshakeTimeout);

        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = _identity.Certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, // spec: TLS 1.2 ou superior
            }, handshake.Token);
        var reader = new MessageReader(tls);
        var first = await reader.ReadAsync(handshake.Token);

        if (first is PairMessage pair)
        {
            if (!_pairing.TryConsume(pair.Secret))
            {
                await DenyAsync(tls, DeniedReason.InvalidPairingSecret, cancellationToken);
                return;
            }
            var (device, token) = _devices.Add(pair.DeviceName);
            _log?.Invoke($"Aparelho pareado: {Sanitize(device.Name)} (id {device.Id})");
            await SendAsync(tls, new PairedMessage(token), cancellationToken);
            first = await reader.ReadAsync(handshake.Token);
        }

        if (first is null) return; // cliente fechou
        if (first is not AuthMessage auth || _devices.Authenticate(auth.Token) is not { } known)
        {
            await DenyAsync(tls, DeniedReason.UnknownDevice, cancellationToken);
            return;
        }

        _log?.Invoke($"Autenticado: {Sanitize(known.Name)} (id {known.Id})");
        await ServeSessionAsync(tls, reader, link, cancellationToken);
    }

    /// <summary>
    /// HELLO → monitor → vídeo (ou CONFIG de fallback), PING do PC a cada intervalo e o laço de leitura, até o cliente
    /// sair, ficar mudo além do prazo ocioso ou a rede falhar. Daqui em diante só o SessionWriter escreve no stream.
    /// Encerramento: vídeo, escritor, monitor.
    /// </summary>
    private async Task ServeSessionAsync(Stream stream, MessageReader reader, VideoLink link, CancellationToken cancellationToken)
    {
        if (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is not HelloMessage hello)
            return; // primeira mensagem não é HELLO: fecha

        if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
        {
            await DenyAsync(stream, DeniedReason.IncompatibleVersion, cancellationToken);
            return;
        }

        // O monitor vale enquanto a sessão durar; o using o libera em qualquer saída (fim, erro ou prazo).
        using var lease = _monitors.Acquire(hello.Width, hello.Height, hello.DensityDpi);
        if (lease.Monitor is { } monitor)
            _log?.Invoke($"Monitor virtual: {monitor.DeviceName} {monitor.Width}×{monitor.Height} em ({monitor.X},{monitor.Y}), escala {monitor.ScalePercent}%.");
        var fallback = new ConfigMessage((ushort)lease.Width, (ushort)lease.Height, VideoCodec.H264, StubBitrateKbps, []);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var writer = new SessionWriter(stream, () => PcClock.NowUs);
        var writing = writer.RunAsync(session.Token);
        Task reading = Task.CompletedTask;
        IVideoStream? video = null;
        try
        {
            if (_video is not null)
                video = await _video.StartAsync(new VideoRequest(lease, hello.SupportedCodecs, link), new WriterOutput(writer), session.Token);
            if (video is null)
            {
                writer.SendFallbackConfig(fallback);
            }
            else
            {
                var started = video;
                writer.KeyframeNeeded += () => RequestKeyframe(started);
                _ = SendFallbackLaterAsync(writer, fallback, session.Token);
            }
            _ = PingAsync(writer, session.Token);

            reading = ReadLoopAsync(reader, writer, video, session.Token);
            // O que terminar primeiro encerra a sessão: o cliente (fim, prazo, protocolo) ou o escritor (rede).
            await await Task.WhenAny(reading, writing);
        }
        finally
        {
            if (video is not null) await video.DisposeAsync();
            await session.CancelAsync();
            await IgnoreErrorsAsync(writing);
            await IgnoreErrorsAsync(reading);
        }
    }

    /// <summary>PING → PONG, KEYFRAME_REQ → vídeo. Termina quando o cliente fecha; prazo estourado lança.</summary>
    private async Task ReadLoopAsync(MessageReader reader, SessionWriter writer, IVideoStream? video, CancellationToken cancellationToken)
    {
        while (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is { } message)
        {
            switch (message)
            {
                case PingMessage ping:
                    writer.Send(new PongMessage(ping.TimestampUs));
                    break;
                case KeyframeRequestMessage when video is not null:
                    RequestKeyframe(video);
                    break;
                // demais mensagens (PONG, TOUCH) são ignoradas: ainda não há toque
            }
        }
    }

    /// <summary>PING do PC a cada intervalo, depois do primeiro CONFIG: o celular acerta o relógio e mede a latência com ele.</summary>
    private async Task PingAsync(SessionWriter writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pingInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (writer.ConfigSent) writer.SendPing();
            }
        }
        catch (OperationCanceledException)
        {
            // fim da sessão
        }
    }

    /// <summary>O vídeo não mandou CONFIG a tempo (captura ainda abrindo, encoder travado): o celular sai da espera.</summary>
    private async Task SendFallbackLaterAsync(SessionWriter writer, ConfigMessage fallback, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_videoConfigTimeout, cancellationToken);
            if (writer.SendFallbackConfig(fallback))
                _log?.Invoke($"O vídeo não começou em {_videoConfigTimeout.TotalSeconds:0.#} s; o celular recebeu um CONFIG sem vídeo.");
        }
        catch (OperationCanceledException)
        {
            // fim da sessão
        }
    }

    /// <summary>Chamado pelo laço de leitura e pelo escritor (thread do encoder): nunca lança.</summary>
    private void RequestKeyframe(IVideoStream video)
    {
        try
        {
            video.RequestKeyframe();
        }
        catch (Exception e)
        {
            _log?.Invoke($"Falha ao pedir keyframe: {e.Message}");
        }
    }

    /// <summary>Espera uma tarefa da sessão que já foi cancelada; o erro dela já foi tratado (ou não importa mais).</summary>
    private static async Task IgnoreErrorsAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // cancelada ou já propagada pela sessão
        }
    }

    /// <summary>O vídeo escreve pelo escritor da sessão.</summary>
    private sealed class WriterOutput(SessionWriter writer) : IVideoOutput
    {
        public void OnConfig(ConfigMessage config) => writer.SendVideoConfig(config);
        public void OnFrame(FrameMessage frame) => writer.SendVideoFrame(frame);
        public int PendingFrames => writer.PendingFrames;
    }

    /// <summary>
    /// Lê uma mensagem com prazo próprio (recriado a cada leitura). Se estourar, lança OperationCanceledException
    /// com o token externo ainda ativo, que o laço de aceitação trata como conexão morta.
    /// </summary>
    private async Task<Message?> ReadWithIdleDeadlineAsync(MessageReader reader, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_idleTimeout);
        return await reader.ReadAsync(idle.Token);
    }

    private static Task SendAsync(Stream stream, Message message, CancellationToken cancellationToken) =>
        stream.WriteAsync(MessageCodec.Encode(message), cancellationToken).AsTask();

    /// <summary>
    /// Envia DENIED e fecha com educação. Fechar de imediato com entrada ainda pendente (ex.: o HELLO que vem logo
    /// depois do AUTH) faz o Windows mandar RST, e o cliente descarta o DENIED que ainda não leu. Por isso: encerra o
    /// nosso lado (close_notify no TLS, FIN no TCP puro) e descarta o que o cliente ainda mandar, até o fim da
    /// conexão ou por no máximo <see cref="DrainTimeout"/>.
    /// </summary>
    private static async Task DenyAsync(Stream stream, DeniedReason reason, CancellationToken cancellationToken)
    {
        await SendAsync(stream, new DeniedMessage(reason), cancellationToken);

        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainTimeout);
        try
        {
            if (stream is SslStream tls) await tls.ShutdownAsync();

            var buffer = new byte[256];
            while (await stream.ReadAsync(buffer, drain.Token) > 0)
            {
                // descarta
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
        {
            // o DENIED já foi enviado; o cliente sumir ou demorar a fechar não importa
        }
    }

    /// <summary>Remove caracteres de controle (ex.: sequências ESC) de um nome vindo do celular antes de ir para o console.</summary>
    public static string Sanitize(string text) => string.Concat(text.Where(c => !char.IsControl(c)));

    public void Dispose()
    {
        _wifi.Stop();
        _usb.Stop();
    }
}
