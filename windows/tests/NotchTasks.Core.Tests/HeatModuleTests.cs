using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The heat toast ports the Mac's thermalChanged (Model.swift). The hottest reading decides: serious above 85 C, critical above
/// 95 C. A level fires once per crossing and re-arms only after the reading falls 5 C below its threshold. Critical supersedes
/// serious. Pure: the tests feed the readings the source would read from the helper.
[Collection("L10n")]
public sealed class HeatModuleTests
{
    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    private static ThermalReading Hottest(double celsius) => new("CPU", celsius);

    [Fact]
    public void Serious_FiresAbove85_NotAt85()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(85), "Gradle");
        Assert.Empty(announcer.Calls);

        module.Judge(Hottest(85.5), "Gradle");
        Assert.Single(announcer.Calls);
    }

    [Fact]
    public void Serious_Toast_IsTheMacsWarmToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(90), "Gradle");

        var (toast, notify) = announcer.Calls.Single();
        Assert.True(notify);
        Assert.Equal("thermometer", toast.Glyph);
        Assert.Equal("#FF9F0A", toast.Tint);
        Assert.Equal("Computer is warm", toast.Title);
        Assert.Equal("Gradle is working hard", toast.Subtitle);
        Assert.Equal("warning", toast.Event);
        Assert.Equal("taskmgr", toast.Url);
        Assert.Equal("See apps", toast.Action);
        Assert.True(toast.ToPhone);
        Assert.Equal("health", toast.Module);
    }

    [Fact]
    public void Subtitle_WithoutAWorkingTool_AsksToCloseHeavyApps()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(90), null);

        Assert.Equal("Consider closing heavy apps", announcer.Calls.Single().Toast.Subtitle);
    }

    [Fact]
    public void Critical_FiresAbove95_Toast_IsTheVeryHotToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(95), "Gradle");       // exactly 95: serious only (critical is above 95)
        Assert.Equal("Computer is warm", announcer.Calls.Single().Toast.Title);

        module.Judge(Hottest(95.5), null);
        Assert.Equal(2, announcer.Calls.Count);
        var toast = announcer.Calls[1].Toast;
        Assert.Equal("Computer is very hot", toast.Title);
        Assert.Equal("#FF453A", toast.Tint);
        Assert.Equal("Consider closing heavy apps", toast.Subtitle);
        Assert.True(toast.ToPhone);
    }

    [Fact]
    public void Serious_FiresOncePerCrossing()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(86), null);
        module.Judge(Hottest(90), null);
        module.Judge(Hottest(92), null);
        module.Judge(Hottest(88), null);
        module.Judge(Hottest(81), null);

        Assert.Single(announcer.Calls);
    }

    [Fact]
    public void Serious_RearmsOnlyAfterFallingFiveBelowIt()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(86), null);
        module.Judge(Hottest(81), null);  // 4 below 85: still armed off
        Assert.Single(announcer.Calls);

        module.Judge(Hottest(80), null);  // 5 below: re-armed
        module.Judge(Hottest(86), null);  // a new crossing
        Assert.Equal(2, announcer.Calls.Count);
    }

    [Fact]
    public void Critical_FiresOncePerCrossing_AndRearmsAt90()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(96), null);
        module.Judge(Hottest(99), null);
        module.Judge(Hottest(91), null);  // 4 below 95: still armed off
        module.Judge(Hottest(96), null);
        Assert.Single(announcer.Calls);

        module.Judge(Hottest(90), null);  // 5 below: re-armed
        module.Judge(Hottest(96), null);
        Assert.Equal(2, announcer.Calls.Count);
        Assert.All(announcer.Calls, call => Assert.Equal("Computer is very hot", call.Toast.Title));
    }

    [Fact]
    public void Critical_SupersedesSerious_WhenItJumpsStraightThere()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(70), null);
        module.Judge(Hottest(96), null);

        var toast = announcer.Calls.Single().Toast;
        Assert.Equal("Computer is very hot", toast.Title);
    }

    [Fact]
    public void Critical_SupersedesSerious_ThenCoolingToWarmDoesNotWarnAgain()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(88), null);   // serious
        module.Judge(Hottest(96), null);   // critical replaces it
        module.Judge(Hottest(88), null);   // back in the serious band: no new warm toast
        module.Judge(Hottest(84), null);

        Assert.Equal(2, announcer.Calls.Count);
        Assert.Equal("Computer is warm", announcer.Calls[0].Toast.Title);
        Assert.Equal("Computer is very hot", announcer.Calls[1].Toast.Title);
    }

    [Fact]
    public void Serious_AfterCriticalCooledBelowSerious_WarmsAgainOnlyAfterTheCrossing()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(96), null);   // critical
        module.Judge(Hottest(89), null);   // critical re-armed, serious still latched (89 is above 80)
        module.Judge(Hottest(86), null);   // no toast: the serious crossing of this heat is not finished
        Assert.Single(announcer.Calls);

        module.Judge(Hottest(79), null);   // serious re-armed
        module.Judge(Hottest(86), null);
        Assert.Equal(2, announcer.Calls.Count);
        Assert.Equal("Computer is warm", announcer.Calls[1].Toast.Title);
    }

    [Fact]
    public void NoReading_KeepsTheLevelsAsTheyWere()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(86), null);
        module.Judge(null, null);  // the helper gave no reading: nothing is judged
        module.Judge(Hottest(86), null);

        Assert.Single(announcer.Calls);
    }

    [Fact]
    public void BelowSerious_NothingFires()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HeatModule(announcer);

        module.Judge(Hottest(60), "Gradle");
        module.Judge(Hottest(85), "Gradle");

        Assert.Empty(announcer.Calls);
    }
}
