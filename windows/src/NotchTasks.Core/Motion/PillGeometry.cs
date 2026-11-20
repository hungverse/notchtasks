namespace NotchTasks.Core.Motion;

/// Pill sizes (DIP) and where the pill window sits. The window is sized once; the pill animates inside it.
public static class PillGeometry
{
    public const double ClosedWidth = 200;
    public const double ClosedHeight = 32;
    public const double ClosedRadius = 16;
    public const double OpenWidth = 520;
    public const double OpenRadius = 28;

    /// The open pill grows with its ask cards up to this height (the window is 420 high).
    public const double MaxOpenHeight = 400;

    /// The ask toast width: max(200 + 300, 520).
    public static double AskToastWidth { get; } = Math.Max(ClosedWidth + 300, OpenWidth);

    /// The width of the pill's layout: every shape is centred in it, and it never changes while the pill moves.
    public static double LayoutWidth { get; } = Math.Max(OpenWidth, AskToastWidth);

    /// The corner radius of the ask toast.
    public const double AskToastRadius = 24;

    /// The shape's rectangle inside the layout (x, y, w, h): a shape `w` wide sits centred in LayoutWidth, at the top.
    public static (double X, double Y, double W, double H) ShapeRect(double w, double h) => ((LayoutWidth - w) / 2, 0, w, h);

    /// How far the wings move in from the layout's edges for a shape `w` wide: the left wing by +offset, the right by -offset.
    public static double WingOffset(double w) => (LayoutWidth - w) / 2;

    /// How far the pill floats below the top of the work area (DIP), as the Mac's islandTop does. The gap above it still
    /// counts for hover: the screen's top edge opens the pill.
    public const double IslandTop = 4;

    /// The window: big enough for the open card and the glow, never resized.
    public const double WindowWidth = 560;
    public const double WindowHeight = 420;

    /// Top centre of the work area, in whole device pixels, IslandTop below its top. The size is the window's DIP size times the DPI scale.
    public static (int X, int Y, int W, int H) Place((int X, int Y, int W, int H) workAreaPx, double windowWidthDip, double windowHeightDip, double scale)
    {
        var w = (int)Math.Round(windowWidthDip * scale, MidpointRounding.AwayFromZero);
        var h = (int)Math.Round(windowHeightDip * scale, MidpointRounding.AwayFromZero);
        var x = workAreaPx.X + (workAreaPx.W - w) / 2;
        var y = workAreaPx.Y + (int)Math.Round(IslandTop * scale, MidpointRounding.AwayFromZero);
        return (x, y, w, h);
    }
}
