using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor de desenvolvimento (sem vídeo). As duas portas exigem TLS e depois PAIR/AUTH antes do HELLO.
/// A porta USB escuta só em loopback (o `adb reverse` chega por ali). Responde PING com PONG.
/// Atende um cliente por vez em cada porta; um cliente mudo é derrubado por prazo (handshake e ociosidade).
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

    /// <param name="handshakeTimeout">Prazo para TLS + PAIR/AUTH em qualquer porta (padrão 10 s).</param>
    /// <param name="idleTimeout">
    /// Silêncio máximo numa sessão, em qualquer porta (padrão 10 s). O app manda PING a cada segundo,
    /// então 10 s sem nada é conexão morta (celular sem Wi-Fi, fora de alcance) e não pode prender a porta.
    /// </param>
    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null, TimeSpan? idleTimeout = null)
    {
        _wifi = new TcpListener(IPAddress.Any, wifiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        _identity = identity;
        _pairing = pairing;
        _devices = devices;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(10);
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
        AcceptLoopAsync(_wifi, "Wi-Fi", cancellationToken),
        AcceptLoopAsync(_usb, "USB", cancellationToken));

    private async Task AcceptLoopAsync(TcpListener listener, string portLabel, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                try
                {
                    client.NoDelay = true;
                    _log?.Invoke($"Cliente conectado ({portLabel}): {client.Client.RemoteEndPoint}");
                    await ServeSecureAsync(client.GetStream(), cancellationToken);
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

    private async Task ServeSecureAsync(NetworkStream network, CancellationToken cancellationToken)
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
        await ServeSessionAsync(tls, reader, cancellationToken);
    }

    /// <summary>HELLO → CONFIG, depois PING → PONG até o cliente sair ou ficar mudo além do prazo ocioso.</summary>
    private async Task ServeSessionAsync(Stream stream, MessageReader reader, CancellationToken cancellationToken)
    {
        if (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is not HelloMessage hello)
            return; // primeira mensagem não é HELLO: fecha

        if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
        {
            await DenyAsync(stream, DeniedReason.IncompatibleVersion, cancellationToken);
            return;
        }

        await SendAsync(stream, new ConfigMessage(hello.Width, hello.Height, VideoCodec.H264, StubBitrateKbps, []), cancellationToken);

        while (await ReadWithIdleDeadlineAsync(reader, cancellationToken) is { } message)
        {
            if (message is PingMessage ping)
                await SendAsync(stream, new PongMessage(ping.TimestampUs), cancellationToken);
            // demais mensagens (TOUCH, KEYFRAME_REQ) são ignoradas: este stub não tem vídeo nem toque
        }
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
            switch (stream)
            {
                case SslStream tls:
                    await tls.ShutdownAsync();
                    break;
                case NetworkStream network:
                    network.Socket.Shutdown(SocketShutdown.Send);
                    break;
            }

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
