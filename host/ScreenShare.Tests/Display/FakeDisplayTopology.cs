using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

/// <summary>Topologia em memória: uma saída do VDD que liga, desliga e troca de resolução como o Windows fez no spike.</summary>
internal sealed class FakeDisplayTopology : IDisplayTopology
{
    public bool Present { get; set; } = true;
    public string DeviceName { get; set; } = @"\\.\DISPLAY5";
    public bool Attached { get; set; }
    public DisplayPosition Position { get; set; }
    public (int Width, int Height) Size { get; set; } = (1920, 1080);
    public List<(int Width, int Height)> Modes { get; } = [(1920, 1080)];
    public int Scale { get; set; } = 100;
    public DisplayPosition Default { get; set; } = new(3440, 0);
    public Func<DisplayPosition, bool> AcceptPosition { get; set; } = _ => true;
    public bool FailScale { get; set; }
    public Exception? ThrowOnFind { get; set; }
    public Exception? ThrowOnGetScale { get; set; }
    public int AttachCalls { get; private set; }
    public int DetachCalls { get; private set; }
    public int SetModeCalls { get; private set; }
    public int SetScaleCalls { get; private set; }
    public List<DisplayPosition> AttachedAt { get; } = [];

    public VddOutput? FindVddOutput()
    {
        if (ThrowOnFind is { } error) throw error;
        if (!Present) return null;
        return Attached
            ? new VddOutput(DeviceName, true, Position.X, Position.Y, Size.Width, Size.Height, [.. Modes])
            : new VddOutput(DeviceName, false, 0, 0, 0, 0, [.. Modes]);
    }

    public bool Attach(string deviceName, DisplayPosition position, int width, int height)
    {
        AttachCalls++;
        AttachedAt.Add(position);
        if (deviceName != DeviceName || !AcceptPosition(position) || !Modes.Contains((width, height))) return false;
        Attached = true;
        Position = position;
        Size = (width, height);
        return true;
    }

    public bool Detach(string deviceName)
    {
        DetachCalls++;
        if (deviceName != DeviceName) return false;
        Attached = false;
        return true;
    }

    public bool SetMode(string deviceName, int width, int height)
    {
        SetModeCalls++;
        if (deviceName != DeviceName || !Attached || !Modes.Contains((width, height))) return false;
        Size = (width, height);
        return true;
    }

    public int? GetScale(string deviceName)
    {
        if (ThrowOnGetScale is { } error) throw error;
        return Attached ? Scale : null;
    }

    public bool SetScale(string deviceName, int percent)
    {
        SetScaleCalls++;
        if (FailScale || deviceName != DeviceName || !Attached) return false;
        Scale = Math.Min(percent, 175); // o máximo que o Windows informou no spike
        return true;
    }

    public DisplayPosition DefaultPosition() => Default;
}
