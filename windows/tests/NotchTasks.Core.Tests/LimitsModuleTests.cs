using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

public class LimitsModuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<Toast> Toasts { get; } = new();

        public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
    }

    private static ProviderLimits Claude(double used, DateTimeOffset resets) =>
        new("claude", "Claude", "com.anthropic.claudefordesktop", new[] { new LimitWindow("5h", used, resets) }, T0);

    private static LimitInputs Inputs(ProviderLimits? claude, double warn = 80, bool on = true, bool claudeOn = true) =>
        new(on, claudeOn, false, false, claude, null, null, warn);

    [Fact]
    public void Warn_OncePerStep_PerWindow()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);
        var resets = T0.AddHours(3);

        Assert.True(module.Update(Inputs(Claude(81, resets)), T0));
        Assert.Single(announcer.Toasts);

        module.Update(Inputs(Claude(82, resets)), T0);
        Assert.Single(announcer.Toasts);

        module.Update(Inputs(Claude(85, resets)), T0);
        Assert.Equal(2, announcer.Toasts.Count);
    }

    [Fact]
    public void Warn_AgainAfterReset()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        module.Update(Inputs(Claude(81, T0.AddHours(3))), T0);
        module.Update(Inputs(Claude(80, T0.AddHours(8))), T0);

        Assert.Equal(2, announcer.Toasts.Count);
    }

    [Fact]
    public void Warn_SameWindow_ResetSecondsApart_NoDuplicate()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);
        var resets = new DateTimeOffset(2026, 11, 20, 15, 5, 0, TimeSpan.Zero);

        module.Update(Inputs(Claude(81, resets)), T0);
        module.Update(Inputs(Claude(81, resets.AddSeconds(30))), T0);

        Assert.Single(announcer.Toasts);
    }

    [Fact]
    public void Warn_Copy_GlyphTintAndFlags()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        module.Update(Inputs(Claude(81, T0.AddHours(3))), T0);

        var t = Assert.Single(announcer.Toasts);
        Assert.Equal("gauge", t.Glyph);
        Assert.Equal("#FF9F0A", t.Tint);
        Assert.Equal("Claude used 81% of its 5h limit", t.Title);
        Assert.StartsWith("Resets at ", t.Subtitle);
        Assert.Equal("limitWarn", t.Event);
        Assert.True(t.Sticky);
        Assert.Equal("limits", t.Module);
    }

    [Fact]
    public void Tint_RedFromStep95()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        module.Update(Inputs(Claude(96, T0.AddHours(3))), T0);

        Assert.Equal("#FF453A", Assert.Single(announcer.Toasts).Tint);
    }

    [Fact]
    public void Step100_SaysWaitUntil()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        module.Update(Inputs(Claude(100, T0.AddMinutes(90))), T0);

        var t = Assert.Single(announcer.Toasts);
        Assert.Equal("hourglass", t.Glyph);
        Assert.Equal("#FF453A", t.Tint);
        Assert.Equal("Claude reached its 5h limit", t.Title);
        Assert.StartsWith("Wait until ", t.Subtitle);
        Assert.EndsWith("(1h 30m left)", t.Subtitle);
    }

    [Fact]
    public void Step100_UnderAnHour_ShowsMinutes()
    {
        var announcer = new RecordingAnnouncer();
        new LimitsModule(announcer).Update(Inputs(Claude(100, T0.AddMinutes(20))), T0);

        Assert.EndsWith("(20 min left)", Assert.Single(announcer.Toasts).Subtitle);
    }

    [Fact]
    public void LimitsOff_Empty_NoToasts()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        Assert.False(module.Update(Inputs(Claude(95, T0.AddHours(1)), on: false), T0));

        Assert.Empty(module.Limits);
        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void ClaudeOff_LeavesClaudeOut()
    {
        var announcer = new RecordingAnnouncer();
        var module = new LimitsModule(announcer);

        module.Update(Inputs(Claude(95, T0.AddHours(1)), claudeOn: false), T0);

        Assert.Empty(module.Limits);
        Assert.Empty(announcer.Toasts);
    }

    [Fact]
    public void Update_ReturnsWhetherLimitsChanged()
    {
        var module = new LimitsModule(new RecordingAnnouncer());
        var claude = Claude(20, T0.AddHours(1));

        Assert.True(module.Update(Inputs(claude), T0));
        Assert.False(module.Update(Inputs(claude), T0));
        Assert.Equal(new[] { "Claude" }, module.Limits.Select(p => p.Name));
    }

    [Fact]
    public void ChipBoard_LimitChip_OnlyAboveWarn()
    {
        var quiet = new SensorState(Array.Empty<string>(), false, false);
        var claude = new ProviderLimits("claude", "Claude", null, new[]
        {
            new LimitWindow("5h", 79, T0.AddHours(1)),
            new LimitWindow("7d", 85, T0.AddDays(3)),
        }, T0);
        var codex = new ProviderLimits("codex", "Codex", null, new[] { new LimitWindow("5h", 60, T0.AddHours(1)) }, T0);

        var snap = new NotchSnapshot(new[] { "limits" }, 0, null, null, quiet, null, null)
        {
            Limits = new[] { claude, codex },
            LimitWarn = 80,
        };

        var chips = ChipBoard.Chips(snap);
        var chip = Assert.Single(chips);
        Assert.Equal(ChipKind.Limit, chip.Kind);
        Assert.Equal("limits", chip.Module);
        Assert.Equal("Claude", chip.Limit!.Name);
        Assert.Equal("7d", chip.Limit.W.Label);

        // nothing at or above the warning: no chip
        var calm = snap with { Limits = new[] { codex } };
        Assert.Empty(ChipBoard.Chips(calm));

        // the module off: no chip even when a limit is high
        var off = snap with { ActiveModules = Array.Empty<string>() };
        Assert.Empty(ChipBoard.Chips(off));
    }
}
