using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Levels;

namespace NotchTasks.Pill;

/// The volume or brightness bar the pill shows while a level changes (the Mac's levelView). Show sets the symbol of the level, the fill at its
/// share of the track, and the percentage, or "Off" with an empty fill when muted. The pill sizes its shape to Span, so the bar sits on the
/// notch's line as the toast does.
public partial class LevelView : UserControl
{
    /// The bar's width: the symbol, the notch's gap and the bar with its figure, and the 14 DIP sides (the XAML's Width).
    public const double Span = 248;

    /// The muted track's fill: grey, as the Mac's bar (Color.gray) when the output is muted.
    private static readonly Brush MutedFill = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));

    private static readonly Brush LoudFill = Brushes.White;

    public LevelView()
    {
        InitializeComponent();
    }

    /// Shows a bar: the level's symbol, the fill and the figure.
    public void Show(LevelHud hud)
    {
        var (family, glyph) = ModuleGlyphs.Get(LevelsModule.SymbolName(hud));
        IconText.FontFamily = family;
        IconText.Text = glyph;

        var value = Math.Clamp(hud.Value, 0, 1);
        var shown = hud.Muted ? 0 : value;
        FillColumn.Width = new GridLength(shown, GridUnitType.Star);
        RestColumn.Width = new GridLength(1 - shown, GridUnitType.Star);
        Fill.Background = hud.Muted ? MutedFill : LoudFill;

        PercentText.Text = hud.Muted
            ? L10n.L("Off")
            : Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    private static Brush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
