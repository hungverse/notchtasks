using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;

namespace NotchTasks.Pill.Sections;

/// The device toast's art (the Mac's DeviceArtView): the accessory's symbol in the toast's lead slot, at the slot's size. A connected
/// device rises (offset a quarter of the size → 0, scale 0.6 → 1, opacity 0 → 1) on the motion's spring, a ripple sits behind it, and it
/// floats up and down by 1.5 px (at 40 px; proportional at other sizes) every 1.6 s. A disconnected device turns grey and sinks (offset
/// 0 → 0.15 of the size, scale 1 → 0.85, opacity 1 → 0.45). Under reduced motion, or with animations off in Windows, the final values
/// show at once and nothing moves. The art is made by ToastView.Show, for the toast's life.
public sealed class DeviceArtView : Grid
{
    private const double ReferenceSize = 40;     // the art's size the proportions are drawn at (the Mac's 40 px)
    private const double FloatAt40 = 1.5;        // the float's amplitude at 40 px
    private const double FloatHalfPeriod = 0.8;  // seconds up or down: the float repeats every 1.6 s

    private static readonly Color GreyColor = Color.FromRgb(0x8E, 0x8E, 0x93);
    private static readonly SolidColorBrush GreyBrush = Frozen(new SolidColorBrush(GreyColor));
    private static readonly FontFamily SegoeFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// The spring's position, 0 (the start) to 1 (the settled look).
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DeviceArtView), new PropertyMetadata(1.0, OnChanged));

    /// The float's offset in pixels, animated by the loop of a connected device.
    public static readonly DependencyProperty FloatProperty = DependencyProperty.Register(
        nameof(Float), typeof(double), typeof(DeviceArtView), new PropertyMetadata(0.0, OnChanged));

    private readonly double _size;
    private readonly bool _connected;
    private readonly FrameworkElement _glyph;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();

    /// The art for a toast in its tint (the toast's own colour), at a size (the lead slot's font size).
    public DeviceArtView(DeviceArt art, Brush tint, string motion, double size)
    {
        _size = size;
        _connected = art.Connected;
        Width = size;
        Height = size;
        Focusable = false;
        IsHitTestVisible = false;

        var brush = art.Connected ? tint : GreyBrush;
        _glyph = Glyph(art.Symbol, brush, size);
        _glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        _glyph.RenderTransform = new TransformGroup { Children = { _scale, _translate } };

        var animated = SystemParameters.ClientAreaAnimation && motion != "reduced";
        if (animated && art.Connected)
        {
            Children.Add(Loops.Ripple(tint, size));
        }
        Children.Add(_glyph);

        if (!animated)
        {
            Apply();
            return;
        }
        Progress = 0;
        Apply();
        var curve = SpringCurve.For(motion, opening: true, animationsOn: true);
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(curve.Settle))
        {
            EasingFunction = new SpringEase(curve),
        });
        if (art.Connected)
        {
            // 0 → up → 0 → down → 0, each leg eased: the float starts at rest, so the rise hands over without a jump.
            var amplitude = FloatAt40 * size / ReferenceSize;
            var half = TimeSpan.FromSeconds(FloatHalfPeriod);
            var bob = new DoubleAnimationUsingKeyFrames { Duration = new Duration(half * 2) };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero), ease));
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(amplitude, KeyTime.FromTimeSpan(half / 2), ease));
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(half), ease));
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(-amplitude, KeyTime.FromTimeSpan(half + half / 2), ease));
            bob.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(half * 2), ease));
            Loops.Run(this, this, FloatProperty, bob);
        }
    }

    /// The spring's position, 0 to 1.
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// The float's offset, in pixels.
    public double Float
    {
        get => (double)GetValue(FloatProperty);
        set => SetValue(FloatProperty, value);
    }

    /// The glyph's look at the spring's position and the float's offset.
    private void Apply()
    {
        var p = Progress;  // not clamped: the spring overshoots and the scale and offset follow it; only the opacity is clamped
        double offset;
        double scale;
        double opacity;
        if (_connected)
        {
            offset = _size * 0.25 * (1 - p) + Float;
            scale = 0.6 + 0.4 * p;
            opacity = p;
        }
        else
        {
            offset = _size * 0.15 * p;
            scale = 1 - 0.15 * p;
            opacity = 1 - 0.55 * p;
        }
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;
        _translate.Y = offset;
        _glyph.Opacity = Math.Clamp(opacity, 0, 1);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DeviceArtView)d).Apply();

    /// A device's symbol in a brush at a size: the drawn path for a name the font lacks, else the Segoe Fluent glyph.
    public static FrameworkElement Glyph(string symbol, Brush brush, double size) =>
        ModuleGlyphs.Vector(symbol) is { } data
            ? new Path
            {
                Data = Geometry.Parse(data),
                Stroke = brush,
                StrokeThickness = 1.6,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            }
            : new TextBlock
            {
                Text = ModuleGlyphs.Get(symbol),
                Foreground = brush,
                FontFamily = SegoeFamily,
                FontSize = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
            };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
