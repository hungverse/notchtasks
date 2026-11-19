using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class PillWindowTests
{
    [Fact]
    public void PillConstructsAndLaysOut()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // Not shown: Show places the pill on the real top edge of the screen. The content is measured and arranged
            // directly, which is the layout pass the XAML and its bindings need.
            var pill = new PillWindow(state);
            try
            {
                var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
                content.Measure(new Size(1200, 600));
                content.Arrange(new Rect(0, 0, 1200, 600));
                Assert.True(content.IsArrangeValid);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Header_ShowsTheLoadMeters_OnlyWithALoadAndSystemLoadOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                var summary = Fixture.Named<UIElement>(pill, "HeaderSummary");
                var meters = Fixture.Named<UIElement>(pill, "LoadMeters");

                // No load yet: the summary text stays.
                state.Publish();
                Assert.Equal(Visibility.Visible, summary.Visibility);
                Assert.Equal(Visibility.Collapsed, meters.Visibility);

                // A load and SystemLoad on: the CPU and RAM meters take the summary's place.
                state.Health.Sample(new CpuTimes(0, 0, 0), 0.4);
                state.Health.Sample(new CpuTimes(50, 70, 30), 0.4);
                state.Publish();
                Assert.NotNull(state.Snapshot.Load);
                Assert.Equal(Visibility.Collapsed, summary.Visibility);
                Assert.Equal(Visibility.Visible, meters.Visibility);

                // SystemLoad off: the summary text is back, even with a load.
                state.Settings.Set("systemLoad", JsonValue.Create(false));
                state.Publish();
                Assert.Equal(Visibility.Visible, summary.Visibility);
                Assert.Equal(Visibility.Collapsed, meters.Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ToastView_Style_FollowsSettingAtShow()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            var toast = new Toast("hand", "#FF9F0A", "Agent wants to run", "echo hi");

            view.Show(toast, "compact", null, "bouncy");
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "CompactRow").Visibility);
            // A title in the compact style: two 170 px side columns, the pill's 200 px, and the Mac's 14 px padding outside.
            Assert.Equal(568.0, view.Width);
            view.Show(new Toast("hand", "#FF9F0A", "3:00", "4:59", Compact: true), "island", null, "bouncy");
            // A compact toast: two 52 px side columns, the same 200 px and the same padding.
            Assert.Equal(332.0, view.Width);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "WideBody").Visibility);

            view.Show(toast, "banner", null, "bouncy");
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "CompactRow").Visibility);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "BannerBar").Visibility);
            Assert.Equal(520.0, view.Width);

            view.Show(toast, "island", null, "bouncy");
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "BannerBar").Visibility);
            Assert.Equal(340.0, view.Width);
        });
    }

    [Fact]
    public void ToastView_AskShowsDenyAllow_QuestionShowsReply()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            var approval = new AgentAsk("a1", 1, "work", "Bash", "rm -rf build", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");
            var question = new AgentAsk("q1", 1, "work", "AskUserQuestion", "Which one?",
                new[] { new AskQuestion("Pick one", null, new[] { "A", "B" }, false) }, DateTimeOffset.Now, "claude");

            view.Show(new Toast("hand", "#FF9F0A", "Run", "rm", AskId: "a1", Module: "agents"), "island", approval, "bouncy");
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "ApprovalButtons").Visibility);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "ReplyButton").Visibility);

            view.Show(new Toast("question", "#FF9F0A", "Ask", "Which one?", AskId: "q1", Module: "agents"), "island", question, "bouncy");
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "ApprovalButtons").Visibility);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "ReplyButton").Visibility);
        });
    }

    [Fact]
    public void Pill_ChipsFollowSnapshot()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                state.Asks.Add(new AgentAsk("c1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();

                Assert.Equal(1, state.Snapshot.AskCount);
                Assert.True(HasText(Fixture.Named<UIElement>(pill, "TopRow"), "Waiting for you"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ChipView_TimerTrailShowsRemaining()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            // The timer ends 4:59 after `now`, so 4:59 is left at `now`.
            var chip = new Chip(ChipKind.Timer, "timer", "timer")
            {
                Timer = new TimerState(now + TimeSpan.FromSeconds(299), TimeSpan.FromMinutes(5)),
            };
            var trail = Assert.IsType<TextBlock>(ChipView.Trail(chip, 18, Colors.Orange, new Dictionary<string, string>(), now));
            Assert.Equal("4:59", trail.Text);
        });
    }

    [Fact]
    public void Pill_EmptyOpen_ShowsNothingRunning()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                Fixture.Call(pill, "OpenPill");
                Assert.True(Fixture.Field<bool>(pill, "_open"));
                Assert.Equal("Nothing running", Fixture.Named<TextBlock>(pill, "HeaderSummary").Text);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(pill, "SectionScroll").Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Pill_WaitingAsk_ShowsInSummary()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                state.Asks.Add(new AgentAsk("s1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();
                Assert.Equal("1 waiting", Fixture.Named<TextBlock>(pill, "HeaderSummary").Text);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Pill_OpenHeight_CountsHeaderAndQuickBarOnce()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            // A fixed 50 DIP section in the "ci" module (on by default): its content, not the pill's own layout, sets the height.
            Func<AppState, FrameworkElement?> section = _ => new Border { Height = 50 };
            try
            {
                SectionRegistry.Register("ci", section);
                state.Publish();
                Fixture.Call(pill, "OpenPill");

                // The top row (32) + the scroll's 26 DIP header margin + the 50 content + its 10 DIP padding on each side + the 32 DIP quick bar margin.
                Assert.Equal(32.0 + 26 + 50 + 20 + 32, Fixture.Field<double>(pill, "_targetHeight"));
            }
            finally
            {
                SectionRegistry.Unregister("ci", section);
                pill.Close();
            }
        });
    }

    [Fact]
    public void ModuleGlyphs_UnknownName_GivesFallback()
    {
        Assert.Equal(ModuleGlyphs.Fallback, ModuleGlyphs.Get("no-such-glyph"));
        Assert.False(string.IsNullOrEmpty(ModuleGlyphs.Get("")));
        Assert.Equal("", ModuleGlyphs.Get("timer"));
    }

    [Fact]
    public void SectionRegistry_Order_CallsFirstThenActiveModulesOrder()
    {
        Assert.Equal(new[] { "calls", "timer", "agents" }, SectionRegistry.Order(new[] { "timer", "calls", "agents" }));
        Assert.Equal(new[] { "calls", "agents" }, SectionRegistry.Order(new[] { "agents", "calls" }));
        Assert.Equal(new[] { "agents", "timer" }, SectionRegistry.Order(new[] { "agents", "timer" }));
    }

    [Fact]
    public void LanguageSwitch_KeepsTimerAndRedraws()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            var folder = MacTables();
            var pill = new PillWindow(state);
            try
            {
                L10n.Init(folder, "en");
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                var left = Fixture.Named<StackPanel>(pill, "LeftWing");
                var right = Fixture.Named<StackPanel>(pill, "RightWing");
                var chipsBefore = Chips(left, right);
                var clockBefore = Clocks(left, right);
                Assert.NotEmpty(chipsBefore);
                Assert.NotEmpty(clockBefore);

                L10n.Init(folder, "vi");

                // The timer keeps running through the switch, its chips are drawn again, and the clock text stays a clock.
                Assert.NotNull(state.Timer.State);
                var chipsAfter = Chips(left, right);
                Assert.NotEmpty(chipsAfter);
                Assert.All(chipsAfter, chip => Assert.DoesNotContain(chip, chipsBefore));
                Assert.NotEmpty(Clocks(left, right));
                Assert.All(Clocks(left, right), text => Assert.Matches(@"^\d+:\d\d$", text));
            }
            finally
            {
                pill.Close();
                L10n.Init(folder, "en");
            }
        });
    }

    /// The Mac's translation tables, which the pill's texts come from in these tests.
    private static string MacTables()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("mac/Sources"), "mac", "Sources");
    }

    /// The chips in the pill's two wings.
    private static List<UIElement> Chips(StackPanel left, StackPanel right) =>
        left.Children.OfType<UIElement>().Concat(right.Children.OfType<UIElement>()).ToList();

    /// The clock texts (m:ss) shown under the wings.
    private static List<string> Clocks(params DependencyObject[] roots) =>
        roots.SelectMany(ClocksUnder).ToList();

    private static List<string> ClocksUnder(DependencyObject node)
    {
        var found = new List<string>();
        if (node is TextBlock block && Regex.IsMatch(block.Text, @"^\d+:\d\d$"))
        {
            found.Add(block.Text);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            found.AddRange(ClocksUnder(VisualTreeHelper.GetChild(node, i)));
        }
        return found;
    }

    /// True when a text block under `node` shows `text`.
    private static bool HasText(DependencyObject node, string text)
    {
        if (node is TextBlock block && block.Text == text)
        {
            return true;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (HasText(VisualTreeHelper.GetChild(node, i), text))
            {
                return true;
            }
        }
        return false;
    }
}
