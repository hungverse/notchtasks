using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NotchTasks.Pill;

/// The horizontal row of a pill section (the shelf's tiles, the devices' capsules), in a scroller whose bar is hidden. A plain
/// ScrollViewer marks every wheel turn handled, so the rows would swallow the wheel the section list needs to scroll. This one
/// takes the wheel only with Shift held (three lines per notch, sideways); any other turn is left unhandled, so it bubbles on to
/// the pill's section scroller. PillWindow routes a horizontal wheel (touchpad swipe) to the row under the pointer through
/// ScrollNotches.
public sealed class RowScroller : ScrollViewer
{
    /// Lines moved per wheel notch, as the Windows list boxes do.
    public const int LinesPerNotch = 3;

    /// One wheel notch in WHEEL_DELTA units.
    public const int NotchDelta = 120;

    public RowScroller()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // The base class is not called: it would mark the turn handled whatever the modifiers.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ScrollNotches(e.Delta);
            e.Handled = true;
        }
    }

    /// Scrolls the row sideways by a wheel delta: a positive delta (wheel away) moves left, as Shift+wheel does on Windows.
    public void ScrollNotches(int delta)
    {
        if (delta == 0)
        {
            return;
        }
        var steps = Math.Max(1, Math.Abs(delta) / NotchDelta) * LinesPerNotch;
        for (var i = 0; i < steps; i++)
        {
            if (delta > 0)
            {
                LineLeft();
            }
            else
            {
                LineRight();
            }
        }
    }
}
