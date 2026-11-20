using NotchTasks.Core.Devices;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests.Devices;

/// DevicesModule ports the Mac's devicesFound: a baseline scan, then connect, disconnect and low battery toasts.
[Collection("L10n")]
public class DevicesModuleTests
{
    public DevicesModuleTests() => AsksFixtures.InitEnglish();

    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    private static DeviceFact Fact(string id, string name, int? battery = null, params string[] categories) =>
        new(id, name, categories.Length == 0 ? ["Audio.Headphone"] : categories, battery);

    private static DevicesModule Module(RecordingAnnouncer announcer, string? tint = null) =>
        new(announcer, () => tint);

    [Fact]
    public void DuplicateIds_Collapsed()
    {
        var module = Module(new RecordingAnnouncer());

        module.Update(
        [
            Fact("a", "AirPods Pro", 80),
            Fact("a", "Other name", 10),
            Fact("m", "Monitor", null, "Display.Monitor"),
        ]);

        var device = Assert.Single(module.Devices);
        Assert.Equal(new Device("a", "AirPods Pro", "airpodspro", "80%", 80), device);
    }

    [Fact]
    public void FirstScan_NoToasts()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer);

        var changed = module.Update([Fact("a", "AirPods", 40)]);

        Assert.True(changed);
        Assert.Single(module.Devices);
        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void Connected_ToastsWithBatteryOrReady()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer);
        module.Update([]);

        module.Update([Fact("a", "AirPods Pro", 72), Fact("k", "Keyboard", null, "Input.Keyboard")]);

        Assert.Equal(2, announcer.Calls.Count);
        var (pods, podsNotify) = announcer.Calls[0];
        Assert.False(podsNotify);
        Assert.Equal("airpodspro", pods.Glyph);
        Assert.Equal("#0A84FF", pods.Tint);
        Assert.Equal("AirPods Pro connected", pods.Title);
        Assert.Equal("72%", pods.Subtitle);
        Assert.Equal("deviceOn", pods.Event);
        Assert.Equal("devices", pods.Module);
        Assert.Equal(new DeviceArt("airpodspro", true), pods.Art);

        var (keys, _) = announcer.Calls[1];
        Assert.Equal("Keyboard connected", keys.Title);
        Assert.Equal("Ready", keys.Subtitle);
        Assert.Equal("keyboard", keys.Glyph);
    }

    [Fact]
    public void Connected_UsesDevicesTintWhenSet()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer, tint: "#123456");
        module.Update([]);

        module.Update([Fact("a", "AirPods", 50)]);

        Assert.Equal("#123456", Assert.Single(announcer.Calls).Toast.Tint);
    }

    [Fact]
    public void Disconnected_ToastsGrey()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer);
        module.Update([Fact("a", "AirPods", 50)]);

        var changed = module.Update([]);

        Assert.True(changed);
        Assert.Empty(module.Devices);
        var (t, notify) = Assert.Single(announcer.Calls);
        Assert.False(notify);
        Assert.Equal("AirPods disconnected", t.Title);
        Assert.Equal("#8E8E93", t.Tint);
        Assert.Equal("deviceOff", t.Event);
        Assert.Equal("devices", t.Module);
        Assert.Equal(new DeviceArt("airpods", false), t.Art);
    }

    [Fact]
    public void LowBattery_OnceUntilAbove30()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer);
        module.Update([Fact("a", "AirPods", 60)]);

        module.Update([Fact("a", "AirPods", 15)]);
        module.Update([Fact("a", "AirPods", 10)]);
        var low = announcer.Calls.Where(c => c.Toast.Event == "lowBattery").ToList();
        Assert.Single(low);

        var (t, notify) = low[0];
        Assert.False(notify);
        Assert.Equal("battery.25percent", t.Glyph);
        Assert.Equal("#FF453A", t.Tint);
        Assert.Equal("AirPods battery low", t.Title);
        Assert.Equal("15%", t.Subtitle);
        Assert.Equal("devices", t.Module);

        // Between 15 and 30 the warning stays disarmed: no second toast.
        module.Update([Fact("a", "AirPods", 25)]);
        module.Update([Fact("a", "AirPods", 10)]);
        Assert.Equal(1, announcer.Calls.Count(c => c.Toast.Event == "lowBattery"));

        // Above 30 it re-arms, so the next drop toasts again.
        module.Update([Fact("a", "AirPods", 31)]);
        module.Update([Fact("a", "AirPods", 14)]);
        Assert.Equal(2, announcer.Calls.Count(c => c.Toast.Event == "lowBattery"));
    }

    [Fact]
    public void Clear_ResetsBaseline()
    {
        var announcer = new RecordingAnnouncer();
        var module = Module(announcer);
        module.Update([Fact("a", "AirPods", 50)]);

        module.Clear();

        Assert.Empty(module.Devices);
        var changed = module.Update([Fact("b", "Keyboard", null, "Input.Keyboard")]);
        Assert.True(changed);
        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void Update_ReturnsFalseWhenUnchanged()
    {
        var module = Module(new RecordingAnnouncer());
        module.Update([Fact("a", "AirPods", 50)]);

        Assert.False(module.Update([Fact("a", "AirPods", 50)]));
    }
}
