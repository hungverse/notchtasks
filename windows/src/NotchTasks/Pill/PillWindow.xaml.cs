using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Settings;

namespace NotchTasks.Pill;

/// The floating pill at the top centre of the primary screen. It never takes focus. The window is sized once; the
/// pill inside it animates. The window is click-through (WS_EX_TRANSPARENT|WS_EX_LAYERED) while the pointer is outside
/// the pill, so clicks reach the windows below; a 15 Hz poll of the pointer drives that, hover-open and the close check.
public partial class PillWindow : Window
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 15);
    private static readonly TimeSpan MaxFadeDuration = TimeSpan.FromMilliseconds(350);

    /// The corner radius of the pill; animated by the spring, bound to every shape through RadiusConverter.
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(PillWindow),
        new PropertyMetadata(PillGeometry.ClosedRadius, (d, _) => ((PillWindow)d).UpdateClip()));

    private readonly AppState _state;
    private readonly DispatcherTimer _pollTimer = new() { Interval = PollInterval };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly BlurEffect _cardBlur = new() { Radius = 6 };
    private nint _hwnd;
    private bool _open;
    private bool _closing;
    private bool _clickThrough;
    private long? _outsideSince;
    private long? _enteredAt;
    private string _badgesJson = string.Empty;
    private bool _hasCollapsed;
    private Color _accent = Color.FromRgb(0x32, 0xD2, 0xFF);

    /// Creates the pill for the app state. Call Show next (the window is placed when its handle exists).
    public PillWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        CardText.Text = L10n.L("No running tasks");
        CardLayer.Effect = _cardBlur;

        _pollTimer.Tick += OnPoll;
        _closeTimer.Tick += OnCloseTick;
        PillShape.MouseLeftButtonUp += OnPillClick;
        PillShape.SizeChanged += (_, _) => UpdateClip();

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _state.PropertyChanged += OnStateChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Refresh();
    }

    /// The corner radius of the pill, animated.
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private double Scale => _hwnd == nint.Zero ? 1 : Win32.GetDpiForWindow(_hwnd) / 96.0;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Place();
        _pollTimer.Start();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
                // Backup for the poll: the pointer outside the pill is HTTRANSPARENT.
                handled = true;
                return IsOverPointer(lParam) ? Win32.HTCLIENT : Win32.HTTRANSPARENT;
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return Win32.MA_NOACTIVATE;
            case Win32.WM_DPICHANGED:
                // WPF moves the window to the suggested rectangle first; then the pill is placed again at the new DPI.
                Dispatcher.BeginInvoke(new Action(Place));
                break;
            case Win32.WM_SETTINGCHANGE:
                // Only the work area matters here. The string form is read now, while the message's memory is valid.
                if (wParam == Win32.SPI_SETWORKAREA || (lParam != nint.Zero && Marshal.PtrToStringUni(lParam) == "WindowMetrics"))
                {
                    Dispatcher.BeginInvoke(new Action(Place));
                }
                break;
        }
        return nint.Zero;
    }

    /// Top centre of the primary work area, in device pixels. The window is never resized.
    private void Place()
    {
        if (_hwnd == nint.Zero || !Win32.SystemParametersInfo(Win32.SPI_GETWORKAREA, 0, out var area, 0))
        {
            return;
        }
        var (x, y, w, h) = PillGeometry.Place(
            (area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top),
            PillGeometry.WindowWidth, PillGeometry.WindowHeight, Scale);
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, x, y, w, h, Win32.SWP_NOACTIVATE);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Place));

    /// 15 Hz: sets click-through from the pointer's position, and opens the pill after HoverDelay over it (hover mode).
    private void OnPoll(object? sender, EventArgs e)
    {
        if (!Win32.GetCursorPos(out var point))
        {
            return;
        }
        var inside = IsInsidePill(point);
        SetClickThrough(!inside);
        if (!inside)
        {
            _enteredAt = null;
            return;
        }

        _enteredAt ??= Stopwatch.GetTimestamp();
        if (!_open && _state.Settings.ExpandMode == "hover"
            && Stopwatch.GetElapsedTime(_enteredAt.Value) >= TimeSpan.FromSeconds(_state.Settings.HoverDelay))
        {
            OpenPill();
        }
    }

    /// Toggles WS_EX_TRANSPARENT. The window is already layered (AllowsTransparency), so the bit alone makes the
    /// pointer pass through. Changes the style only on a transition.
    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough || _hwnd == nint.Zero)
        {
            return;
        }
        _clickThrough = on;
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        style = on ? style | Win32.WS_EX_TRANSPARENT : style & ~(nint)Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, style);
    }

    private void OnPillClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_open && _state.Settings.ExpandMode == "click")
        {
            OpenPill();
        }
    }

    /// Closes after the pointer has been outside the open shape for CloseDelay. Runs only while open.
    private void OnCloseTick(object? sender, EventArgs e)
    {
        if (!_open)
        {
            _closeTimer.Stop();
            return;
        }
        if (Win32.GetCursorPos(out var point) && IsInsidePill(point))
        {
            _outsideSince = null;
            return;
        }
        _outsideSince ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_outsideSince.Value) >= CloseDelay)
        {
            ClosePill();
        }
    }

    private void OpenPill()
    {
        if (_open)
        {
            return;
        }
        _open = true;
        _closing = false;  // a reopen cancels a pending hide of the closed pill
        _outsideSince = null;
        // Topmost is asserted again on every open; the window itself never activates.
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        ApplyAppearance();
        Animate(PillGeometry.OpenWidth, PillGeometry.OpenHeight, PillGeometry.OpenRadius, opening: true);
        _closeTimer.Start();
    }

    private void ClosePill()
    {
        if (!_open)
        {
            return;
        }
        _open = false;
        _closing = true;  // the pill stays visible until the close spring has finished
        _outsideSince = null;
        _closeTimer.Stop();
        ApplyAppearance();
        var widthAnimation = Animate(PillGeometry.ClosedWidth, PillGeometry.ClosedHeight, PillGeometry.ClosedRadius, opening: false);
        widthAnimation.Completed += (_, _) =>
        {
            if (!_open)
            {
                _closing = false;
                ApplyAppearance();
            }
        };
    }

    /// The Mac's spring for the shape. The card fades and un-blurs over min(Settle, 0.35 s), with opacity only when reduced.
    /// Returns the width animation (the one that finishes last on the way down).
    private DoubleAnimation Animate(double width, double height, double radius, bool opening)
    {
        var settings = _state.Settings;
        var animationsOn = SystemParameters.ClientAreaAnimation;
        var reduced = !animationsOn || settings.Motion == "reduced";
        var curve = SpringCurve.For(settings.Motion, opening, animationsOn);

        var widthAnimation = Spring(PillShape, FrameworkElement.WidthProperty, width, curve);
        Spring(PillShape, FrameworkElement.HeightProperty, height, curve);
        Spring(this, RadiusProperty, radius, curve);

        var fadeSeconds = Math.Min(curve.Settle, MaxFadeDuration.TotalSeconds);
        var fade = new SpringCurve(fadeSeconds, 1) { EaseInOut = true };
        var duration = new Duration(TimeSpan.FromSeconds(fadeSeconds));
        CardLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(opening ? 1 : 0, duration) { EasingFunction = new SpringEase(fade) });
        var blur = opening || reduced ? 0 : 6;
        _cardBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(blur, duration) { EasingFunction = new SpringEase(fade) });
        return widthAnimation;
    }

    private static DoubleAnimation Spring(IAnimatable target, DependencyProperty property, double to, SpringCurve curve)
    {
        var animation = new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(curve.Settle)))
        {
            EasingFunction = new SpringEase(curve),
        };
        target.BeginAnimation(property, animation);
        return animation;
    }

    /// Clips the card and the pill's layers to the animated rounded rectangle, so nothing shows past the shape mid-bounce.
    private void UpdateClip()
    {
        var w = PillShape.Width;
        var h = PillShape.Height;
        var r = Math.Min(Radius, Math.Min(w, h) / 2);
        PillContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    private bool IsOverPointer(nint lParam)
    {
        var point = new NativePoint
        {
            X = (short)(lParam.ToInt64() & 0xFFFF),
            Y = (short)((lParam.ToInt64() >> 16) & 0xFFFF),
        };
        return IsInsidePill(point);
    }

    /// Hit test in the animated geometry: the pill is centred at the top of the window, with the current size and radius.
    private bool IsInsidePill(NativePoint screenPoint)
    {
        var client = screenPoint;
        Win32.ScreenToClient(_hwnd, ref client);
        var scale = Scale;
        var p = new Point(client.X / scale, client.Y / scale);

        var width = PillShape.Width;
        var height = PillShape.Height;
        var x = (PillGeometry.WindowWidth - width) / 2;
        return InRoundedRect(p, x, 0, width, height, Radius);
    }

    private static bool InRoundedRect(Point p, double x, double y, double w, double h, double radius)
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

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Settings))
        {
            Refresh();
        }
    }

    /// Brings the look in line with the settings: badges, background, accent and motion (read again on the next open).
    private void Refresh()
    {
        var settings = _state.Settings;
        var badges = settings.Badges;
        var json = badges.ToJsonString();
        if (json != _badgesJson)
        {
            _badgesJson = json;
            _hasCollapsed = badges.Count > 0;
            RebuildBadges(badges);
        }

        var hex = Appearance.ResolveAccent(settings.Accent, settings.CustomAccent, SystemAccent.Hex()) ?? "#32D2FF";
        _accent = (Color)ColorConverter.ConvertFromString(hex)!;
        ApplyAppearance();
    }

    /// The surface, shadow, glow and visibility for the current state (open, closing, closed, idle).
    private void ApplyAppearance()
    {
        var settings = _state.Settings;
        // Idle (no badges, closed) hides the pill only once the close spring has finished; its hover area stays.
        PillShape.Opacity = _open || _closing || _hasCollapsed ? 1 : 0;

        // The background setting shows only while open; closed, the pill is black (Mac: opened ? background : .black).
        var background = _open ? settings.Background : "black";
        PillShape.Background = background switch
        {
            "glass" => Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1E))),
            "gradient" => Frozen(new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(settings.GradientA)!,
                (Color)ColorConverter.ConvertFromString(settings.GradientB)!,
                new Point(0, 0),
                new Point(1, 1))),
            _ => Brushes.Black,
        };
        Tint.Opacity = background switch
        {
            "glass" => 0.4,
            "gradient" => 0.3,
            _ => 0,
        };

        PillShape.Effect = _open
            ? Shadow(opacity: 0.45, blur: 14, depth: 6)
            : Shadow(opacity: 0.3, blur: 6, depth: 2);

        var glow = settings.Background == "glow" && (_open || _hasCollapsed);
        GlowRing.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        GlowRing.BorderBrush = Frozen(new SolidColorBrush(_accent) { Opacity = 0.9 });
        GlowRing.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 };
    }

    private static DropShadowEffect Shadow(double opacity, double blur, double depth) =>
        new() { Color = Colors.Black, Opacity = opacity, BlurRadius = blur, ShadowDepth = depth, Direction = 90 };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private void RebuildBadges(JsonArray badges)
    {
        LeftWing.Children.Clear();
        RightWing.Children.Clear();
        foreach (var node in badges)
        {
            if (node is not JsonObject badge)
            {
                continue;
            }
            var wing = ReadBool(badge, "right") ? RightWing : LeftWing;
            wing.Children.Add(BadgeElement(ReadString(badge, "sprite")));
        }
    }

    /// A sprite when the badge names one that exists; otherwise the star glyph (for now).
    private static UIElement BadgeElement(string? spriteId)
    {
        var sheet = spriteId is null ? null : SpriteCache.Get(spriteId);
        if (sheet is not null)
        {
            return new SpriteView(sheet) { Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        }
        return new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brushes.White,
            Width = 18,
            Height = 18,
            Margin = new Thickness(3, 0, 3, 0),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string? ReadString(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool ReadBool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    private void OnClosed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _closeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _state.PropertyChanged -= OnStateChanged;
    }
}

/// Turns the animated double Radius into the CornerRadius of a shape.
public sealed class RadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
