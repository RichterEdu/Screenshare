using ScreenShare.Display.Driver;
using ScreenShare.Display.Native;

namespace ScreenShare.Display;

/// <summary>A topologia de tela real do Windows (veja <see cref="IDisplayTopology"/>). Validada no spike da Parte 2.</summary>
public sealed class DisplayTopology : IDisplayTopology
{
    /// <summary>Os degraus de escala do Windows, na ordem em que os passos relativos contam.</summary>
    private static readonly int[] ScaleSteps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public VddOutput? FindVddOutput()
    {
        var adapter = DisplayApi.Adapters().FirstOrDefault(IsVdd);
        if (adapter is null) return null;
        var modes = DisplayApi.Modes(adapter.DeviceName);
        if ((adapter.StateFlags & DisplayApi.AttachedToDesktop) != 0 && DisplayApi.CurrentMode(adapter.DeviceName) is { } current)
            return new VddOutput(adapter.DeviceName, true, current.X, current.Y, current.Width, current.Height, modes);
        return new VddOutput(adapter.DeviceName, false, 0, 0, 0, 0, modes);
    }

    public bool Attach(string deviceName, DisplayPosition position, int width, int height) =>
        DisplayApi.SetPlacement(deviceName, position.X, position.Y, width, height);

    public bool Detach(string deviceName) => DisplayApi.SetPlacement(deviceName, 0, 0, 0, 0);

    public bool SetMode(string deviceName, int width, int height) => DisplayApi.SetSize(deviceName, width, height);

    public int? GetScale(string deviceName) =>
        DisplayApi.GetScaleSteps(deviceName) is { } steps ? ToPercent(steps.Min, steps.Current) : null;

    public bool SetScale(string deviceName, int percent) =>
        DisplayApi.GetScaleSteps(deviceName) is { } steps &&
        DisplayApi.SetScaleStep(deviceName, ToRelativeStep(steps.Min, steps.Max, percent));

    public DisplayPosition DefaultPosition()
    {
        var right = 0;
        foreach (var adapter in DisplayApi.Adapters())
        {
            if ((adapter.StateFlags & DisplayApi.AttachedToDesktop) == 0 || IsVdd(adapter)) continue;
            if (DisplayApi.CurrentMode(adapter.DeviceName) is { } mode) right = Math.Max(right, mode.X + mode.Width);
        }
        return new DisplayPosition(right, 0);
    }

    /// <summary>Passo relativo para pedir <paramref name="percent"/>: o degrau mais alto que não passa do pedido, limitado ao que o Windows aceita.</summary>
    public static int ToRelativeStep(int minRelative, int maxRelative, int percent)
    {
        var index = Math.Max(0, Array.FindLastIndex(ScaleSteps, step => step <= percent));
        return Math.Clamp(index - Math.Abs(minRelative), minRelative, maxRelative);
    }

    public static int ToPercent(int minRelative, int currentRelative) =>
        ScaleSteps[Math.Clamp(Math.Abs(minRelative) + currentRelative, 0, ScaleSteps.Length - 1)];

    private static bool IsVdd(DisplayApi.Adapter adapter) =>
        adapter.DeviceId.Equals(VddPackage.HardwareId, StringComparison.OrdinalIgnoreCase);
}
