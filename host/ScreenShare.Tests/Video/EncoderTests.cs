using System.Collections.Concurrent;
using System.Diagnostics;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Video;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ScreenShare.Tests.Video;

public sealed class EncoderTests
{
    [GpuFact]
    public void H265_starts_with_an_idr_with_parameter_sets_honors_a_forced_idr_and_is_fast() =>
        AssertEncodes(VideoCodec.H265);

    [GpuFact]
    public void H264_starts_with_an_idr_with_parameter_sets_honors_a_forced_idr_and_is_fast() =>
        AssertEncodes(VideoCodec.H264);

    [GpuFact]
    public void No_idr_appears_on_its_own_in_600_frames()
    {
        var frames = Encode(VideoCodec.H265, count: 600, forceAt: -1);

        Assert.True(frames[0].Frame.IsKeyframe);
        Assert.DoesNotContain(frames.Skip(1), f => f.Frame.IsKeyframe);
    }

    [GpuFact]
    public async Task Default_source_captures_and_encodes_the_primary_monitor()
    {
        var source = VideoSourceFactory.CreateDefault(new VideoOptions(), _ => { });
        Assert.NotNull(source);
        var monitor = new PrimaryMonitorSource();
        var output = new FakeOutput();

        var stream = await source.StartAsync(new VideoRequest(monitor, VideoCodec.H264 | VideoCodec.H265, VideoLink.Usb),
            output, CancellationToken.None);
        Assert.NotNull(stream);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (output.Frames.Count == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "nenhum quadro em 3 s");
                await Task.Delay(20);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        var config = Assert.IsType<ConfigMessage>(output.Sent[0]);
        Assert.Equal(monitor.Current!.Width, config.Width);
        Assert.Equal(monitor.Current.Height, config.Height);
        Assert.NotEmpty(config.CodecConfig);
        Assert.True(output.Frames[0].IsKeyframe);
    }

    [GpuFact]
    public void Our_output_sample_returned_by_the_encoder_is_released_only_once()
    {
        var own = MediaFactory.MFCreateSample();
        var returned = new IMFSample(own.NativePointer); // como o Vortice devolve a saída: objeto novo, sem AddRef

        MediaFoundationEncoder.ReleaseOutputSample(returned, own);

        own.AddRef();
        Assert.Equal(1u, own.Release()); // a única referência continua com o own
        own.Dispose();
    }

    [GpuFact]
    public void Encoder_that_fails_to_configure_releases_everything_and_the_next_one_opens()
    {
        using var gpu = GpuContext.Create(null);
        var activate = EncoderCatalog.Find(VideoCodec.H264, gpu.VendorId, gpu.AdapterLuid);
        Assert.NotNull(activate);

        Assert.ThrowsAny<Exception>(() =>
            new MediaFoundationEncoder(gpu, activate, new EncoderSettings(VideoCodec.H264, 16384, 16384, 60, 25_000, 50_000)));
        using var encoder = new MediaFoundationEncoder(gpu, activate, new EncoderSettings(VideoCodec.H264, 1920, 1080, 60, 25_000, 50_000));

        var deadline = Stopwatch.StartNew();
        while (!encoder.CanAccept)
        {
            Assert.True(deadline.ElapsedMilliseconds < 1000, "o encoder novo não pediu entrada em 1 s");
            Thread.Sleep(1);
        }
    }

    private static void AssertEncodes(VideoCodec codec)
    {
        var frames = Encode(codec, count: 30, forceAt: 10);

        Assert.True(frames[0].Frame.IsKeyframe);
        Assert.NotNull(AnnexB.ExtractParameterSets(frames[0].Frame.Data, codec));
        Assert.True(frames[10].Frame.IsKeyframe); // o IDR forçado sai no mesmo quadro
        Assert.DoesNotContain(frames.Skip(1).Take(9), f => f.Frame.IsKeyframe);
        Assert.Equal(Enumerable.Range(1, 30).Select(i => (ulong)i * 16_667), frames.Select(f => f.Frame.TimestampUs));
        var sorted = frames.Select(f => f.Ms).Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Assert.True(p95 < 10, $"p95 da entrada à saída: {p95:0.0} ms");
    }

    /// <summary>Codifica quadros 2520×1080 de cor diferente, um por vez (o encoder devolve uma saída por entrada).</summary>
    private static List<(EncodedFrame Frame, double Ms)> Encode(VideoCodec codec, int count, int forceAt)
    {
        using var gpu = GpuContext.Create(null);
        var activate = EncoderCatalog.Find(codec, gpu.VendorId, gpu.AdapterLuid);
        Assert.NotNull(activate);
        using var encoder = new MediaFoundationEncoder(gpu, activate, new EncoderSettings(codec, 2520, 1080, 60, 25_000, 50_000));
        using var texture = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 2520, 1080, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource));
        using var target = gpu.Device.CreateRenderTargetView(texture);
        var image = new GpuImage(texture, 2520, 1080);
        var outputs = new BlockingCollection<(EncodedFrame Frame, long At)>();
        Exception? failure = null;
        encoder.Output += frame => outputs.Add((frame, Stopwatch.GetTimestamp()));
        encoder.Failed += error => failure = error;

        var results = new List<(EncodedFrame Frame, double Ms)>();
        for (var i = 0; i < count; i++)
        {
            var deadline = Stopwatch.StartNew();
            while (!encoder.CanAccept)
            {
                Assert.True(deadline.ElapsedMilliseconds < 1000, "o encoder não pediu entrada em 1 s");
                Thread.Sleep(0);
            }
            gpu.Context.ClearRenderTargetView(target, new Color4(i % 2 == 0 ? 0.9f : 0.1f, 0.5f, i % 7 / 7f, 1f));
            var submitted = Stopwatch.GetTimestamp();
            encoder.Submit(image, (ulong)(i + 1) * 16_667, forceKeyframe: i == 0 || i == forceAt);
            Assert.True(outputs.TryTake(out var output, TimeSpan.FromSeconds(1)), $"sem saída para o quadro {i}: {failure}");
            results.Add((output.Frame, Stopwatch.GetElapsedTime(submitted, output.At).TotalMilliseconds));
        }
        Assert.Null(failure);
        return results;
    }
}
