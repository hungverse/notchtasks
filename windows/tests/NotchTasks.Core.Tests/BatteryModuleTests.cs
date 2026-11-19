using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The battery module ports the Mac's updateBattery (Model.swift): a plug change after the first reading, the low warning
/// once per discharge, and the power status flags read by PowerFacts.
public class BatteryModuleTests
{
    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    private static PowerFacts Unplugged(int percent) => new(true, percent, false, false);

    private static PowerFacts Plugged(int percent) => new(true, percent, true, true);

    [Fact]
    public void NoBattery_StateNull_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);

        var changed = module.Update(new PowerFacts(false, 0, false, false));

        Assert.False(changed);
        Assert.Null(module.State);
        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void FirstReading_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);

        var changed = module.Update(Plugged(60));

        Assert.True(changed);
        Assert.Equal(new BatteryState(60, true, true), module.State);
        Assert.Empty(announcer.Calls);
    }

    [Fact]
    public void PlugChange_AnnouncesCompactWithChargeArt()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);
        module.Update(Unplugged(60));

        module.Update(Plugged(61));

        var (charging, notify) = Assert.Single(announcer.Calls);
        Assert.False(notify);
        Assert.Equal("bolt", charging.Glyph);
        Assert.Equal("#30D158", charging.Tint);
        Assert.Equal("Charging", charging.Title);
        Assert.Equal("61%", charging.Subtitle);
        Assert.Equal("charging", charging.Event);
        Assert.True(charging.Compact);
        Assert.Equal(new ChargeArt(61, true), charging.Art);
        Assert.Equal("battery", charging.Module);

        module.Update(Unplugged(61));

        Assert.Equal(2, announcer.Calls.Count);
        var (unplugged, unpluggedNotify) = announcer.Calls[1];
        Assert.False(unpluggedNotify);
        Assert.Equal("plug", unplugged.Glyph);
        Assert.Equal("#8E8E93", unplugged.Tint);
        Assert.Equal("Unplugged", unplugged.Title);
        Assert.Equal("61%", unplugged.Subtitle);
        Assert.Equal("unplugged", unplugged.Event);
        Assert.True(unplugged.Compact);
        Assert.Equal(new ChargeArt(61, false), unplugged.Art);
        Assert.Equal("battery", unplugged.Module);
    }

    [Fact]
    public void Low_AtOrBelow20_Unplugged_AnnouncesOnce()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);
        module.Update(Unplugged(50));

        module.Update(Unplugged(20));

        var (low, notify) = Assert.Single(announcer.Calls);
        Assert.True(notify);
        Assert.Equal("batterylow", low.Glyph);
        Assert.Equal("#FF453A", low.Tint);
        Assert.Equal("Low battery", low.Title);
        Assert.Equal("20% left", low.Subtitle);
        Assert.Equal("lowBattery", low.Event);
        Assert.False(low.Compact);
        Assert.Equal("battery", low.Module);

        module.Update(Unplugged(18));

        Assert.Single(announcer.Calls);
    }

    [Fact]
    public void Plugging_ResetsLowWarning()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);
        module.Update(Unplugged(15));
        Assert.Single(announcer.Calls);  // the low warning on the first reading

        module.Update(Plugged(15));
        Assert.Equal(2, announcer.Calls.Count);  // Charging

        module.Update(Unplugged(15));

        // Unplugged again: the plug toast, then the low warning once more.
        Assert.Equal(4, announcer.Calls.Count);
        Assert.Equal("Unplugged", announcer.Calls[2].Toast.Title);
        Assert.Equal("Low battery", announcer.Calls[3].Toast.Title);
    }

    [Fact]
    public void PlugFlapping_OneToastPerRealChange()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);
        module.Update(Unplugged(80));

        // Percent moves while the plug stays put: no toast.
        module.Update(Unplugged(79));
        module.Update(Unplugged(78));
        Assert.Empty(announcer.Calls);

        // Each real plug change makes exactly one toast.
        module.Update(Plugged(78));
        module.Update(Plugged(77));
        module.Update(Unplugged(77));
        module.Update(Unplugged(76));
        module.Update(Plugged(76));

        Assert.Equal(3, announcer.Calls.Count);
        Assert.Equal(new[] { "Charging", "Unplugged", "Charging" }, announcer.Calls.Select(c => c.Toast.Title));
    }

    [Fact]
    public void Update_ReturnsTrueOnlyWhenStateChanged()
    {
        var module = new BatteryModule(new RecordingAnnouncer());

        Assert.True(module.Update(Unplugged(70)));
        Assert.False(module.Update(Unplugged(70)));
        Assert.True(module.Update(Unplugged(69)));
        Assert.True(module.Update(new PowerFacts(false, 0, false, false)));
        Assert.False(module.Update(new PowerFacts(false, 0, false, false)));
    }

    [Fact]
    public void UnknownPercent_KeepsTheState_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new BatteryModule(announcer);
        module.Update(Unplugged(40));

        var changed = module.Update(new PowerFacts(true, -1, false, true));

        Assert.False(changed);
        Assert.Equal(new BatteryState(40, false, false), module.State);
        Assert.Empty(announcer.Calls);
    }
}

public class PowerFactsTests
{
    [Fact]
    public void FromStatus_NoBatteryFlag_MeansNoBattery()
    {
        var facts = PowerFacts.FromStatus(1, 128, 255);

        Assert.False(facts.HasBattery);
    }

    [Fact]
    public void FromStatus_UnknownPercent_IsMinusOne()
    {
        var facts = PowerFacts.FromStatus(0, 1, 255);

        Assert.True(facts.HasBattery);
        Assert.Equal(-1, facts.Percent);
    }

    [Fact]
    public void FromStatus_AcLineOne_IsPlugged()
    {
        Assert.True(PowerFacts.FromStatus(1, 1, 50).Plugged);
        Assert.False(PowerFacts.FromStatus(0, 1, 50).Plugged);
        Assert.False(PowerFacts.FromStatus(255, 1, 50).Plugged);
    }

    [Fact]
    public void FromStatus_FlagBitEight_IsCharging()
    {
        Assert.True(PowerFacts.FromStatus(1, 8, 50).Charging);
        Assert.True(PowerFacts.FromStatus(1, 9, 50).Charging);
        Assert.False(PowerFacts.FromStatus(1, 1, 50).Charging);
        Assert.False(PowerFacts.FromStatus(1, 2, 50).Charging);
    }

    [Fact]
    public void FromStatus_Percent_IsTheLifePercent()
    {
        var facts = PowerFacts.FromStatus(0, 2, 37);

        Assert.Equal(new PowerFacts(true, 37, false, false), facts);
    }
}
