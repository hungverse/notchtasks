using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchTasks.Core.Motion;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The closed pill fits its content: one icon is a circle, the wings sit side by side, and the shape animates when it
/// appears, grows, or shrinks back to idle.
public sealed class PillFitTests
{
    private const string OneBadge = "[{\"symbol\":\"seg:E700\",\"right\":false}]";

    [Fact]
    public void OneBadge_ClosedTargetIsOneCircle_AndTheBadgeIsCentred()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // The default size is compact: a 16 DIP badge (the Mac's iconSize) and padding 8, so one badge is a 32 DIP circle.
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Assert.Equal(PillGeometry.ClosedHeight, Fixture.Field<double>(pill, "_targetWidth"));

                Settle(pill);
                Assert.Equal(PillGeometry.ClosedHeight, Fixture.Field<RectangleGeometry>(pill, "_shape").Rect.Width, 0.5);

                var badge = Assert.IsAssignableFrom<FrameworkElement>(Fixture.Named<StackPanel>(pill, "LeftWing").Children[0]);
                Assert.Equal(16, badge.Width);  // the compact icon size, as the Mac's iconSize
                // The badge oozes in (its transform moves it until the ooze ends), so the centre is read once that is done.
                PumpFrame();
                Fixture.PumpUntil(() => badge.Opacity == 1 && ((TranslateTransform)((TransformGroup)badge.RenderTransform!).Children[1]).X == 0);
                Arrange(pill);
                var shape = Fixture.Named<FrameworkElement>(pill, "PillShape");
                var centre = badge.TransformToAncestor(shape).Transform(new Point(badge.ActualWidth / 2, badge.ActualHeight / 2));
                Assert.Equal(PillGeometry.LayoutWidth / 2, centre.X, 0.5);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void BadgeOnOneSide_ChipOnTheOther_WidthIsTheWingsPlusTheTenGap()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Settings.Set("placements", JsonNode.Parse("{\"timer\":\"right\"}"));  // not the lone case: a gap of 10
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                Settle(pill);
                Arrange(pill);

                // The left wing is the badge alone; the right wing is the chip. Each wing's ink is its width less its own end margins (3 DIP).
                var leftInk = Fixture.Named<StackPanel>(pill, "LeftWing").DesiredSize.Width - 3 - 3;
                var rightInk = Fixture.Named<StackPanel>(pill, "RightWing").DesiredSize.Width - 3 - 3;
                var padding = PillGeometry.ClosedPadding(PillGeometry.IconSizeFor(state.Settings.Size));
                var expected = 2 * padding + leftInk + 10 + rightInk;  // the padding each side, the badge, the gap of 10, the chip
                Assert.Equal(expected, Fixture.Field<double>(pill, "_targetWidth"), 0.5);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void RemovingEverything_ShrinksToIdle_AndThePillFadesOut()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Settle(pill);
                var shape = Fixture.Named<UIElement>(pill, "PillShape");
                Assert.Equal(1.0, shape.Opacity, 0.01);

                state.Settings.Set("badges", JsonNode.Parse("[]"));
                Fixture.Flush();  // settings changes reach the pill through the dispatcher
                state.Publish();
                Assert.Equal(PillGeometry.IdleWidth(PillGeometry.IconSizeFor(state.Settings.Size)), Fixture.Field<double>(pill, "_targetWidth"), 0.5);

                Fixture.PumpUntil(() => shape.Opacity == 0);
                Settle(pill);
                Assert.Equal(0.0, shape.Opacity, 0.01);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void SecondPublish_WithTheSameContent_DoesNotStartANewShapeAnimation()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Settle(pill);
                var token = Fixture.Field<int>(pill, "_animation");

                state.Publish();
                Assert.Equal(token, Fixture.Field<int>(pill, "_animation"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ReducedMotion_NewItemOozesOnlyInOpacity()
    {
        Sta.Run(() =>
        {
            var item = new Border { Width = 18, Height = 18 };
            var host = new Window { Content = item };
            Fixture.ShowOffScreen(host);
            try
            {
                PillMotion.Ooze(item, leftWing: true, "reduced", animationsOn: true);
                Assert.Null(item.RenderTransform as TransformGroup);  // no scale and no offset: opacity only
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void Smooth_NewLeftWingItem_OozesFromItsCentreSideEdge()
    {
        Sta.Run(() =>
        {
            var item = new Border { Width = 18, Height = 18 };
            var host = new Window { Content = item };
            Fixture.ShowOffScreen(host);
            try
            {
                PillMotion.Ooze(item, leftWing: true, "bouncy", animationsOn: true);
                var group = Assert.IsType<TransformGroup>(item.RenderTransform);
                var scale = Assert.IsType<ScaleTransform>(group.Children[0]);
                Assert.True(scale.HasAnimatedProperties);
                Assert.Equal(new Point(1, 0.5), item.RenderTransformOrigin);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void SizeChange_RemeasuresAndRespringsTheWidth()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // Two badges in one wing: their ink is 16 + 6 + 16 at compact (the 6 is the two inner margins), 20 + 6 + 20 at regular.
            state.Settings.Set("badges", JsonNode.Parse("[{\"id\":\"a\",\"symbol\":\"seg:E700\",\"right\":false},{\"id\":\"b\",\"symbol\":\"seg:E701\",\"right\":false}]"));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Assert.Equal(2 * 8 + 38, Fixture.Field<double>(pill, "_targetWidth"), 0.5);  // compact: padding 8

                state.Settings.Set("size", JsonValue.Create("regular"));
                Fixture.Flush();
                state.Publish();
                Assert.Equal(2 * 6 + 46, Fixture.Field<double>(pill, "_targetWidth"), 0.5);  // regular: padding 6
                Settle(pill);
                Assert.Equal(2 * 6 + 46, Fixture.Field<RectangleGeometry>(pill, "_shape").Rect.Width, 0.5);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ClosedRetarget_GrowsWithTheGrowCurve_AndShrinksWithTheSettleCurve()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Settle(pill);

                // A chip joins the closed badge: the shape grows, so the grow curve (with its bounce) is used.
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                Assert.True(pill.LastCurveOpening);
                Settle(pill);

                // The chip goes: it fades first, then the pill shrinks back to the badge's circle, with the settle curve.
                state.Timer.Stop();
                state.Publish();
                Fixture.PumpUntil(() => Fixture.Field<double>(pill, "_targetWidth") < 40);
                Assert.False(pill.LastCurveOpening);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void IdleShape_NarrowerThanItsCorners_HitTestDoesNotThrow()
    {
        // The idle shape is 12 DIP wide, and rounding can leave its corner bounds a hair apart; the test must not throw.
        var idle = new Rect(274, 0, 11.999999999999986, 32);
        var distance = PillHitTest.DistanceToShape(new Point(280, 16), idle, PillGeometry.ClosedRadius);
        Assert.True(distance <= 0);
        Assert.True(PillHitTest.InRoundedRect(new Point(280, 16), idle.X, idle.Y, idle.Width, idle.Height, PillGeometry.ClosedRadius));
    }

    /// Shows the pill off screen: a spring runs only in a presented window, and the pill is moved away from the top edge.
    private static void ShowAway(PillWindow pill)
    {
        Fixture.ShowOffScreen(pill);
        pill.Left = -10000;
        pill.Top = -10000;
    }

    /// Runs the dispatcher for one frame, so the animations started in it have moved their values.
    private static void PumpFrame()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// Runs the dispatcher until the pill's shape animation has finished (or five seconds pass).
    private static void Settle(PillWindow pill) => Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating"));

    /// Lays the pill's content out as the layout pass does for a shown window, so the wings' sizes and positions are set.
    private static void Arrange(PillWindow pill)
    {
        var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
        content.Measure(new Size(1200, 600));
        content.Arrange(new Rect(0, 0, 1200, 600));
        content.UpdateLayout();
    }
}
