using System.Windows;

namespace NotchTasks.Pill;

/// The pill's hit geometry: its rounded rectangles in the pill's DIP space (the window's client area, unscaled).
internal static class PillHitTest
{
    /// Signed distance from p to the rounded rectangle r with the given corner radius: negative or zero inside it.
    public static double DistanceToShape(Point p, Rect r, double radius)
    {
        var x = r.X;
        var width = r.Width;
        var height = r.Height;
        var cr = Math.Min(radius, Math.Min(width, height) / 2);
        var cx = Span(p.X, x + cr, x + width - cr);
        var cy = Span(p.Y, r.Y + cr, r.Y + height - cr);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return Math.Sqrt(dx * dx + dy * dy) - cr;
    }

    /// v held between lo and hi. Unlike Math.Clamp this never throws: a shape narrower than its corners (the idle pill) has hi a
    /// rounding error below lo, and the point then sits on the bound it is nearer to.
    private static double Span(double v, double lo, double hi) => Math.Min(Math.Max(v, lo), Math.Max(lo, hi));

    /// True when p lies in the band `gap` high just above the rectangle (x, y, w, h), within its width. The gap above a
    /// floating pill still counts, so the screen's top edge opens it (the Mac's islandTop gap).
    public static bool InGapAbove(Point p, double x, double y, double w, double gap) =>
        p.Y >= y - gap && p.Y < y && p.X >= x && p.X <= x + w;

    /// True when p lies in the rounded rectangle (x, y, w, h) with the given corner radius.
    public static bool InRoundedRect(Point p, double x, double y, double w, double h, double radius)
    {
        if (p.X < x || p.X > x + w || p.Y < y || p.Y > y + h)
        {
            return false;
        }
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        var cx = Span(p.X, x + r, x + w - r);
        var cy = Span(p.Y, y + r, y + h - r);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return dx * dx + dy * dy <= r * r;
    }
}
