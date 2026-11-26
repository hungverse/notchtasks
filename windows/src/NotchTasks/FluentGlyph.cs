using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NotchTasks;

/// One Fluent System Icons glyph, drawn as a path and centred in its box. A TextBlock sets a glyph on the font's baseline, so an
/// icon sits low or off-centre; this control measures the glyph's own outline and moves it so the outline's bounds are centred in
/// a Size by Size square, at the size asked for (Stretch None). The glyph is filled with Foreground, which inherits from the
/// parent (a pill badge's ink, a Control Panel tile's text colour), and is white by default.
///
///   <local:FluentGlyph Code="&#xF72A;" Size="12" />          a Regular glyph
///   <local:FluentGlyph Code="&#xF72A;" Size="12" Filled="True" />  the Filled twin
public sealed class FluentGlyph : FrameworkElement
{
    /// The codepoint of the glyph, as its one-character string (ModuleGlyphs and the Mac-symbol maps hold these).
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(FluentGlyph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((FluentGlyph)d).Rebuild()));

    /// The box the glyph is centred in, in DIP (a square).
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(FluentGlyph), new FrameworkPropertyMetadata(16.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((FluentGlyph)d).Rebuild()));

    /// True for the Filled font, false (the default) for the Regular one.
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(FluentGlyph), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FluentGlyph)d).Rebuild()));

    /// The fill of the glyph: the TextElement foreground, so a glyph inside a Button or a TextBlock takes its colour the way text does
    /// (a Button's template sets it), and is white when no ancestor sets one.
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(FluentGlyph), new FrameworkPropertyMetadata(Brushes.White,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly ConcurrentDictionary<(string Code, bool Filled, double Size), Geometry?> Cache = new();

    private Geometry? _geometry;

    public FluentGlyph()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;  // an icon is centred in its parent: a stretched element would move its drawing off the centre
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// The outline of the glyph, moved so its bounds are centred in the Size square (null for an empty code). Frozen and cached.
    public Geometry? Outline => _geometry;

    /// The glyph of a ModuleGlyphs name, in its own family (Filled when the name is a filled icon), at `size` DIP. The one way the pill
    /// and the Control Panel draw a named icon.
    public static FluentGlyph Named(string name, double size)
    {
        var glyph = new FluentGlyph { Size = size };
        glyph.SetNamed(name);
        return glyph;
    }

    /// The centred outline of a glyph at `size` DIP, from the font it names. Cached by (code, family, size); null when the code is
    /// empty or the font has no outline for it.
    public static Geometry? CentredOutline(string code, bool filled, double size) =>
        Cache.GetOrAdd((code ?? string.Empty, filled, size), key => Build(key.Code, key.Filled, key.Size));

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is not null)
        {
            drawingContext.DrawGeometry(Foreground, null, _geometry);
        }
    }

    /// Draws the named ModuleGlyphs icon into this glyph (its code and family), as the Control Panel and the pill name their icons.
    public void SetNamed(string name)
    {
        var (family, code) = NotchTasks.Pill.ModuleGlyphs.Get(name);
        Filled = ReferenceEquals(family, FluentIcons.Filled);
        Code = code;
    }

    private void Rebuild()
    {
        _geometry = CentredOutline(Code, Filled, Size);
        InvalidateVisual();
    }

    private static Geometry? Build(string code, bool filled, double size)
    {
        if (string.IsNullOrEmpty(code) || size <= 0)
        {
            return null;
        }
        var family = filled ? FluentIcons.Filled : FluentIcons.Regular;
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        // The em box is the size itself: the glyph is drawn at the size the caller gave, then centred in that square.
        var text = new FormattedText(code, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
        var outline = text.BuildGeometry(new Point(0, 0));
        var bounds = outline.Bounds;
        if (bounds.IsEmpty)
        {
            return null;
        }
        // The outline is moved so the centre of its bounds is the centre of the Size square.
        outline.Transform = new TranslateTransform(size / 2 - (bounds.X + bounds.Width / 2), size / 2 - (bounds.Y + bounds.Height / 2));
        outline.Freeze();
        return outline;
    }
}
