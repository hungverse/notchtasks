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
        var cx = Math.Clamp(p.X, x + cr, x + width - cr);
        var cy = Math.Clamp(p.Y, r.Y + cr, r.Y + height - cr);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return Math.Sqrt(dx * dx + dy * dy) - cr;
    }

    /// True when p lies in the rounded rectangle (x, y, w, h) with the given corner radius.
    public static bool InRoundedRect(Point p, double x, double y, double w, double h, double radius)
    {
        if (p.X < x || p.X > x + w || p.Y < y || p.Y > y + h)
        {
            return false;
        }
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        var cx = Math.Clamp(p.X, x + r, x + w - r);
        var cy = Math.Clamp(p.Y, y + r, y + h - r);
        var dx = p.X - cx;
        var dy = p.Y - cy;
        return dx * dx + dy * dy <= r * r;
    }
}
