namespace NotchTasks.Core.Motion;

/// Pill sizes (DIP) and where the pill window sits. The window is sized once; the pill animates inside it.
public static class PillGeometry
{
    public const double ClosedWidth = 200;
    public const double ClosedHeight = 32;
    public const double ClosedRadius = 16;
    public const double OpenWidth = 520;
    public const double OpenHeight = 220;
    public const double OpenRadius = 28;

    /// The ask toast width: max(200 + 300, 520).
    public static double AskToastWidth { get; } = Math.Max(ClosedWidth + 300, OpenWidth);

    /// The window: big enough for the open card and the glow, never resized.
    public const double WindowWidth = 560;
    public const double WindowHeight = 420;

    /// Top centre of the work area, in whole device pixels. The size is the window's DIP size times the DPI scale.
    public static (int X, int Y, int W, int H) Place((int X, int Y, int W, int H) workAreaPx, double windowWidthDip, double windowHeightDip, double scale)
    {
        var w = (int)Math.Round(windowWidthDip * scale, MidpointRounding.AwayFromZero);
        var h = (int)Math.Round(windowHeightDip * scale, MidpointRounding.AwayFromZero);
        var x = workAreaPx.X + (workAreaPx.W - w) / 2;
        return (x, workAreaPx.Y, w, h);
    }
}
