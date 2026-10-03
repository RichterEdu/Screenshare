using System.Net;
using System.Net.Sockets;
using ScreenShare.Core.Protocol;

namespace ScreenShare.DevHost;

/// <summary>
/// Servidor TCP de desenvolvimento: faz o handshake (HELLO → CONFIG) e responde PING com PONG, sem enviar vídeo.
/// Atende um cliente por vez. Serve para testar o app Android antes de o host real existir.
/// </summary>
public sealed class HostServer(int port, Action<string>? log = null) : IDisposable
{
    private const uint StubBitrateKbps = 8000;

    private readonly TcpListener _listener = new(IPAddress.Any, port);

    /// <summary>Porta em que está escutando (útil quando criado com porta 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start() => _listener.Start();

    /// <summary>Aceita clientes até o token ser cancelado.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                log?.Invoke($"Cliente conectado: {client.Client.RemoteEndPoint}");
                try
                {
                    await ServeAsync(client.GetStream(), cancellationToken);
                }
                catch (Exception e) when (e is IOException or ProtocolException)
                {
                    log?.Invoke($"Conexão encerrada: {e.Message}");
                }
                log?.Invoke("Cliente desconectado.");
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento normal
        }
    }

    private static async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new MessageReader(stream);

        if (await reader.ReadAsync(cancellationToken) is not HelloMessage hello
            || hello.ProtocolVersion != MessageCodec.ProtocolVersion)
            return; // primeira mensagem inválida ou versão incompatível: fecha a conexão

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

    public void Dispose() => _listener.Stop();
}
