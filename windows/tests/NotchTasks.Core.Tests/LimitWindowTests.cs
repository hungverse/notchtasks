using System.Globalization;
using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

public class LimitWindowTests
{
    private static readonly CultureInfo EnGb = CultureInfo.GetCultureInfo("en-GB");

    [Fact]
    public void WindowLabel_HoursUnderADay_DaysAfter()
    {
        Assert.Equal("5h", Limit.WindowLabel(300));
        Assert.Equal("7d", Limit.WindowLabel(10080));
    }

    [Fact]
    public void Step_FivePercentSteps_ThenNinetyNineAndHundred()
    {
        Assert.Equal(80, Limit.Step(84));
        Assert.Equal(85, Limit.Step(85));
        Assert.Equal(95, Limit.Step(97));
        Assert.Equal(99, Limit.Step(99.4));
        Assert.Equal(100, Limit.Step(100));
    }

    [Fact]
    public void ResetText_TimeToday_WeekdayWithinAWeek_ElseDate_Utc_EnGb()
    {
        var now = new DateTimeOffset(2026, 11, 11, 6, 53, 0, TimeSpan.Zero);

        var today = new LimitWindow("5h", 0, new DateTimeOffset(2026, 11, 11, 7, 21, 6, TimeSpan.Zero));
        Assert.Equal("07:21", Limit.ResetText(today, now, EnGb, TimeZoneInfo.Utc));

        var week = new LimitWindow("7d", 0, new DateTimeOffset(2026, 11, 14, 12, 19, 38, TimeSpan.Zero));
        Assert.Equal("Sat 12:19", Limit.ResetText(week, now, EnGb, TimeZoneInfo.Utc));

        var later = new LimitWindow("7d", 0, now.AddDays(20));
        Assert.Equal("1 Dec", Limit.ResetText(later, now, EnGb, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Badges_PortOfMac()
    {
        // 3 windows, one at 92 %: it is shown with its reset
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var windows = new[]
        {
            new LimitWindow("5h", 20, now.AddHours(1)),
            new LimitWindow("7d", 44, now.AddDays(5)),
            new LimitWindow("Sonnet", 92, now.AddHours(2)),
        };

        var (shown, hidden) = Limit.Badges(windows, 0, now);

        Assert.Empty(hidden);
        Assert.Equal(3, shown.Count);
        Assert.Null(shown[0].Detail);
        Assert.Null(shown[1].Detail);
        Assert.Equal("Sonnet", shown[2].Label);
        Assert.StartsWith("92% · ", shown[2].Detail);
        Assert.Equal(92, shown[2].Window.Used);
    }

    [Fact]
    public void Badges_CalmLimits_HaveNoDetail()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var (shown, _) = Limit.Badges(new[] { new LimitWindow("5h", 20, now.AddHours(1)), new LimitWindow("7d", 44, now.AddHours(1)) }, 0, now);
        Assert.All(shown, b => Assert.Null(b.Detail));
    }

    [Fact]
    public void Badges_WarningLimit_ShowsPercent()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var (shown, _) = Limit.Badges(new[] { new LimitWindow("5h", 76, now.AddHours(1)) }, 0, now);
        Assert.Equal("76%", shown[0].Detail);
    }

    [Fact]
    public void Badges_PressingResetWins_TiedUsageGoesToLaterReset()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var mixed = Limit.Badges(new[] { new LimitWindow("5h", 94, now.AddHours(1)), new LimitWindow("7d", 76, now.AddDays(3)) }, 0, now).Shown;
        Assert.StartsWith("94% · ", mixed[0].Detail);
        Assert.Equal("76%", mixed[1].Detail);

        var urgent = Limit.Badges(new[] { new LimitWindow("5h", 94, now.AddHours(1)), new LimitWindow("7d", 94, now.AddHours(2)) }, 0, now).Shown;
        Assert.Equal("94%", urgent[0].Detail);
        Assert.StartsWith("94% · ", urgent[1].Detail);
    }

    [Fact]
    public void Badges_FullLimit_ArrowAndReset()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var shown = Limit.Badges(new[] { new LimitWindow("5h", 100, now.AddHours(1)) }, 0, now).Shown;
        Assert.StartsWith("→ ", shown[0].Detail);
    }

    [Fact]
    public void Badges_OverflowHidden_NarrowRows()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        LimitWindow W(string label, double used) => new(label, used, now.AddHours(1));
        var many = new[] { W("a", 1), W("b", 2), W("c", 3), W("d", 4), W("e", 5) };

        Assert.Equal(2, Limit.Badges(many, 0, now).Hidden.Count);
        Assert.All(Limit.Badges(many, 1, now).Shown, b => Assert.Null(b.Label));
        Assert.Empty(Limit.Badges(many, 2, now).Shown);
    }

    [Fact]
    public void Badges_PressingWindow_StaysVisibleWhenOverflowing()
    {
        var now = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        LimitWindow W(string label, double used) => new(label, used, now.AddHours(1));
        var (shown, hidden) = Limit.Badges(new[] { W("a", 1), W("b", 2), W("c", 3), W("d", 4), W("e", 94) }, 0, now);
        Assert.Contains(shown, b => b.Window.Label == "e" && b.Detail is not null && b.Detail.Contains("94% · "));
        Assert.DoesNotContain(hidden, w => w.Label == "e");
    }
}
