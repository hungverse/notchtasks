using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NotchTasks.Pill.Sections;

/// The lead of a job from its symbol (the Mac's job symbol names): a drawn path or a Fluent glyph, in one brush. Shared by a job
/// row in the agent and Build & CI cards and by a build chip, so both draw a symbol the same way.
public static class JobLead
{
    private static readonly FontFamily GlyphFamily = FluentIcons.Regular;

    /// The glyph or vector name of a job's symbol, or null for none (a plain status dot). A symbol that has its own drawn path is
    /// named by itself (hammer.fill, checkmark.diamond.fill, server.rack, ...); else the keyword rules: a checkmark, sparkles, a
    /// gauge or an hourglass.
    public static string? MarkOf(string? symbol)
    {
        if (symbol is null)
        {
            return null;
        }
        if (ModuleGlyphs.Vector(symbol) is not null)
        {
            return symbol;
        }
        if (symbol.Contains("checkmark", StringComparison.OrdinalIgnoreCase))
        {
            return "check";
        }
        if (symbol.Contains("sparkles", StringComparison.OrdinalIgnoreCase))
        {
            return "sparkles";
        }
        if (symbol.StartsWith("gauge", StringComparison.OrdinalIgnoreCase))
        {
            return "gauge";
        }
        return symbol.Equals("hourglass", StringComparison.OrdinalIgnoreCase) ? "hourglass" : null;
    }

    /// The symbol's lead of size by size in the brush, or null when the symbol has no mark.
    public static FrameworkElement? Element(string? symbol, double size, Brush brush)
    {
        var mark = MarkOf(symbol);
        if (mark is null)
        {
            return null;
        }
        if (ModuleGlyphs.Vector(mark) is { } path)
        {
            return new Path
            {
                Data = Geometry.Parse(path),
                Stroke = brush,
                StrokeThickness = 1.4,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };
        }
        return new TextBlock
        {
            Text = ModuleGlyphs.Get(mark),
            FontFamily = GlyphFamily,
            FontSize = size,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
        };
    }
}
