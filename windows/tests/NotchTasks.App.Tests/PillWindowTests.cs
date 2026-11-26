using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
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
            // A title in the compact style: two 170 px side columns, the island's 12 px notch width, and the Mac's 14 px padding outside.
            Assert.Equal(380.0, view.Width);
            view.Show(new Toast("hand", "#FF9F0A", "3:00", "4:59", Compact: true), "island", null, "bouncy");
            // A compact toast: two 52 px side columns, the same 12 px notch width and the same padding.
            Assert.Equal(144.0, view.Width);
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
    public void Pill_EmptyOpen_BodySaysNothingRunning_AndFitsIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                Fixture.Call(pill, "OpenPill");
                SettleOpen(pill);

                var body = Fixture.Named<TextBlock>(pill, "EmptyText");
                Assert.Equal(Visibility.Visible, body.Visibility);
                Assert.Equal("Nothing running", body.Text);
                // The fixed 220 DIP placeholder is gone: the top row, the header strip, the text and the quick bar fit the content.
                Assert.True(Fixture.Field<double>(pill, "_targetHeight") < 220);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Pill_EmptyOpen_WithABadge_HasNoBodyText()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // A badge is a chip-like "collapsed" item (the Mac's hasCollapsed): no section, so no body text.
            state.Settings.Set("badges", JsonNode.Parse("[{\"symbol\":\"seg:E700\",\"right\":false}]"));
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                Fixture.Call(pill, "OpenPill");
                SettleOpen(pill);

                Assert.Equal(Visibility.Collapsed, Fixture.Named<TextBlock>(pill, "EmptyText").Visibility);
                // Only the top row (32), the header strip (26) and the quick bar's margin (32) remain.
                Assert.Equal(PillGeometry.ClosedHeight + 26 + 32, Fixture.Field<double>(pill, "_targetHeight"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Pill_StartingATimerFromTheQuickBar_KeepsThePointerInsideThePill()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Settings.Set("badges", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                Fixture.Call(pill, "OpenPill");
                SettleOpen(pill);

                // The pointer rests on the 5′ pill, as it does when the user clicks it.
                var button = Fixture.Named<Button>(Fixture.Named<QuickBar>(pill, "Bar"), "Timer5");
                var pointer = CentreInPill(pill, button);
                Assert.True(InsideOpenPill(pill, pointer));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(Fixture.Field<bool>(pill, "_open"));
                Assert.NotNull(state.Timer.State);
                Assert.Single(Fixture.Named<StackPanel>(pill, "SectionList").Children.OfType<TimerCard>());

                // The pill settles on its new height: the pointer must still be over it, or the close check would close the pill.
                SettleOpen(pill);
                Assert.True(InsideOpenPill(pill, pointer));
                Assert.True(Fixture.Field<bool>(pill, "_open"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Badges_SitOutermost_ChipsInside()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Settings.Set("placements", JsonNode.Parse("{}"));  // a lone chip: its lead goes left, its trail right
            state.Settings.Set("badges", JsonNode.Parse(
                "[{\"symbol\":\"seg:E700\",\"right\":false},{\"symbol\":\"seg:E701\",\"right\":true}]"));
            var pill = new PillWindow(state);
            try
            {
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                var left = Fixture.Named<StackPanel>(pill, "LeftWing");
                var right = Fixture.Named<StackPanel>(pill, "RightWing");

                // Left: the badge is the outermost (first) child, the chip's lead follows it.
                Assert.Contains(left.Children.OfType<UIElement>(), IsChip);
                Assert.False(IsChip(left.Children[0]));
                // Right: the badge is the outermost (last) child, the chip's trail comes before it.
                Assert.Contains(right.Children.OfType<UIElement>(), IsChip);
                Assert.False(IsChip(right.Children[right.Children.Count - 1]));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    /// Lays the open pill out as it settles: the shape's rectangle at its target size, and the content measured and arranged.
    private static void SettleOpen(PillWindow pill)
    {
        var shape = Fixture.Field<System.Windows.Media.RectangleGeometry>(pill, "_shape");
        shape.BeginAnimation(System.Windows.Media.RectangleGeometry.RectProperty, null);
        shape.BeginAnimation(System.Windows.Media.RectangleGeometry.RadiusXProperty, null);
        shape.BeginAnimation(System.Windows.Media.RectangleGeometry.RadiusYProperty, null);
        var height = Fixture.Field<double>(pill, "_targetHeight");
        shape.Rect = new Rect((PillGeometry.LayoutWidth - PillGeometry.OpenWidth) / 2, 0, PillGeometry.OpenWidth, height);
        shape.RadiusX = shape.RadiusY = PillGeometry.OpenRadius;
        var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
        content.Measure(new Size(1200, 600));
        content.Arrange(new Rect(0, 0, 1200, 600));
        content.UpdateLayout();
    }

    /// The centre of an element, in the open pill's own DIP space (the origin is the pill's top-left corner).
    private static Point CentreInPill(PillWindow pill, FrameworkElement element)
    {
        var shape = Fixture.Named<UIElement>(pill, "PillShape");
        return element.TransformToAncestor(shape).Transform(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    }

    /// True when the point lies within the open pill's current height and width.
    private static bool InsideOpenPill(PillWindow pill, Point p) =>
        p.X >= 0 && p.X <= PillGeometry.OpenWidth && p.Y >= 0 && p.Y <= Fixture.Field<double>(pill, "_targetHeight");

    /// A chip's panel in a wing carries the Tag "chip"; a badge does not.
    private static bool IsChip(UIElement element) => element is FrameworkElement { Tag: "chip" };

    [Fact]
    public void Pill_PendingAsk_IsNotCountedInTheSummary_ToldApartFromIdleJobs()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                // Mac's header counts jobs (working or idle), never pending asks: an ask alone reads "Nothing running".
                state.Asks.Add(new AgentAsk("s1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();
                Assert.Equal("Nothing running", Fixture.Named<TextBlock>(pill, "HeaderSummary").Text);
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
        Assert.Equal(ModuleGlyphs.Fallback, ModuleGlyphs.Get("no-such-glyph").Glyph);
        Assert.False(string.IsNullOrEmpty(ModuleGlyphs.Get("").Glyph));
        Assert.Equal("", ModuleGlyphs.Get("timer").Glyph);
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
                var contentBefore = chipsBefore.OfType<Panel>().SelectMany(p => p.Children.OfType<UIElement>()).ToList();  // before the switch
                Assert.NotEmpty(chipsBefore);
                Assert.NotEmpty(clockBefore);

                L10n.Init(folder, "vi");

                // The timer keeps running through the switch, its chips are drawn again, and the clock text stays a clock.
                Assert.NotNull(state.Timer.State);
                var chipsAfter = Chips(left, right);
                Assert.NotEmpty(chipsAfter);
                // A chip that is kept keeps its panel (and any enter in progress), but its content is drawn again.
                var contentAfter = chipsAfter.OfType<Panel>().SelectMany(p => p.Children.OfType<UIElement>()).ToList();
                Assert.NotEmpty(contentAfter);
                Assert.All(contentAfter, content => Assert.DoesNotContain(content, contentBefore));
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
