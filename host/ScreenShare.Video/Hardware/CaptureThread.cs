using System.Runtime.InteropServices;

namespace ScreenShare.Video.Hardware;

/// <summary>
/// Preparo da thread de captura. DPI por monitor: o DuplicateOutput1 exige, e fica só nesta thread para não mudar o que
/// o resto do host lê das telas. Timer do Windows de 1 ms: sem ele, as esperas curtas do pipeline viram 15 ms.
/// Dispose desfaz os dois (na mesma thread).
/// </summary>
internal static class CaptureThread
{
    private const nint PerMonitorAwareV2 = -4;

    public static IDisposable Prepare()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        timeBeginPeriod(1);
        return new Restore(previous);
    }

    private sealed class Restore(nint previous) : IDisposable
    {
        public void Dispose()
        {
            timeEndPeriod(1);
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
