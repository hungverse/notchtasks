using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// After the closed pill's spring settles, its two wings' ink does not overlap, keeps at least WingGap between them, and lies inside
/// the closed shape, whatever happened to a chip's width while the pill was open, a toast showed or a toast was closing.
public sealed class PillWingOverlapTests
{
    private const double Epsilon = 0.5;

    [Fact]
    public void LimitChip_GrowsWhileClosed_WingsHold()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                AssertWingsHold(pill, "8%", 0, 0);

                SetClaude(state, 82);
                state.Publish();
                AssertWingsHold(pill, "82%", 1, 0);

                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                AssertWingsHold(pill, "Waiting 2h 13m", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void TwoLimitChangesInOneTick_WhileClosed_WingsHold()
    {
        // Two publishes with no layout pass between them: the second changes a panel the first one just filled.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                Settle(pill);

                SetClaude(state, 82);
                state.Publish();
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                AssertWingsHold(pill, "82% then Waiting 2h 13m in one tick", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void LimitChip_GrowsWhileOpen_ThenTheClosedPillFollows()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                Settle(pill);

                Fixture.Call(pill, "OpenPill");
                Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill
                Settle(pill);
                SetClaude(state, 82);
                state.Publish();
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                Settle(pill);

                Fixture.Call(pill, "ClosePill");
                AssertWingsHold(pill, "Waiting 2h 13m, after the pill closed", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void LimitChip_GrowsWhileAToastShows_ThenTheToastEnds()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                Settle(pill);

                state.Announce(new Toast("check", "#30D158", "Task done"));
                Settle(pill);
                SetClaude(state, 82);
                state.Publish();
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                Settle(pill);

                state.Toasts.Dismiss(DateTimeOffset.Now);
                AssertWingsHold(pill, "Waiting 2h 13m, after the toast ended", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void LimitChip_GrowsWhileAToastCloses_WingsHoldAfterTheClose()
    {
        // The toast's closing move is under way when the limit changes: the change must still reach the closed pill.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                Settle(pill);

                state.Announce(new Toast("check", "#30D158", "Task done"));
                Settle(pill);

                state.Toasts.Dismiss(DateTimeOffset.Now);  // the toast's close starts: the closed pill is not retargeted meanwhile
                SetClaude(state, 82);
                state.Publish();
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                AssertWingsHold(pill, "Waiting 2h 13m, while the toast closed", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void LevelBarReplacesAClosingToast_ThenGrows_WingsHold()
    {
        // A level bar takes the shape while the toast closes, and the bar goes after: a limit that grows then must still reach the closed pill.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 8);
                state.Publish();
                Settle(pill);

                state.Announce(new Toast("check", "#30D158", "Task done"));
                Settle(pill);
                state.Levels.Brightness(0.2);  // the first reading is the baseline
                state.Toasts.Dismiss(DateTimeOffset.Now);  // the toast starts to close...
                state.Levels.Brightness(0.6);  // ...and a bar takes the shape over
                Settle(pill);
                state.Levels.Hide();
                Settle(pill);

                SetClaude(state, 82);
                state.Publish();
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Publish();
                AssertWingsHold(pill, "Waiting 2h 13m, after a level bar replaced a closing toast", 1, 0);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void RightAndLeftWingsGrow_InOnePublish_WingsHold()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 82);
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                AssertWingsHold(pill, "a 5 minute timer and 82%", 1, 1);

                // The right wing's chip grows (the timer's time left) while the left limit's text grows.
                SetClaude(state, 100, resetsIn: TimeSpan.FromMinutes(133));
                state.Timer.Start(725, DateTimeOffset.Now);
                state.Publish();
                AssertWingsHold(pill, "a longer timer and Waiting 2h 13m", 1, 1);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void LeftChipExits_WhileTheLimitTextGrows_WingsHold()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = NewState();
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                SetClaude(state, 82);
                SetCodex(state, 90);
                state.Publish();
                AssertWingsHold(pill, "two limit chips on the left", 2, 0);
                Assert.Equal(2, ChipsIn(pill, "LeftWing"));

                // Codex's chip leaves on the left (it fades, then goes) while Claude's text grows.
                state.LimitsModule.Update(Inputs(claude: Claude(100, TimeSpan.FromMinutes(133)), codex: null), DateTimeOffset.Now);
                state.Publish();
                AssertWingsHold(pill, "Codex's chip gone, Claude Waiting 2h 13m", 1, 0);
                Assert.Equal(1, ChipsIn(pill, "LeftWing"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    /// The app state with the limits and timer modules on, the timer on the right and the limits on the left.
    private static AppState NewState()
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings));
        state.Settings.Set("modules", JsonNode.Parse("[\"limits\",\"timer\"]"));
        state.Settings.Set("disabled", JsonNode.Parse("[]"));
        state.Settings.Set("placements", JsonNode.Parse("{\"limits\":\"left\",\"timer\":\"right\"}"));
        Fixture.Flush();  // the module host starts the limits module now, not in the middle of a test step (it would clear the limits it is given)
        return state;
    }

    private static void SetClaude(AppState state, double used, TimeSpan? resetsIn = null) =>
        state.LimitsModule.Update(Inputs(claude: Claude(used, resetsIn), codex: null), DateTimeOffset.Now);

    private static void SetCodex(AppState state, double used) =>
        state.LimitsModule.Update(Inputs(claude: Claude(82, null), codex: Codex(used)), DateTimeOffset.Now);

    private static LimitInputs Inputs(ProviderLimits? claude, ProviderLimits? codex) =>
        new(On: true, ClaudeEnabled: claude is not null, CodexEnabled: codex is not null, AgyEnabled: false,
            Claude: claude, Codex: codex, Antigravity: null, LimitWarn: 101);  // above 100: no warning toast (the chip shows from the settings' 80)

    private static ProviderLimits Claude(double used, TimeSpan? resetsIn) =>
        new("claude", "Claude", null, new[] { new LimitWindow("5h", used, DateTimeOffset.Now + (resetsIn ?? TimeSpan.FromHours(2))) }, DateTimeOffset.Now);

    private static ProviderLimits Codex(double used) =>
        new("codex", "Codex", null, new[] { new LimitWindow("5h", used, DateTimeOffset.Now + TimeSpan.FromHours(2)) }, DateTimeOffset.Now);

    /// The closed pill's invariant, read from the rendered wings once the move and the fades are done: the two wings' ink rectangles
    /// (relative to the shape) do not overlap, the gap between them is at least WingGap, and both lie inside the closed shape.
    private static void AssertWingsHold(PillWindow pill, string when, int leftChips, int rightChips)
    {
        Settle(pill);
        Arrange(pill);
        var shape = Fixture.Named<UIElement>(pill, "PillShape");
        var outline = Fixture.Field<RectangleGeometry>(pill, "_shape").Rect;
        var left = InkOf(Fixture.Named<StackPanel>(pill, "LeftWing"), shape);
        var right = InkOf(Fixture.Named<StackPanel>(pill, "RightWing"), shape);
        Assert.Equal(leftChips, ChipsIn(pill, "LeftWing"));  // the step is what the test says it is: a wing that is empty proves nothing
        Assert.Equal(rightChips, ChipsIn(pill, "RightWing"));
        var single = Fixture.Field<bool>(pill, "_single");

        if (!left.IsEmpty)
        {
            Assert.True(left.Left >= outline.Left - Epsilon && left.Right <= outline.Right + Epsilon,
                $"{when}: the left ink {left.Left:F1}..{left.Right:F1} is outside the shape {outline.Left:F1}..{outline.Right:F1}");
        }
        if (!right.IsEmpty)
        {
            Assert.True(right.Left >= outline.Left - Epsilon && right.Right <= outline.Right + Epsilon,
                $"{when}: the right ink {right.Left:F1}..{right.Right:F1} is outside the shape {outline.Left:F1}..{outline.Right:F1}");
        }
        if (!left.IsEmpty && !right.IsEmpty)
        {
            var gap = right.Left - left.Right;
            var wanted = PillGeometry.WingGap(left.Width, right.Width, single);
            Assert.True(gap >= wanted - Epsilon, $"{when}: the wings are {gap:F1} DIP apart, closer than the {wanted} DIP WingGap (overlap {wanted - gap:F1})");
        }
    }

    /// A wing's ink: the box that holds its chip panels and badges, from the first one's left edge to the last one's right edge.
    private static Rect InkOf(StackPanel wing, UIElement shape)
    {
        var boxes = wing.Children.OfType<FrameworkElement>().Where(e => e.Visibility == Visibility.Visible && e.ActualWidth > 0)
            .Select(e => new Rect(e.TransformToAncestor(shape).Transform(new Point(0, 0)), new Size(e.ActualWidth, e.ActualHeight)))
            .ToList();
        if (boxes.Count == 0)
        {
            return Rect.Empty;
        }
        var left = boxes.Min(b => b.Left);
        var right = boxes.Max(b => b.Right);
        var top = boxes.Min(b => b.Top);
        var bottom = boxes.Max(b => b.Bottom);
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    /// Shows the pill off screen: a spring runs only in a presented window, and the pill is moved away from the top edge.
    private static void ShowAway(PillWindow pill)
    {
        Fixture.ShowOffScreen(pill);
        pill.Left = -10000;
        pill.Top = -10000;
    }

    /// Runs the dispatcher until the pill's shape move has finished and no chip is still leaving, then lets the fades and the ooze end.
    private static void Settle(PillWindow pill)
    {
        Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating") && !AnyExiting(pill));
        Fixture.PumpUntil(() => false, 1000);
        Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating") && !AnyExiting(pill));
    }

    /// The chip panels a wing holds (the ones still leaving included: none once Settle has run).
    private static int ChipsIn(PillWindow pill, string wing) =>
        Fixture.Named<StackPanel>(pill, wing).Children.OfType<FrameworkElement>().Count(e => e.Tag is "chip");

    private static bool AnyExiting(PillWindow pill) =>
        Fixture.Named<StackPanel>(pill, "LeftWing").Children.OfType<FrameworkElement>().Any(PillMotion.IsExiting)
        || Fixture.Named<StackPanel>(pill, "RightWing").Children.OfType<FrameworkElement>().Any(PillMotion.IsExiting);

    /// Lays the pill's content out as the layout pass does for a shown window, so the wings' sizes and positions are set.
    private static void Arrange(PillWindow pill)
    {
        var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
        content.Measure(new Size(1200, 600));
        content.Arrange(new Rect(0, 0, 1200, 600));
        content.UpdateLayout();
    }
}
