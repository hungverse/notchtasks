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

    private int _wheelRemainder;  // the wheel delta not yet a whole notch

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

    /// The row's delta for a horizontal wheel turn. WM_MOUSEHWHEEL is positive to the right, while ScrollNotches is positive to the
    /// left (as Shift+wheel is), so the sign is flipped.
    public static int FromHorizontalWheel(int delta) => -delta;

    /// Scrolls the row sideways by a wheel delta: a positive delta (wheel away) moves left, as Shift+wheel does on Windows. Deltas add
    /// up: a touchpad sends many small ones, and the row moves three lines per whole notch (120) accumulated, the rest waiting for the
    /// next turn.
    public void ScrollNotches(int delta)
    {
        _wheelRemainder += delta;
        var notches = _wheelRemainder / NotchDelta;
        _wheelRemainder %= NotchDelta;
        if (notches == 0)
        {
            return;
        }
        var lines = Math.Abs(notches) * LinesPerNotch;
        for (var i = 0; i < lines; i++)
        {
            if (notches > 0)
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
