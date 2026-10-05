using System.Runtime.InteropServices;
using ScreenShare.Core.Protocol;
using ScreenShare.Core.Video;
using ScreenShare.Video.Pipeline;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Encoder de hardware (MFT assíncrono do Media Foundation, NVENC na placa NVIDIA), como validado no spike:
/// - entrada NV12 vinda do Nv12Converter, em amostras de um pool na GPU;
/// - baixa latência, PeakConstrainedVBR com pico de 2×, sem B-frames (o NVENC já não usa) e sem IDR periódico
///   (GOP no maior valor: IDR só no começo, quando pedido ou depois de descarte);
/// - uma thread própria lê os eventos do MFT: pedido de entrada (CanAccept) e saída pronta (Output, cópia dos bytes).
/// O timestamp viaja no SampleTime (µs × 10 = unidades de 100 ns) e volta na saída.
/// </summary>
internal sealed class MediaFoundationEncoder : IVideoEncoder
{
    /// <summary>GOP "infinito": o maior valor que o NVENC aceita sem IDR sozinho em 600 quadros (conferido na GPU).</summary>
    public const uint MaxGop = int.MaxValue;

    private const int StreamChange = unchecked((int)0xC00D6D61); // MF_E_TRANSFORM_STREAM_CHANGE
    private const int ProvidesSamples = 0x100; // MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
    private const int CanProvideSamples = 0x200; // MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES

    private readonly EncoderSettings _settings;
    private readonly IMFActivate _activate;
    private readonly IMFTransform _transform;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly CodecApi _api;
    private readonly IMFMediaType _inputType;
    private readonly IMFVideoSampleAllocatorEx _allocator;
    private readonly Nv12Converter _converter;
    private readonly IMFMediaEventGenerator _events;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly Thread _thread;
    private int _inputRequests;
    private volatile bool _stopping;

    public MediaFoundationEncoder(GpuContext gpu, IMFActivate activate, EncoderSettings settings)
    {
        _settings = settings;
        _activate = activate;
        _transform = activate.ActivateObject<IMFTransform>();
        _transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        _transform.Attributes.Set(CodecApi.LowLatency, 1u); // MF_LOW_LATENCY tem o mesmo GUID

        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(gpu.Device).CheckError();
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)_manager.NativePointer);

        _api = new CodecApi(_transform);
        _api.SetBool(CodecApi.LowLatency, true);
        _api.SetUInt32(CodecApi.GopSize, MaxGop);
        _api.SetUInt32(CodecApi.RateControl, CodecApi.PeakConstrainedVbr);
        var bitrate = (uint)settings.BitrateKbps * 1000;
        var peak = (uint)settings.PeakBitrateKbps * 1000;
        _api.SetUInt32(CodecApi.MeanBitRate, bitrate);
        _api.SetUInt32(CodecApi.MaxBitRate, peak);
        _api.SetUInt32(CodecApi.BufferSize, peak / (uint)settings.Fps * 3); // ~3 quadros no pico

        using (var outputType = OutputType(settings, bitrate)) _transform.SetOutputType(0, outputType, 0);
        _inputType = InputType(settings);
        _transform.SetInputType(0, _inputType, 0);
        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (ProvidesSamples | CanProvideSamples)) != 0;
        _outputSize = Math.Max(info.Size, settings.Width * settings.Height);

        _allocator = new IMFVideoSampleAllocatorEx(MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID));
        _allocator.SetDirectXManager(_manager);
        using (var attributes = MediaFactory.MFCreateAttributes(2))
        {
            attributes.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.RenderTarget | BindFlags.VideoEncoder));
            attributes.Set(TransformAttributeKeys.D3D11Usage, (uint)ResourceUsage.Default);
            _allocator.InitializeSampleAllocatorEx(3, 6, attributes, _inputType);
        }
        _converter = new Nv12Converter(gpu, settings.Width, settings.Height, settings.Fps);

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _thread = new Thread(RunEvents) { IsBackground = true, Name = "ScreenShare encoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public VideoCodec Codec => _settings.Codec;
    public int Width => _settings.Width;
    public int Height => _settings.Height;
    public bool CanAccept => Volatile.Read(ref _inputRequests) > 0;

    public event Action<EncodedFrame>? Output;
    public event Action<Exception>? Failed;

    public void Submit(IVideoImage image, ulong timestampUs, bool forceKeyframe)
    {
        if (Interlocked.Decrement(ref _inputRequests) < 0)
        {
            Interlocked.Increment(ref _inputRequests);
            throw new InvalidOperationException("o encoder não pediu entrada");
        }
        try
        {
            using var sample = _allocator.AllocateSample();
            _converter.Convert(((GpuImage)image).Texture, sample);
            sample.SampleTime = (long)timestampUs * 10;
            sample.SampleDuration = 10_000_000 / _settings.Fps;
            if (forceKeyframe) _api.SetUInt32(CodecApi.ForceKeyFrame, 1); // vale para o próximo quadro (o spike conferiu)
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException e) when (DxgiErrors.IsDeviceLost(e.HResult))
        {
            throw new DeviceLostException($"encoder: 0x{e.HResult:X8}", e); // o pipeline recria a placa, não só o encoder
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            using var shutdown = _transform.QueryInterface<IMFShutdown>();
            shutdown.Shutdown(); // destrava o GetEvent da thread de eventos
        }
        catch (Exception)
        {
            // o MFT já parou
        }
        _thread.Join(TimeSpan.FromSeconds(2));
        _events.Dispose();
        _converter.Dispose();
        _allocator.Dispose();
        _inputType.Dispose();
        _api.Dispose();
        _transform.Dispose();
        _activate.ShutdownObject();
        _manager.Dispose();
    }

    private void RunEvents()
    {
        try
        {
            while (!_stopping)
            {
                using var mediaEvent = _events.GetEvent(0);
                if (mediaEvent.EventType == MediaEventTypes.TransformNeedInput) Interlocked.Increment(ref _inputRequests);
                else if (mediaEvent.EventType == MediaEventTypes.TransformHaveOutput) DrainOutput();
                else if (mediaEvent.EventType == MediaEventTypes.Error) throw new SharpGenException(mediaEvent.Status);
            }
        }
        catch (Exception e) when (!_stopping)
        {
            // Placa removida ou reiniciada vira DeviceLostException: o pipeline recria tudo, não só o encoder.
            Failed?.Invoke(e is SharpGenException sharpGen && DxgiErrors.IsDeviceLost(sharpGen.HResult)
                ? new DeviceLostException($"encoder: 0x{sharpGen.HResult:X8}", e)
                : e);
        }
        catch (Exception)
        {
            // encerrando: o Shutdown faz o GetEvent falhar
        }
    }

    private void DrainOutput()
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? own = null;
        if (!_providesSamples)
        {
            own = MediaFactory.MFCreateSample();
            using var memory = MediaFactory.MFCreateMemoryBuffer(_outputSize);
            own.AddBuffer(memory);
            buffer.Sample = own;
        }
        try
        {
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            if (result.Code == StreamChange)
            {
                using var available = _transform.GetOutputAvailableType(0, 0);
                _transform.SetOutputType(0, available, 0);
                return;
            }
            result.CheckError();
            var data = CopyBytes(buffer.Sample!);
            var timestamp = (ulong)(buffer.Sample!.SampleTime / 10);
            Output?.Invoke(new EncodedFrame(timestamp, AnnexB.IsKeyframe(data, _settings.Codec), data));
        }
        finally
        {
            buffer.Events?.Dispose();
            if (buffer.Sample is { } sample && !ReferenceEquals(sample, own)) sample.Dispose();
            own?.Dispose();
        }
    }

    private static byte[] CopyBytes(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out _, out var length);
        try
        {
            var data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
            return data;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    private static IMFMediaType OutputType(EncoderSettings settings, uint bitrate)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, settings.Codec == VideoCodec.H265 ? VideoFormatGuids.Hevc : VideoFormatGuids.H264);
        SetVideoBasics(type, settings);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
        type.Set(MediaTypeAttributeKeys.Mpeg2Profile, settings.Codec == VideoCodec.H264 ? 100u : 1u); // High; Main
        type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u); // BT.709
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u); // 16–235
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u); // BT.709
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u); // BT.709
        return type;
    }

    private static IMFMediaType InputType(EncoderSettings settings)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        SetVideoBasics(type, settings);
        return type;
    }

    private static void SetVideoBasics(IMFMediaType type, EncoderSettings settings)
    {
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)settings.Width << 32) | (uint)settings.Height);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)settings.Fps << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressivo
    }
}
