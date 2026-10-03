using System.Net;
using System.Net.Sockets;
using ScreenShare.Core.Protocol;
using ScreenShare.DevHost;

namespace ScreenShare.Tests.DevHost;

public class HostServerTests : IAsyncLifetime
{
    private readonly HostServer _server = new(port: 0);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));
    private Task _serving = Task.CompletedTask;

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
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MessageReader Reader)> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.Port, _cts.Token);
        var stream = client.GetStream();
        return (client, stream, new MessageReader(stream));
    }

    private Task SendAsync(Stream stream, Message message) =>
        stream.WriteAsync(MessageCodec.Encode(message), _cts.Token).AsTask();

    [Fact]
    public async Task Hello_is_answered_with_config_using_the_phone_resolution()
    {
        var (client, stream, reader) = await ConnectAsync();
        using var _ = client;

        await SendAsync(stream, new HelloMessage(MessageCodec.ProtocolVersion, 2400, 1080, 420, VideoCodec.H264 | VideoCodec.H265));

        var config = Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
        Assert.Equal(2400, config.Width);
        Assert.Equal(1080, config.Height);
        Assert.Equal(VideoCodec.H264, config.Codec);
    }

    [Fact]
    public async Task Ping_is_answered_with_pong_carrying_the_same_value()
    {
        var (client, stream, reader) = await ConnectAsync();
        using var _ = client;
        await SendAsync(stream, new HelloMessage(MessageCodec.ProtocolVersion, 2400, 1080, 420, VideoCodec.H264));
        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));

        await SendAsync(stream, new PingMessage(123456789));

        Assert.Equal(new PongMessage(123456789), await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Hello_with_incompatible_version_closes_the_connection()
    {
        var (client, stream, reader) = await ConnectAsync();
        using var _ = client;

        await SendAsync(stream, new HelloMessage(1, 2400, 1080, 420, VideoCodec.H264));

        Assert.Null(await reader.ReadAsync(_cts.Token));
    }

    [Fact]
    public async Task Server_accepts_a_new_client_after_the_previous_one_disconnects()
    {
        var first = await ConnectAsync();
        await SendAsync(first.Stream, new HelloMessage(MessageCodec.ProtocolVersion, 2400, 1080, 420, VideoCodec.H264));
        Assert.IsType<ConfigMessage>(await first.Reader.ReadAsync(_cts.Token));
        first.Client.Dispose();

        var (client, stream, reader) = await ConnectAsync();
        using var _ = client;
        await SendAsync(stream, new HelloMessage(MessageCodec.ProtocolVersion, 1920, 1080, 320, VideoCodec.H264));

        Assert.IsType<ConfigMessage>(await reader.ReadAsync(_cts.Token));
    }
}
