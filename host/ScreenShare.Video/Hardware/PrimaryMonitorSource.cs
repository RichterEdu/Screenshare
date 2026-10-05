using ScreenShare.Display;

namespace ScreenShare.Video.Hardware;

/// <summary>Captura o monitor principal em vez do virtual (opção --capturar principal, para depurar sem o driver).</summary>
public sealed class PrimaryMonitorSource : IMonitorSource
{
    public PrimaryMonitorSource() => Refresh();

    public VirtualMonitor? Current { get; private set; }

    /// <summary>O monitor principal não muda de nome durante a sessão: nunca dispara.</summary>
    public event Action? Changed
    {
        add { }
        remove { }
    }

    public VirtualMonitor? Refresh()
    {
        using var located = DxgiOutputLocator.FindPrimary();
        if (located is null) return Current = null;
        var description = located.Output.Description;
        var area = description.DesktopCoordinates;
        return Current = new VirtualMonitor(description.DeviceName, area.Left, area.Top, area.Right - area.Left,
            area.Bottom - area.Top, 100);
    }
}
