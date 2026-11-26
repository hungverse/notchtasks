namespace NotchTasks.Core.Motion;

/// Pill sizes (DIP) and where the pill window sits. The window is sized once; the pill animates inside it.
public static class PillGeometry
{
    /// The closed pill's width while it is idle: today's 200 DIP strip, centred. It is only the hover area then (the pill is
    /// invisible); a closed pill with content is as wide as its content (ClosedContentWidth).
    public const double ClosedWidth = 200;
    public const double ClosedHeight = 32;
    public const double ClosedRadius = 16;
    public const double OpenWidth = 520;
    public const double OpenRadius = 28;

    /// The open pill grows with its ask cards up to this height (the window is this high plus HostMargin).
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

    /// The padding of a closed pill outside its content: the Mac's max((height - icon) / 2, 6), so one icon sits in a circle.
    public static double ClosedPadding(double iconSize) => Math.Max((ClosedHeight - iconSize) / 2, 6);

    /// The icon size of the closed pill, as the Mac's Settings.swift: 16 in the compact size, else 20.
    public static double IconSizeFor(string size) => size == "compact" ? 16 : 20;

    /// The gap between the two wings: none when one wing is empty; else 5 for the single chip (its lead and trail), 10 otherwise.
    public static double WingGap(double leftWidth, double rightWidth, bool single) =>
        leftWidth > 0 && rightWidth > 0 ? (single ? 5 : 10) : 0;

    /// The width of a closed pill that holds content: padding, the left wing, the gap, the right wing and padding. Never below
    /// ClosedHeight, so one icon gives a circle of ClosedHeight. Each wing is its ink width (its icons and text, no margins).
    public static double ClosedContentWidth(double leftWidth, double rightWidth, double iconSize, bool single) =>
        Math.Max(ClosedHeight, 2 * ClosedPadding(iconSize) + leftWidth + rightWidth + WingGap(leftWidth, rightWidth, single));

    /// The width a closed pill shrinks to, and grows from, when it has nothing to show (it is invisible then).
    public static double IdleWidth(double iconSize) => 2 * ClosedPadding(iconSize);

    /// The left edge, in the layout, of a closed group `groupWidth` wide: the group is centred in the layout.
    public static double ClosedGroupLeft(double groupWidth) => (LayoutWidth - groupWidth) / 2;

    /// How far the pill floats below the top of the work area (DIP), as the Mac's islandTop does. The gap above it still
    /// counts for hover: the screen's top edge opens the pill.
    public const double IslandTop = 4;

    /// How far the glow reaches outside the ring's geometry: the glow's 8 DIP blur (BlurRadius), which the ring's own stroke half (1.2 DIP)
    /// is added to.
    public const double GlowReach = 10;

    /// The room the window keeps round the layout on the sides and at the bottom (not the top, which is the screen's edge). The
    /// bouncy open overshoots its target by 7.3 % (SpringCurve), up to 36 DIP for a 488 DIP grow; with the glow's reach and the
    /// ring's half stroke that is 47 DIP, so 48 holds every step of every motion (the test checks it).
    public const double HostMargin = 48;

    /// The window: the layout with HostMargin round it on both sides, and HostMargin under the open height. It is never resized.
    public static double WindowWidth => LayoutWidth + 2 * HostMargin;
    public static double WindowHeight => MaxOpenHeight + HostMargin;

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
