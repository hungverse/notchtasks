using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NotchTasks;

/// One Fluent System Icons glyph, drawn as a path and centred. A TextBlock sets a glyph on the font's baseline, so an icon sits
/// low or off-centre; this control moves the glyph's own outline so its bounds are centred in the box it is arranged in (its
/// RenderSize), with the glyph at Size DIP. The fill is Foreground, which is the TextElement foreground: a glyph inside a Button
/// or a TextBlock takes that colour as text does, and is white when no ancestor sets one.
///
/// The control draws only; it is not a hit target. A click, cursor or tooltip belongs to a Border or Button around it.
///
///   <fi:FluentGlyph Code="&#xF72A;" Size="12" />                    a Regular glyph
///   <fi:FluentGlyph Code="&#xF72A;" Size="12" Filled="True" />      the Filled twin
public sealed class FluentGlyph : FrameworkElement
{
    /// The codepoint of the glyph, as its one-character string (ModuleGlyphs and the Mac-symbol maps hold these).
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(FluentGlyph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((FluentGlyph)d).Rebuild()));

    /// The em size of the glyph, in DIP: the box it is centred in is this size, and the arranged box may be larger.
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(FluentGlyph), new FrameworkPropertyMetadata(16.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((FluentGlyph)d).Rebuild()));

    /// True for the Filled font, false (the default) for the Regular one.
    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(FluentGlyph), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FluentGlyph)d).Rebuild()));

    /// The fill of the glyph: the TextElement foreground (white when no ancestor sets one).
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(FluentGlyph), new FrameworkPropertyMetadata(Brushes.White,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly ConcurrentDictionary<(string Code, bool Filled, double Size), Geometry> Cache = new();

    private Geometry? _geometry;

    public FluentGlyph()
    {
        HorizontalAlignment = HorizontalAlignment.Center;  // an icon is centred in its parent by default
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// Takes its colour from a theme resource that follows the theme live (a one-time FindResource would keep the first theme's brush).
    public FluentGlyph ThemedBy(string resourceKey)
    {
        SetResourceReference(ForegroundProperty, resourceKey);
        return this;
    }

    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// The outline of the glyph, centred in a Size square at the origin (null for an empty code). Frozen and cached.
    public Geometry? Outline => _geometry;

    /// Where the glyph is drawn inside this element: the outline's bounds, moved by the offset that centres the Size square in
    /// the arranged box (RenderSize). Empty when there is no glyph.
    public Rect DrawnBounds
    {
        get
        {
            if (_geometry is null)
            {
                return Rect.Empty;
            }
            var offset = Offset();
            var bounds = _geometry.Bounds;
            return new Rect(bounds.X + offset.X, bounds.Y + offset.Y, bounds.Width, bounds.Height);
        }
    }

    /// The centred outline of a glyph at `size` DIP, from the font it names. Cached by (code, family, size); null when the code is
    /// empty or the font has no outline for it (a failed build is not cached, so a later call can try again).
    public static Geometry? CentredOutline(string code, bool filled, double size)
    {
        if (string.IsNullOrEmpty(code) || size <= 0)
        {
            return null;
        }
        var key = (code, filled, size);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var built = Build(code, filled, size);
        if (built is not null)
        {
            Cache[key] = built;
        }
        return built;
    }

    /// The glyph of a ModuleGlyphs name, in its own family (Filled when the name is a filled icon), at `size` DIP.
    public static FluentGlyph Named(string name, double size)
    {
        var glyph = new FluentGlyph { Size = size };
        glyph.SetNamed(name);
        return glyph;
    }

    /// Draws the named ModuleGlyphs icon into this glyph (its code and family).
    public void SetNamed(string name)
    {
        var (family, code) = NotchTasks.Pill.ModuleGlyphs.Get(name);
        Filled = ReferenceEquals(family, FluentIcons.Filled);
        Code = code;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_geometry is null)
        {
            return;
        }
        var offset = Offset();
        drawingContext.PushTransform(new TranslateTransform(offset.X, offset.Y));
        drawingContext.DrawGeometry(Foreground, null, _geometry);
        drawingContext.Pop();
    }

    /// The offset of the Size square inside the arranged box: half of the spare room on each axis.
    private Vector Offset() => new((RenderSize.Width - Size) / 2, (RenderSize.Height - Size) / 2);

    private void Rebuild()
    {
        _geometry = CentredOutline(Code, Filled, Size);
        InvalidateVisual();
    }

    private static Geometry? Build(string code, bool filled, double size)
    {
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
