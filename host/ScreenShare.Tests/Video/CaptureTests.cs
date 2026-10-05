using ScreenShare.Core.Video;
using ScreenShare.Video.Hardware;
using ScreenShare.Video.Pipeline;

namespace ScreenShare.Tests.Video;

public sealed class CaptureTests
{
    [Theory]
    [InlineData(unchecked((int)0x887A0027), "Timeout")]
    [InlineData(unchecked((int)0x887A0026), "CaptureLost")] // ACCESS_LOST: UAC, Win+L, troca de modo
    [InlineData(unchecked((int)0x80070005), "CaptureLost")] // E_ACCESSDENIED: área de trabalho segura
    [InlineData(unchecked((int)0x887A0022), "CaptureLost")] // NOT_CURRENTLY_AVAILABLE
    [InlineData(unchecked((int)0x887A0005), "DeviceLost")]  // DEVICE_REMOVED
    [InlineData(unchecked((int)0x887A0007), "DeviceLost")]  // DEVICE_RESET
    public void Dxgi_errors_are_classified(int hresult, string expected) =>
        Assert.Equal(expected, DxgiErrors.Classify(hresult).ToString());

    [Fact]
    public void Dxgi_errors_become_the_exceptions_the_pipeline_handles()
    {
        Assert.IsType<CaptureLostException>(DxgiErrors.ToException(DxgiErrors.AccessLost, "teste"));
        Assert.IsType<DeviceLostException>(DxgiErrors.ToException(DxgiErrors.DeviceRemoved, "teste"));
    }

    [GpuFact]
    public void Primary_monitor_is_captured_at_its_size_within_a_second()
    {
        using var thread = CaptureThread.Prepare();
        var monitor = new PrimaryMonitorSource().Current!;
        using var located = DxgiOutputLocator.Find(monitor.DeviceName)!;
        using var gpu = GpuContext.Create(located.Adapter);
        using var capture = new DesktopDuplicationCapture(gpu, located.Output);

        var deadline = DateTime.UtcNow.AddSeconds(1);
        var result = AcquireResult.Timeout;
        ulong presentUs = 0;
        while (result != AcquireResult.NewImage && DateTime.UtcNow < deadline)
            result = capture.TryAcquire(TimeSpan.FromMilliseconds(100), out presentUs);

        Assert.Equal(AcquireResult.NewImage, result);
        Assert.Equal((monitor.Width, monitor.Height), (capture.Width, capture.Height));
        Assert.InRange(presentUs, 1UL, PcClock.NowUs);
    }

    [GpuFact]
    public void Output_that_does_not_exist_is_not_found()
    {
        Assert.Null(DxgiOutputLocator.Find(@"\\.\DISPLAY999"));
    }
}
