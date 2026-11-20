using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The rows of the open pill: a wheel turn on a tile or capsule bubbles on to the section list (only Shift scrolls the row), and
/// a Shift turn or a ScrollNotches moves the row sideways.
public sealed class RowScrollerTests
{
    /// A row of 10 tiles, 80 DIP each (800 DIP of content in a 200 DIP row), inside an outer border that records the wheel. The
    /// row is shown in a window of its own (off screen, not activated) so that its scroll host is laid out.
    internal static (Window Window, RowScroller Row, Border Tile, Func<bool> Reached) Build()
    {
        var tiles = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < 10; i++)
        {
            tiles.Children.Add(new Border { Width = 80, Height = 40 });
        }
        var tile = (Border)tiles.Children[0];
        var row = new RowScroller { Content = tiles, Width = 200, Height = 60 };
        var reached = false;
        var outer = new Border { Child = row };
        outer.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, _) => reached = true));
        var window = new Window
        {
            Content = outer,
            Width = 200,
            Height = 60,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -10_000,
            Top = -10_000,
        };
        window.Show();
        window.UpdateLayout();
        return (window, row, tile, () => reached);
    }

    [Fact]
    public void WheelOnTile_IsNotHandledByTheRow_AndBubblesOn()
    {
        Sta.Run(() =>
        {
            var (window, row, tile, reached) = Build();
            try
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = tile,
                };
                tile.RaiseEvent(args);

                Assert.True(reached());
                Assert.False(args.Handled);
                Assert.Equal(0, row.HorizontalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ScrollNotches_MovesTheRowThreeLinesPerNotch()
    {
        Sta.Run(() =>
        {
            var (window, row, _, _) = Build();
            try
            {
                Assert.True(row.ScrollableWidth > 0, $"extent {row.ExtentWidth}, viewport {row.ViewportWidth}");

                row.ScrollNotches(-120);
                window.UpdateLayout();  // the offset is applied on the next layout pass
                Assert.Equal(3 * 16, row.HorizontalOffset);  // LineRight three times, the default 16 DIP line

                row.ScrollNotches(120);
                window.UpdateLayout();
                Assert.Equal(0, row.HorizontalOffset);       // LineLeft three times, back to the start
            }
            finally
            {
                window.Close();
            }
        });
    }
}

/// The wheel: small deltas add up (a touchpad sends many small ones), and a horizontal wheel turn to the right scrolls right.
public sealed class RowScrollerWheelTests
{
    [Fact]
    public void SmallDeltas_AccumulateToThreeLinesPerNotch()
    {
        Sta.Run(() =>
        {
            var (window, row, _, _) = BuildRow();
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    row.ScrollNotches(-30);  // a quarter notch each
                }
                window.UpdateLayout();
                Assert.Equal(0, row.HorizontalOffset);  // 90 accumulated: not yet a notch

                row.ScrollNotches(-30);  // 120 accumulated: one notch, three lines
                window.UpdateLayout();
                Assert.Equal(3 * 16, row.HorizontalOffset);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HorizontalWheelToTheRight_ScrollsRight()
    {
        Sta.Run(() =>
        {
            var (window, row, _, _) = BuildRow();
            try
            {
                // A horizontal wheel turn to the right is a positive delta; RowScroller maps it to scrolling right.
                row.ScrollNotches(RowScroller.FromHorizontalWheel(120));
                window.UpdateLayout();
                Assert.True(row.HorizontalOffset > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (Window Window, RowScroller Row, Border Tile, Func<bool> Reached) BuildRow() => RowScrollerTests.Build();
}
