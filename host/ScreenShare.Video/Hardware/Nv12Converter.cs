using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// BGRA (faixa cheia) → NV12 BT.709 (faixa limitada), na GPU, com o ID3D11VideoProcessor. O processamento automático
/// fica desligado para não mexer no texto. As views das texturas do pool do encoder são guardadas e soltas no Dispose.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly Dictionary<(nint Texture, uint Slice), (ID3D11Texture2D Texture, ID3D11VideoProcessorOutputView View)> _outputs = [];
    private ID3D11Texture2D? _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;

    public Nv12Converter(GpuContext gpu, int width, int height, int fps)
    {
        _videoDevice = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            InputFrameRate = new Rational((uint)fps, 1),
            OutputFrameRate = new Rational((uint)fps, 1),
            Usage = VideoUsage.OptimalSpeed,
        });
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        using var videoContext1 = gpu.Context.QueryInterface<ID3D11VideoContext1>();
        videoContext1.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        videoContext1.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP709);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    /// <summary>Converte a imagem para a amostra NV12 (uma textura do pool do encoder).</summary>
    public void Convert(ID3D11Texture2D source, IMFSample sample)
    {
        if (!ReferenceEquals(source, _inputTexture))
        {
            _inputView?.Dispose();
            _inputView = _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            });
            _inputTexture = source;
        }

        using var buffer = sample.GetBufferByIndex(0);
        using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        var pointer = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID); // já vem com uma referência nossa
        var slice = dxgiBuffer.SubresourceIndex;
        if (_outputs.TryGetValue((pointer, slice), out var output))
        {
            System.Runtime.InteropServices.Marshal.Release(pointer);
        }
        else
        {
            var texture = new ID3D11Texture2D(pointer);
            var view = _videoDevice.CreateVideoProcessorOutputView(texture, _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayVideoProcessorOutputView { MipSlice = 0, FirstArraySlice = slice, ArraySize = 1 },
            });
            output = (texture, view);
            _outputs[(pointer, slice)] = output;
        }

        _videoContext.VideoProcessorBlt(_processor, output.View, 0, 1,
            [new VideoProcessorStream { Enable = true, InputSurface = _inputView }]).CheckError();
    }

    public void Dispose()
    {
        foreach (var (texture, view) in _outputs.Values)
        {
            view.Dispose();
            texture.Dispose();
        }
        _outputs.Clear();
        _inputView?.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
