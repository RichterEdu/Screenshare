using ScreenShare.Display;

namespace ScreenShare.Tests.Display;

public sealed class DisplayTopologyIntegrationTests
{
    [VddFact]
    public void Attaches_changes_scale_and_detaches_the_real_vdd_output()
    {
        var topology = new DisplayTopology();
        var output = topology.FindVddOutput();
        Assert.NotNull(output);
        var mode = output.Modes[0];
        var wasAttached = output.Attached;
        try
        {
            if (output.Attached) Assert.True(topology.Detach(output.DeviceName));
            Assert.False(topology.FindVddOutput()!.Attached);

            Assert.True(topology.Attach(output.DeviceName, topology.DefaultPosition(), mode.Width, mode.Height));
            var attached = topology.FindVddOutput()!;
            Assert.True(attached.Attached);
            Assert.Equal(mode, (attached.Width, attached.Height));

            var scale = topology.GetScale(attached.DeviceName);
            Assert.NotNull(scale);
            Assert.True(topology.SetScale(attached.DeviceName, 125));
            Assert.Equal(125, topology.GetScale(attached.DeviceName));
            topology.SetScale(attached.DeviceName, scale.Value);
        }
        finally
        {
            if (!wasAttached && topology.FindVddOutput() is { Attached: true } now) topology.Detach(now.DeviceName);
        }
    }
}
