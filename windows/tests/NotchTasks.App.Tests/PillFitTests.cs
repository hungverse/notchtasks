using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            // The regular size: a 20 DIP icon, padding 6, so the lone 18 DIP badge is the circle of 32. (In the compact size the
            // padding is 8 for its 16 DIP icon, and the badge measures 34: the badge is drawn at 18 whatever the size.)
            state.Settings.Set("size", JsonValue.Create("regular"));
            state.Settings.Set("badges", JsonNode.Parse(OneBadge));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                Assert.Equal(PillGeometry.ClosedHeight, Fixture.Field<double>(pill, "_targetWidth"));

                Settle(pill);
                Arrange(pill);
                Assert.Equal(PillGeometry.ClosedHeight, Fixture.Field<RectangleGeometry>(pill, "_shape").Rect.Width, 0.5);

                var badge = Assert.IsAssignableFrom<FrameworkElement>(Fixture.Named<StackPanel>(pill, "LeftWing").Children[0]);
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
            state.Settings.Set("size", JsonValue.Create("regular"));  // padding 6, as in the test above
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

                // The left wing is the badge alone (18 DIP); the right wing is the chip, its ink without its own 3 DIP margins.
                var right = Fixture.Named<StackPanel>(pill, "RightWing");
                var rightInk = right.DesiredSize.Width - 3 - 3;
                var expected = 2 * 6 + 18 + 10 + rightInk;  // padding 6 each side (a 20 DIP icon), the badge, the gap, the chip
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
