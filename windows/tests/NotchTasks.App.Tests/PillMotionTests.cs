using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// Enter and exit motion (chips, sections, toasts) and the 30 fps decorative loops, which stop when they leave the screen.
public sealed class PillMotionTests
{
    [Fact]
    public void Fade_ReducedOrOff_Is150ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(150), PillMotion.Fade("smooth", animationsOn: false));
        Assert.Equal(TimeSpan.FromMilliseconds(150), PillMotion.Fade("reduced", animationsOn: true));
    }

    [Fact]
    public void Fade_Smooth_Capped350ms()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(350), PillMotion.Fade("smooth", animationsOn: true));
    }

    [Fact]
    public void Exit_RemovesAfterFade()
    {
        Sta.Run(() =>
        {
            var parent = new StackPanel();
            var child = new Border();
            parent.Children.Add(child);
            var window = new Window { Content = parent };
            Fixture.ShowOffScreen(window);
            try
            {
                PillMotion.Exit(parent, child, "smooth", animationsOn: true);
                Assert.True(parent.Children.Contains(child), "the element stays while it fades");
                Assert.True(WaitUntil(() => !parent.Children.Contains(child), TimeSpan.FromSeconds(3)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Exit_ReAddedElement_NotRemoved()
    {
        Sta.Run(() =>
        {
            var parent = new StackPanel();
            var child = new Border();
            parent.Children.Add(child);
            var window = new Window { Content = parent };
            Fixture.ShowOffScreen(window);
            try
            {
                PillMotion.Exit(parent, child, "smooth", animationsOn: true);
                PillMotion.Enter(child, scale: false, "smooth", animationsOn: true);  // the element is reused before its fade ends
                PumpFor(TimeSpan.FromMilliseconds(800));
                Assert.True(parent.Children.Contains(child));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Loops_StopOnUnloaded()
    {
        Sta.Run(() =>
        {
            var transform = new ScaleTransform(1, 1);
            var pulse = Pulse(transform);
            var owner = new Border();
            var window = new Window { Content = owner };
            Fixture.ShowOffScreen(window);
            try
            {
                Loops.Run(owner, pulse);
                PumpFor(TimeSpan.FromMilliseconds(100));
                Assert.True(transform.HasAnimatedProperties, "the loop runs while its owner is on screen");

                window.Content = null;  // the owner leaves the tree
                PumpFor(TimeSpan.FromMilliseconds(50));
                Assert.False(transform.HasAnimatedProperties, "the loop stops on Unloaded");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Loops_DesiredFrameRate30()
    {
        Sta.Run(() =>
        {
            var pulse = Pulse(new ScaleTransform(1, 1));

            Loops.Run(new Border(), pulse);

            Assert.Equal(30, Timeline.GetDesiredFrameRate(pulse));
            Assert.Equal(RepeatBehavior.Forever, pulse.RepeatBehavior);
        });
    }

    [Fact]
    public void Chips_RemovedChip_FadesThenGoes()
    {
        Sta.Run(() =>
        {
            var (left, right, host) = ChipHost();
            var window = new Window { Content = host };
            Fixture.ShowOffScreen(window);
            try
            {
                var both = new[] { new Chip(ChipKind.Timer, "timer", "a"), new Chip(ChipKind.Timer, "timer", "b") };
                Fill(left, right, both);
                var full = ChipPanels(left, right).Count;
                var settledOne = SettledCount(new[] { new Chip(ChipKind.Timer, "timer", "a") });

                Fill(left, right, new[] { new Chip(ChipKind.Timer, "timer", "a") });
                Assert.Equal(full, ChipPanels(left, right).Count);  // the removed chip is still there, fading

                Assert.True(WaitUntil(() => ChipPanels(left, right).Count == settledOne, TimeSpan.FromSeconds(3)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Chips_InPlaceUpdate_DoesNotEnter()
    {
        Sta.Run(() =>
        {
            var (left, right, host) = ChipHost();
            var window = new Window { Content = host };
            Fixture.ShowOffScreen(window);
            try
            {
                var chip = new Chip(ChipKind.Timer, "timer", "a");
                Fill(left, right, new[] { chip });
                PumpFor(TimeSpan.FromMilliseconds(500));  // the first Enter has finished

                Fill(left, right, new[] { chip });  // the same chip, updated in place

                var panels = ChipPanels(left, right);
                Assert.NotEmpty(panels);
                Assert.All(panels, p => Assert.Equal(1.0, p.Opacity));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Badges_NeverAnimate()
    {
        Sta.Run(() =>
        {
            var (left, right, host) = ChipHost();
            var window = new Window { Content = host };
            Fixture.ShowOffScreen(window);
            try
            {
                var badge = new Border { Width = 16, Height = 16 };  // an installed badge: not a chip, so no Tag
                left.Children.Add(badge);

                Fill(left, right, new[] { new Chip(ChipKind.Timer, "timer", "a") });
                Fill(left, right, Array.Empty<Chip>());
                PumpFor(TimeSpan.FromMilliseconds(500));

                Assert.Contains(badge, left.Children.OfType<UIElement>());
                Assert.Equal(1.0, badge.Opacity);
                Assert.Null(badge.RenderTransform as ScaleTransform);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Enter_Completed_LocalOpacityIsNotPinned()
    {
        Sta.Run(() =>
        {
            var e = new Border();
            var window = new Window { Content = e };
            Fixture.ShowOffScreen(window);
            try
            {
                PillMotion.Enter(e, scale: true, "smooth", animationsOn: true);
                PumpFor(TimeSpan.FromMilliseconds(800));
                Assert.Equal(1.0, e.Opacity);

                e.Opacity = 0.5;  // a later local set takes effect: the Enter animation is no longer held
                Assert.Equal(0.5, e.Opacity);
                var scale = Assert.IsType<ScaleTransform>(e.RenderTransform);
                scale.ScaleX = 0.7;
                Assert.Equal(0.7, scale.ScaleX);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Enter_ForeignRenderTransform_NotOverwritten()
    {
        Sta.Run(() =>
        {
            var e = new Border();
            var rotate = new RotateTransform(30);
            e.RenderTransform = rotate;
            var window = new Window { Content = e };
            Fixture.ShowOffScreen(window);
            try
            {
                PillMotion.Enter(e, scale: true, "smooth", animationsOn: true);
                Assert.Same(rotate, e.RenderTransform);  // the element's own transform is kept: it enters by opacity only
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Chips_InPlaceUpdateMidEnter_StaysBelowFull()
    {
        Sta.Run(() =>
        {
            var (left, right, host) = ChipHost();
            var window = new Window { Content = host };
            Fixture.ShowOffScreen(window);
            try
            {
                var chip = new Chip(ChipKind.Timer, "timer", "a");
                Fill(left, right, new[] { chip });
                PumpFor(TimeSpan.FromMilliseconds(100));  // 100 ms into the 350 ms enter

                Fill(left, right, new[] { chip });  // the same chip, updated in place by the next publish

                var panels = ChipPanels(left, right);
                Assert.NotEmpty(panels);
                Assert.All(panels, p => Assert.True(p.Opacity < 1.0, $"opacity was {p.Opacity}: the enter restarted or was dropped"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// A ScaleY animation the loop tests run: a pulse on the transform, over a second, repeated by Loops.Run.
    private static DoubleAnimation Pulse(ScaleTransform transform)
    {
        var animation = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(1)) { AutoReverse = true };
        Loops.Aim(animation, transform, ScaleTransform.ScaleYProperty);
        return animation;
    }

    private static (StackPanel Left, StackPanel Right, StackPanel Host) ChipHost()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(left);
        host.Children.Add(right);
        return (left, right, host);
    }

    private static void Fill(StackPanel left, StackPanel right, IReadOnlyList<Chip> chips) =>
        ChipView.Fill(left, right, chips, new Dictionary<string, string>(), new Dictionary<string, string>(), null, DateTimeOffset.Now,
            null, motion: "smooth", animationsOn: true);

    /// The chip panels in both wings (the ones ChipView tags).
    private static List<FrameworkElement> ChipPanels(StackPanel left, StackPanel right) =>
        left.Children.OfType<FrameworkElement>().Concat(right.Children.OfType<FrameworkElement>())
            .Where(e => e.Tag is "chip").ToList();

    /// How many chip panels a fresh fill of these chips makes, in its own wings (a settled count to compare with).
    private static int SettledCount(IReadOnlyList<Chip> chips)
    {
        var (left, right, host) = ChipHost();
        var window = new Window { Content = host };
        Fixture.ShowOffScreen(window);
        try
        {
            Fill(left, right, chips);
            return ChipPanels(left, right).Count;
        }
        finally
        {
            window.Close();
        }
    }

    /// Runs the dispatcher in short spans until the condition holds or the limit passes.
    private static bool WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (!condition() && DateTime.UtcNow < end)
        {
            PumpFor(TimeSpan.FromMilliseconds(16));
        }
        return condition();
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
