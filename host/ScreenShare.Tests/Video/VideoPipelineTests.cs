using Microsoft.Extensions.Time.Testing;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Display;
using ScreenShare.Tests.Protocol;
using ScreenShare.Video;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class VideoPipelineTests : IDisposable
{
    private static readonly VirtualMonitor Display5 = new(@"\\.\DISPLAY5", 3440, 0, 2520, 1080, 175);
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private readonly FakeTimeProvider _time = new();
    private readonly long _t0;
    private readonly FakeBackend _backend = new();
    private readonly FakeMonitor _monitor = new(Display5);
    private readonly FakeOutput _output = new();
    private readonly List<string> _log = [];
    private VideoPipeline? _pipeline;
    private int _backendsCreated;

    public VideoPipelineTests()
    {
        _t0 = _time.GetTimestamp();
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now);
    }

    public void Dispose() => _pipeline?.Dispose();

    private TimeSpan Now => _time.GetElapsedTime(_t0);

    /// <summary>Relógio do PC falso: 1 s + o tempo do relógio falso, em µs.</summary>
    private ulong ClockUs() => 1_000_000UL + (ulong)(Now.Ticks / 10);

    private VideoPipeline Create(VideoCodec phone = VideoCodec.H264 | VideoCodec.H265, VideoOptions? options = null,
        CodecHealth? health = null, Func<ulong>? clock = null)
    {
        _pipeline = new VideoPipeline(() =>
        {
            _backendsCreated++;
            return _backend;
        }, _monitor, phone, options ?? new VideoOptions(), 25_000, health ?? new CodecHealth(), _output, _time,
            clock ?? ClockUs, _log.Add);
        return _pipeline;
    }

    /// <summary>Um Step por milissegundo: o relógio falso anda 1 ms depois de cada passo.</summary>
    private void Run(VideoPipeline pipeline, TimeSpan duration)
    {
        for (var t = TimeSpan.Zero; t < duration; t += Ms)
        {
            pipeline.Step();
            _time.Advance(Ms);
        }
    }

    [Fact]
    public void First_output_is_a_config_with_the_parameter_sets_then_the_idr()
    {
        var pipeline = Create();

        Run(pipeline, 5 * Ms);

        var config = Assert.IsType<ConfigMessage>(_output.Sent[0]);
        Assert.Equal(2520, config.Width);
        Assert.Equal(1080, config.Height);
        Assert.Equal(VideoCodec.H265, config.Codec);
        Assert.Equal(25_000u, config.BitrateKbps);
        Assert.Equal(AnnexB.ExtractParameterSets(Vectors.Load("annexb/h265-idr.hex"), VideoCodec.H265), config.CodecConfig);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[1]).IsKeyframe);
        Assert.True(_backend.Encoders[0].Submitted[0].Forced);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(30, 30)]
    public void Frames_follow_a_60hz_screen_up_to_the_fps_limit(int fps, int expected)
    {
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now)
        {
            Period = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60),
        };
        var pipeline = Create(options: new VideoOptions(Fps: fps));

        Run(pipeline, TimeSpan.FromSeconds(1));

        Assert.InRange(_output.Frames.Count, expected - 1, expected + 1);
    }

    [Fact]
    public void Nothing_is_captured_while_a_frame_waits_for_the_network_or_the_encoder_is_busy()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);
        var capture = _backend.Captures[0];
        capture.Change();
        var acquires = capture.Acquires;

        _output.PendingFrames = 1;
        Run(pipeline, 50 * Ms);
        Assert.Equal(acquires, capture.Acquires);

        _output.PendingFrames = 0;
        _backend.Encoders[0].CanAccept = false;
        Run(pipeline, 50 * Ms);
        Assert.Equal(acquires, capture.Acquires);

        _backend.Encoders[0].CanAccept = true;
        Run(pipeline, 5 * Ms);
        Assert.Equal(2, _output.Frames.Count); // o IDR do começo e a mudança, que esperou
    }

    [Fact]
    public void Still_screen_gets_refinements_at_100_300_and_700ms_and_then_nothing()
    {
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromSeconds(3));

        var submitted = _backend.Encoders[0].Submitted;
        Assert.Equal([1_000_000UL, 1_100_000UL, 1_300_000UL, 1_700_000UL], submitted.Select(s => s.Timestamp));
        Assert.Equal([true, false, false, false], submitted.Select(s => s.Forced));
    }

    [Fact]
    public void Keyframe_request_with_a_still_screen_reencodes_the_last_image_as_an_idr()
    {
        var pipeline = Create();
        Run(pipeline, TimeSpan.FromSeconds(1));
        var before = _output.Frames.Count;

        pipeline.RequestKeyframe();
        Run(pipeline, 5 * Ms);

        Assert.Equal(before + 1, _output.Frames.Count);
        Assert.True(_output.Frames[^1].IsKeyframe);
    }

    [Fact]
    public void Keyframes_are_200ms_apart_and_a_request_in_between_is_only_delayed()
    {
        var pipeline = Create();
        Run(pipeline, 50 * Ms); // IDR do começo em t = 0

        pipeline.RequestKeyframe();
        Run(pipeline, 100 * Ms);
        Assert.Single(_output.Frames, f => f.IsKeyframe);

        Run(pipeline, 100 * Ms);
        Assert.Equal(2, _output.Frames.Count(f => f.IsKeyframe));
    }

    [Fact]
    public void Lost_capture_is_reopened_after_100ms_and_keeps_the_encoder()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new CaptureLostException("ACCESS_LOST");
        Run(pipeline, 150 * Ms);

        Assert.Equal(2, _backend.Opened.Count);
        Assert.True(_backend.Captures[0].Disposed);
        Assert.Single(_backend.Encoders);
        Assert.Single(_output.Configs);
    }

    [Fact]
    public void Capture_with_a_new_size_starts_a_new_stream_with_config_and_idr()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.CaptureFactory = name => new FakeCapture(name, 1920, 1080, () => Now);
        _backend.Captures[0].ThrowOnAcquire = new CaptureLostException("ACCESS_LOST");
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(1920, _backend.Encoders[1].Width);
        Assert.Equal(1920, _output.Configs[1].Width);
        Assert.IsType<ConfigMessage>(_output.Sent[^2]);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[^1]).IsKeyframe);
    }

    [Fact]
    public void Monitor_change_reopens_the_capture_on_the_new_output()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _monitor.Change(Display5 with { DeviceName = @"\\.\DISPLAY6" });
        Run(pipeline, 5 * Ms);

        Assert.Equal([@"\\.\DISPLAY5", @"\\.\DISPLAY6"], _backend.Opened);
        Assert.True(_backend.Captures[0].Disposed);
    }

    [Fact]
    public void Unavailable_output_is_retried_with_growing_waits_and_refreshes_the_monitor()
    {
        for (var i = 0; i < 10; i++) _backend.OpenFailures.Enqueue(new CaptureLostException("E_ACCESSDENIED"));
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromMilliseconds(3050)); // tentativas em 0, 100, 300, 700 e 1500 ms (a próxima, em 3100)

        Assert.Equal(5, _backend.Opened.Count);
        Assert.Equal(5, _monitor.Refreshes);
        Assert.Single(_log, line => line.Contains("indisponível")); // a mesma mensagem não se repete
    }

    [Fact]
    public void H265_that_fails_to_open_is_dropped_for_the_run_and_h264_is_used()
    {
        _backend.FailingCodecs = VideoCodec.H265;
        var health = new CodecHealth();
        var pipeline = Create(health: health);

        Run(pipeline, 5 * Ms);

        Assert.Equal(VideoCodec.H264, _output.Configs[0].Codec);
        Assert.Equal(VideoCodec.H265, health.Failed);
    }

    [Fact]
    public void Without_a_common_codec_nothing_is_encoded()
    {
        _backend.HardwareCodecs = VideoCodec.H265;
        var pipeline = Create(phone: VideoCodec.H264);

        Run(pipeline, 500 * Ms);

        Assert.Empty(_output.Sent);
        Assert.Empty(_backend.Encoders);
    }

    [Theory]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264 | VideoCodec.H265, VideoCodec.None, VideoCodec.H265)]
    [InlineData(VideoCodec.H264, VideoCodec.H264 | VideoCodec.H265, VideoCodec.None, VideoCodec.H264)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264, VideoCodec.None, VideoCodec.H264)]
    [InlineData(VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264 | VideoCodec.H265, VideoCodec.H264, VideoCodec.H264)]
    [InlineData(VideoCodec.H265, VideoCodec.H264, VideoCodec.None, VideoCodec.None)]
    [InlineData(VideoCodec.H264, VideoCodec.H264 | VideoCodec.H265, VideoCodec.H265, VideoCodec.None)]
    public void Codec_choice(VideoCodec phone, VideoCodec host, VideoCodec forced, VideoCodec expected) =>
        Assert.Equal(expected, CodecChooser.Choose(phone, host, forced));

    [Fact]
    public void Timestamps_strictly_increase_even_with_a_stuck_clock()
    {
        _backend.CaptureFactory = name => new FakeCapture(name, 2520, 1080, () => Now) { Period = 17 * Ms };
        var pipeline = Create(clock: () => 5);

        Run(pipeline, 200 * Ms);

        var timestamps = _output.Frames.Select(f => f.TimestampUs).ToList();
        Assert.True(timestamps.Count > 5);
        Assert.True(timestamps.Zip(timestamps.Skip(1)).All(pair => pair.Second > pair.First));
    }

    [Fact]
    public void Stream_that_does_not_start_with_an_idr_waits_for_one_before_the_config()
    {
        _backend.EncoderSetup = encoder => encoder.FirstOutputIsP = true;
        var pipeline = Create();

        Run(pipeline, 20 * Ms);

        Assert.Equal(2, _output.Sent.Count);
        Assert.IsType<ConfigMessage>(_output.Sent[0]);
        Assert.True(Assert.IsType<FrameMessage>(_output.Sent[1]).IsKeyframe);
        Assert.Equal(2, _backend.Encoders[0].Submitted.Count);
    }

    [Fact]
    public void Encoder_failure_recreates_it_as_a_new_stream()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].Fail(new InvalidOperationException("falhou"));
        Run(pipeline, 150 * Ms); // recria depois da espera de 100 ms

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(2, _output.Configs.Count);
        Assert.True(_output.Frames[^1].IsKeyframe);
    }

    [Fact]
    public void Output_from_an_old_encoder_is_ignored()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);
        var old = _backend.Encoders[0];
        old.Fail(new InvalidOperationException("falhou"));
        Run(pipeline, 150 * Ms);
        var count = _output.Sent.Count;

        old.Emit(new EncodedFrame(99, true, FakeEncoder.Idr(VideoCodec.H265)));

        Assert.Equal(count, _output.Sent.Count);
    }

    [Fact]
    public void Lost_device_recreates_the_whole_backend()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new DeviceLostException("DEVICE_REMOVED");
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Disposed);
        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Encoder_that_rejects_every_frame_backs_off_and_its_codec_gives_way()
    {
        _backend.EncoderSetup = encoder => encoder.ThrowOnSubmit = encoder.Codec == VideoCodec.H265;
        var health = new CodecHealth();
        var pipeline = Create(health: health);

        Run(pipeline, TimeSpan.FromSeconds(2));

        Assert.Equal(VideoPipeline.MaxFailuresWithoutOutput, _backend.Encoders.Count(e => e.Codec == VideoCodec.H265));
        Assert.Equal(VideoCodec.H265, health.Failed);
        Assert.Equal(VideoCodec.H264, Assert.Single(_output.Configs).Codec);
        Assert.True(_log.Count < 10, $"{_log.Count} linhas de log");
    }

    [Fact]
    public void Codec_that_worked_and_then_fails_to_reopen_recreates_everything_and_is_kept()
    {
        var health = new CodecHealth();
        var pipeline = Create(health: health);
        Run(pipeline, 50 * Ms);

        // A placa reiniciou: o encoder avisa erro e nenhum encoder abre enquanto ela volta.
        _backend.FailingCodecs = VideoCodec.H264 | VideoCodec.H265;
        _backend.Encoders[0].Fail(new InvalidOperationException("erro do encoder"));
        Run(pipeline, 500 * Ms);
        _backend.FailingCodecs = VideoCodec.None;
        Run(pipeline, TimeSpan.FromSeconds(3));

        Assert.Equal(VideoCodec.None, health.Failed);
        Assert.True(_backendsCreated >= 2, "o backend não foi recriado");
        Assert.Equal(2, _output.Configs.Count);
        Assert.Equal(VideoCodec.H265, _output.Configs[1].Codec);
    }

    [Fact]
    public void Encoder_reporting_a_lost_device_recreates_the_backend()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].Fail(new DeviceLostException("DEVICE_REMOVED"));
        Run(pipeline, 150 * Ms);

        Assert.True(_backend.Disposed);
        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Requested_idr_that_comes_out_as_a_p_frame_is_requested_again()
    {
        var pipeline = Create();
        Run(pipeline, 300 * Ms);
        _backend.Encoders[0].IgnoreForce = 1;

        pipeline.RequestKeyframe();
        Run(pipeline, 500 * Ms);

        Assert.Equal(2, _output.Frames.Count(f => f.IsKeyframe)); // o do começo e o pedido de novo
    }

    [Fact]
    public void Encoder_that_stops_asking_for_input_is_recreated_after_a_second()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Encoders[0].CanAccept = false;
        Run(pipeline, TimeSpan.FromMilliseconds(1200));

        Assert.True(_backend.Encoders[0].Disposed);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Unexpected_error_restarts_the_video_instead_of_stopping_it()
    {
        var pipeline = Create();
        Run(pipeline, 5 * Ms);

        _backend.Captures[0].ThrowOnAcquire = new InvalidOperationException("erro inesperado do driver");
        Run(pipeline, 150 * Ms);

        Assert.Equal(2, _backendsCreated);
        Assert.Equal(2, _output.Configs.Count);
    }

    [Fact]
    public void Monitor_change_does_not_wait_for_a_pending_backoff()
    {
        for (var i = 0; i < 3; i++) _backend.OpenFailures.Enqueue(new CaptureLostException("E_ACCESSDENIED"));
        var pipeline = Create();
        Run(pipeline, 320 * Ms); // tentativas em 0, 100 e 300 ms; a próxima seria em 700
        Assert.Equal(3, _backend.Opened.Count);

        _monitor.Change(Display5 with { DeviceName = @"\\.\DISPLAY6" });
        Run(pipeline, 5 * Ms);

        Assert.Equal(4, _backend.Opened.Count);
    }

    [Fact]
    public void Stats_count_frames_bytes_and_keyframes()
    {
        var pipeline = Create();

        Run(pipeline, TimeSpan.FromSeconds(1));

        var stats = pipeline.Stats;
        Assert.Equal((2520, 1080, VideoCodec.H265), (stats.Width, stats.Height, stats.Codec));
        Assert.Equal(4, stats.Frames);
        Assert.Equal(1, stats.Keyframes);
        Assert.Equal(_output.Frames.Sum(f => (long)f.Data.Length), stats.Bytes);
    }
}
