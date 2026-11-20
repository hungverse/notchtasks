using NotchTasks.Core.Devices;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests.Devices;

/// The reader's cache decision and the hot-plug case it guards: a container whose nodes were not yet enumerated must not stay "not an
/// accessory" once its nodes appear.
public class DeviceCacheTests
{
    private const string KeyboardClass = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

    /// Collects the toasts the module announces.
    private sealed class RecordingToasts : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    [Fact]
    public void NeedsClassify_NoKnownFact_True()
    {
        Assert.True(DeviceCache.NeedsClassify(null));
    }

    [Fact]
    public void NeedsClassify_KnownEmptyCategories_True()
    {
        // The hot-plug case: an empty classification is never trusted, so the nodes are read again.
        var known = new DeviceFact("{kb}", "USB Keyboard", Array.Empty<string>(), null);

        Assert.True(DeviceCache.NeedsClassify(known));
    }

    [Fact]
    public void NeedsClassify_KnownAccessory_False()
    {
        var known = new DeviceFact("{kb}", "USB Keyboard", new[] { "Input.Keyboard", "Bus.Usb" }, 80);

        Assert.False(DeviceCache.NeedsClassify(known));
    }

    [Fact]
    public void HotPlug_NodesAppearAfterTheContainer_ShownAndToastedConnected()
    {
        var announcer = new RecordingToasts();
        var module = new DevicesModule(announcer, () => null);
        var mouse = new DeviceFact("{mouse}", "USB Mouse", new[] { "Input.Mouse", "Bus.Usb" }, null);
        module.Update(new[] { mouse });  // the baseline

        // First read: the keyboard's container is listed, but its device nodes are not enumerated yet, so it is not an accessory.
        var noNodes = DeviceKinds.Categories("USB Keyboard", Array.Empty<(string InstanceId, string? ClassGuid)>());
        var first = new DeviceFact("{kb}", "USB Keyboard", noNodes, null);
        Assert.True(DeviceCache.NeedsClassify(first));
        module.Update(new[] { mouse, first }.Where(f => DeviceKinds.Shown(f.Categories)).ToList());
        Assert.Single(module.Devices);
        Assert.Empty(announcer.Toasts);

        // Second read: the container is classified again (the cache does not keep the empty kind), and its keyboard node is there.
        var nodes = new (string InstanceId, string? ClassGuid)[]
        {
            (@"USB\VID_046D&PID_C548&MI_00\6&874d577&0&0000", KeyboardClass),
        };
        var second = new DeviceFact("{kb}", "USB Keyboard", DeviceKinds.Categories("USB Keyboard", nodes), null);
        Assert.Equal(new[] { "Input.Keyboard", "Bus.Usb" }, second.Categories);

        module.Update(new[] { mouse, second });

        Assert.Equal(2, module.Devices.Count);
        var connected = Assert.Single(announcer.Toasts, t => t.Event == "deviceOn");
        Assert.Equal(new DeviceArt("keyboard", true), connected.Art);
        Assert.Equal("devices", connected.Module);
    }
}
