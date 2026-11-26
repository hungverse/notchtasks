using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NotchTasks.Core.Motion;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The pill's composited shape: one animated RectangleGeometry, and no layout while it moves.
public sealed class PillMotionShapeTests
{
    [Fact]
    public void Animate_KeepsPillShapeLayoutSize()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                var shape = Fixture.Named<FrameworkElement>(pill, "PillShape");
                Arrange(pill);
                var before = (shape.ActualWidth, shape.ActualHeight);

                OpenHeld(pill);
                Arrange(pill);
                var during = (shape.ActualWidth, shape.ActualHeight);
                PumpFor(TimeSpan.FromMilliseconds(100));
                Arrange(pill);
                var mid = (shape.ActualWidth, shape.ActualHeight);
                WaitForSettle(pill);
                Arrange(pill);
                var after = (shape.ActualWidth, shape.ActualHeight);

                Assert.Equal(before, during);
                Assert.Equal(before, mid);
                Assert.Equal(before, after);
                Assert.Equal(PillGeometry.LayoutWidth, after.ActualWidth);
                Assert.Equal(PillGeometry.MaxOpenHeight, after.ActualHeight);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Animate_Retarget_StartsFromCurrentShape()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // One badge: the closed pill is one icon's circle (ClosedHeight wide), so the open flight starts from that.
            state.Settings.Set("badges", JsonNode.Parse("[{\"symbol\":\"seg:E700\",\"right\":false}]"));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                var shape = Fixture.Field<RectangleGeometry>(pill, "_shape");
                OpenHeld(pill);

                // Pump until a frame shows the open in flight (strictly between the closed and open widths). The pump stops there, so
                // the close starts from that value: no timing guess, and no race with the spring's end.
                var end = DateTime.UtcNow.AddSeconds(5);
                while (!InFlight(shape) && Fixture.Field<bool>(pill, "_animating") && DateTime.UtcNow < end)
                {
                    PumpFor(TimeSpan.FromMilliseconds(5));
                }
                Assert.True(InFlight(shape), $"the open was never in flight: width {shape.Rect.Width}");

                // The close starts from where the open had got to, not from the open's start or end.
                Fixture.Call(pill, "ClosePill");
                var width = shape.Rect.Width;

                Assert.True(width > PillGeometry.ClosedHeight && width < PillGeometry.OpenWidth, $"width was {width}");
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Closed_CardLayerHiddenAndNoEffect()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                var card = Fixture.Named<UIElement>(pill, "CardLayer");

                // Closed at rest: hidden (still measured), and no blur effect on the card.
                Assert.Equal(Visibility.Hidden, card.Visibility);
                Assert.Null(card.Effect);

                OpenHeld(pill);
                Assert.Equal(Visibility.Visible, card.Visibility);
                WaitForSettle(pill);
                Assert.Null(card.Effect);

                Fixture.Call(pill, "ClosePill");
                WaitForSettle(pill);
                Assert.Equal(Visibility.Hidden, card.Visibility);
                Assert.Null(card.Effect);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void HitTest_FollowsAnimatedRect()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("badges", JsonNode.Parse("[]"));
            var pill = new PillWindow(state);
            try
            {
                // Not shown: the window handle is zero, so screen points are read as client points at scale 1.
                var shape = Fixture.Field<RectangleGeometry>(pill, "_shape");

                // The layout sits in the window with the host margin on its left, so a layout x is a window x plus `left`.
                var left = (PillGeometry.WindowWidth - PillGeometry.LayoutWidth) / 2;

                // The closed rectangle: 200 wide, centred in the layout, so x in 160..360 and y in 0..32 (layout).
                shape.Rect = new Rect(160, 0, 200, 32);
                shape.RadiusX = shape.RadiusY = PillGeometry.ClosedRadius;
                Assert.True(Inside(pill, (int)left + 260, 16));
                Assert.False(Inside(pill, (int)left + 260, 100));
                Assert.False(Inside(pill, (int)left + 10, 16));

                // The open rectangle: 520 wide, so x in 0..520 and y in 0..400 (layout).
                shape.Rect = new Rect(0, 0, 520, 400);
                shape.RadiusX = shape.RadiusY = PillGeometry.OpenRadius;
                Assert.True(Inside(pill, (int)left + 10, 200));
                Assert.True(Inside(pill, (int)left + 260, 390));
                Assert.False(Inside(pill, (int)left + 260, 410));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Animate_OpenKeepsTheNeonRingInsideTheWindowAtEveryStep()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // One badge, so the closed pill shows the glow too; the open flight starts from the closed one.
            state.Settings.Set("badges", JsonNode.Parse("[{\"symbol\":\"seg:E700\",\"right\":false}]"));
            // The glow look with the bouncy open: its spring overshoots the open size, which is the case the window must hold.
            state.Settings.Set("background", JsonValue.Create("glow"));
            state.Settings.Set("motion", JsonValue.Create("bouncy"));
            var pill = new PillWindow(state);
            try
            {
                ShowAway(pill);
                state.Publish();
                var ring = Fixture.Named<System.Windows.Shapes.Path>(pill, "GlowRing");
                var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
                Assert.Equal(Visibility.Visible, ring.Visibility);
                AssertNoClipAbove(ring);

                OpenHeld(pill);
                var steps = 0;
                var end = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    AssertRingFitsWindow(ring, content);
                    steps++;
                    PumpFor(TimeSpan.FromMilliseconds(16));
                }
                while (Fixture.Field<bool>(pill, "_animating") && DateTime.UtcNow < end);

                Assert.True(steps > 3, $"the open took {steps} steps");
                AssertRingFitsWindow(ring, content);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    /// The ring's stroke and its glow, at the ring's place in the window, lie inside the window on the sides and the bottom (the
    /// top edge sits on the screen edge, so it is exempt). A spring overshoot must not push them past the window's edge.
    private static void AssertRingFitsWindow(System.Windows.Shapes.Path ring, UIElement content)
    {
        var bounds = ring.TransformToAncestor(content).TransformBounds(ring.Data.Bounds);
        var reach = PillGeometry.GlowReach + ring.StrokeThickness / 2;
        Assert.True(bounds.Left - reach >= -0.5, $"the ring's left edge and glow start at {bounds.Left - reach}");
        Assert.True(bounds.Right + reach <= PillGeometry.WindowWidth + 0.5, $"the ring's right glow ends at {bounds.Right + reach}");
        Assert.True(bounds.Bottom + reach <= PillGeometry.WindowHeight + 0.5, $"the ring's bottom glow ends at {bounds.Bottom + reach}");
    }

    /// No element above the ring (up to the window's content) clips it: a clip there would cut the stroke or the glow.
    private static void AssertNoClipAbove(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement layer)
            {
                Assert.Null(layer.Clip);
            }
        }
    }

    /// IsInsidePill takes the internal NativePoint of the pill's assembly, so the test builds one by reflection.
    private static bool Inside(PillWindow pill, int x, int y)
    {
        var assembly = typeof(PillWindow).Assembly;
        var pointType = assembly.GetType("NotchTasks.Pill.NativePoint", throwOnError: true)!;
        var point = Activator.CreateInstance(pointType)!;
        pointType.GetField("X")!.SetValue(point, x);
        pointType.GetField("Y")!.SetValue(point, y);
        var method = typeof(PillWindow).GetMethod("IsInsidePill", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(pill, new[] { point })!;
    }

    /// Shows the pill off screen. WPF runs an animation only while its window is presented, so the motion tests need a shown
    /// pill. PillWindow places itself at the top of the screen when its handle is made, so it is moved away again at once.
    private static void ShowAway(PillWindow pill)
    {
        Fixture.ShowOffScreen(pill);
        pill.Left = -10000;
        pill.Top = -10000;
    }

    /// Whether the shape's width is strictly between the closed and the open width.
    private static bool InFlight(RectangleGeometry shape) =>
        shape.Rect.Width > PillGeometry.ClosedHeight && shape.Rect.Width < PillGeometry.OpenWidth;

    /// Opens the pill and holds it open. A test's pointer is never over the pill, so the close timer would close it 350 ms
    /// after the open; the timer is stopped here, as a pointer on the pill would.
    private static void OpenHeld(PillWindow pill)
    {
        Fixture.Call(pill, "OpenPill");
        Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();
    }

    /// Runs the dispatcher in short spans until the pill's animation has finished (or five seconds pass). Each span lets the
    /// render pass advance the clocks, which a bare dispatcher drain does not.
    private static void WaitForSettle(PillWindow pill)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (Fixture.Field<bool>(pill, "_animating") && DateTime.UtcNow < end)
        {
            PumpFor(TimeSpan.FromMilliseconds(16));
        }
    }

    /// Lays the pill's content out at the test's size, as the layout pass does for a shown window.
    private static void Arrange(PillWindow pill)
    {
        var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
        content.Measure(new Size(1200, 600));
        content.Arrange(new Rect(0, 0, 1200, 600));
        content.UpdateLayout();
    }

    /// Runs the dispatcher for a span, so the animation clocks and CompositionTarget.Rendering can advance.
    private static void PumpFor(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = span };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
