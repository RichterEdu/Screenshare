using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Security;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor de desenvolvimento (sem vídeo). Porta Wi-Fi: TLS obrigatório, depois PAIR/AUTH antes do HELLO.
/// Porta USB: só em loopback (o `adb reverse` chega por ali), HELLO direto. Responde PING com PONG.
/// Atende um cliente por vez em cada porta.
/// </summary>
public sealed class HostServer : IDisposable
{
    private const uint StubBitrateKbps = 8000;

    private readonly TcpListener _wifi;
    private readonly TcpListener _usb;
    private readonly HostIdentity _identity;
    private readonly PairingSession _pairing;
    private readonly DeviceRegistry _devices;
    private readonly Action<string>? _log;
    private readonly TimeSpan _handshakeTimeout;

    public HostServer(int wifiPort, int usbPort, HostIdentity identity, PairingSession pairing, DeviceRegistry devices,
        Action<string>? log = null, TimeSpan? handshakeTimeout = null)
    {
        _wifi = new TcpListener(IPAddress.Any, wifiPort);
        _usb = new TcpListener(IPAddress.Loopback, usbPort);
        _identity = identity;
        _pairing = pairing;
        _devices = devices;
        _log = log;
        _handshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
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
        AcceptLoopAsync(_wifi, secure: true, cancellationToken),
        AcceptLoopAsync(_usb, secure: false, cancellationToken));

    private async Task AcceptLoopAsync(TcpListener listener, bool secure, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _log?.Invoke($"Cliente conectado ({(secure ? "Wi-Fi" : "USB")}): {client.Client.RemoteEndPoint}");
                try
                {
                    if (secure)
                        await ServeWifiAsync(client.GetStream(), cancellationToken);
                    else
                        await ServeSessionAsync(client.GetStream(), new MessageReader(client.GetStream()), cancellationToken);
                }
                catch (Exception e) when (e is IOException or AuthenticationException
                                          || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // IOException inclui ProtocolException; OperationCanceledException aqui é o timeout do handshake
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

    private async Task ServeWifiAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        await using var tls = new SslStream(network, leaveInnerStreamOpen: false);
        // TLS e PAIR/AUTH precisam terminar dentro do prazo: um cliente calado não pode prender a porta.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(_handshakeTimeout);

        await tls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = _identity.Certificate }, handshake.Token);
        var reader = new MessageReader(tls);
        var first = await reader.ReadAsync(handshake.Token);

        if (first is PairMessage pair)
        {
            if (!_pairing.TryConsume(pair.Secret))
            {
                await SendAsync(tls, new DeniedMessage(DeniedReason.InvalidPairingSecret), cancellationToken);
                return;
            }
            var (device, token) = _devices.Add(pair.DeviceName);
            _log?.Invoke($"Aparelho pareado: {device.Name} (id {device.Id})");
            await SendAsync(tls, new PairedMessage(token), cancellationToken);
            first = await reader.ReadAsync(handshake.Token);
        }

        if (first is null) return; // cliente fechou
        if (first is not AuthMessage auth || _devices.Authenticate(auth.Token) is not { } known)
        {
            await SendAsync(tls, new DeniedMessage(DeniedReason.UnknownDevice), cancellationToken);
            return;
        }

        _log?.Invoke($"Autenticado: {known.Name} (id {known.Id})");
        await ServeSessionAsync(tls, reader, cancellationToken);
    }

    /// <summary>HELLO → CONFIG, depois PING → PONG até o cliente sair.</summary>
    private static async Task ServeSessionAsync(Stream stream, MessageReader reader, CancellationToken cancellationToken)
    {
        if (await reader.ReadAsync(cancellationToken) is not HelloMessage hello)
            return; // primeira mensagem não é HELLO: fecha

        if (hello.ProtocolVersion != MessageCodec.ProtocolVersion)
        {
            await SendAsync(stream, new DeniedMessage(DeniedReason.IncompatibleVersion), cancellationToken);
            return;
        }

        await SendAsync(stream, new ConfigMessage(hello.Width, hello.Height, VideoCodec.H264, StubBitrateKbps, []), cancellationToken);

        while (await reader.ReadAsync(cancellationToken) is { } message)
        {
            if (message is PingMessage ping)
                await SendAsync(stream, new PongMessage(ping.TimestampUs), cancellationToken);
            // demais mensagens (TOUCH, KEYFRAME_REQ) são ignoradas: este stub não tem vídeo nem toque
        }
    }

    private static Task SendAsync(Stream stream, Message message, CancellationToken cancellationToken) =>
        stream.WriteAsync(MessageCodec.Encode(message), cancellationToken).AsTask();

    public void Dispose()
    {
        _wifi.Stop();
        _usb.Stop();
    }
}
