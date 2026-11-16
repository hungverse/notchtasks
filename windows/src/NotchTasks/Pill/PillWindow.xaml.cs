using System.Collections.Specialized;
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
using NotchTasks.Core.Asks;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Settings;

namespace NotchTasks.Pill;

/// The floating pill at the top centre of the primary screen. It never takes focus. The window is sized once; the
/// pill inside it animates. The window is click-through (WS_EX_TRANSPARENT|WS_EX_LAYERED) while the pointer is outside
/// the pill, so clicks reach the windows below; a 10 Hz poll of the pointer drives that, hover-open and the close check.
public partial class PillWindow : Window
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
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
    private NativePoint _lastPoint;
    private bool _havePoint;
    private string _badgesJson = string.Empty;
    private bool _hasCollapsed;
    private Color _accent = Color.FromRgb(0x32, 0xD2, 0xFF);

    /// The shape the pill is animating to: closed, the ask toast, or open.
    private enum Mode
    {
        Closed,
        Toast,
        Open,
    }

    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, AskCard> _cards = new(StringComparer.Ordinal);
    private AgentAsk? _toastAsk;  // the ask the toast shows; null when no toast is up
    private readonly List<AgentAsk> _toastQueue = [];  // asks waiting for their toast, oldest first (Mac toastQueue)
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);  // ids that have had their toast queued
    private int _animation;  // counts the animations; only the last one's completion clears _closing
    private bool _animating;  // a shape animation runs: the poll evaluates click-through on every tick
    private double _targetWidth, _targetHeight, _targetRadius;  // the shape the running animation springs to

    /// Creates the pill for the app state. Call Show next (the window is placed when its handle exists).
    public PillWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        CardText.Text = L10n.L("No running tasks");
        CardLayer.Effect = _cardBlur;

        _pollTimer.Tick += OnPoll;
        _closeTimer.Tick += OnCloseTick;
        _waitTimer.Tick += (_, _) => TickWaiting();
        PillShape.MouseLeftButtonUp += OnPillClick;
        PillShape.SizeChanged += (_, _) => UpdateClip();
        Toast.Answered += (ask, reply) => _state.Answer(ask.Id, reply);
        Toast.Opened += OpenPill;
        Toast.Hidden += ShowNextToast;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _state.PropertyChanged += OnStateChanged;
        _state.Asks.CollectionChanged += OnAsksChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        SyncCards();
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

    /// 10 Hz: sets click-through from the pointer's position, and opens the pill after HoverDelay over it (hover mode).
    /// An unmoved pointer costs nothing, unless a hover-open is still waiting for its delay.
    private void OnPoll(object? sender, EventArgs e)
    {
        if (!Win32.GetCursorPos(out var point))
        {
            return;
        }
        var moved = !_havePoint || point.X != _lastPoint.X || point.Y != _lastPoint.Y;
        var hoverWaiting = _enteredAt is not null && !_open && _state.Settings.ExpandMode == "hover";
        if (!moved && !hoverWaiting && !_animating)
        {
            return;
        }
        _lastPoint = point;
        _havePoint = true;

        var inside = IsOverShape(point);
        SetClickThrough(!inside);
        if (!inside)
        {
            _enteredAt = null;
            return;
        }

        _enteredAt ??= Stopwatch.GetTimestamp();
        // Over a toast the pointer is there to read it or press its buttons: the pill does not open on hover (Mac).
        if (!_open && _toastAsk is null && _state.Settings.ExpandMode == "hover"
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
        if (!_open && _toastAsk is null && _state.Settings.ExpandMode == "click")
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
        if (Win32.GetCursorPos(out var point) && IsOverShape(point))
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
        _outsideSince = null;
        // Topmost is asserted again on every open; the window itself never activates.
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        // Opening the pill takes over the toast; its ask stays in the card list.
        _toastAsk = null;
        Animate(Mode.Open);
        ApplyAppearance();
        _closeTimer.Start();
        if (_state.Asks.Count > 0)
        {
            TickWaiting();
            _waitTimer.Start();
        }
    }

    private void ClosePill()
    {
        if (!_open)
        {
            return;
        }
        _open = false;
        _outsideSince = null;
        _closeTimer.Stop();
        _waitTimer.Stop();
        Animate(Mode.Closed);  // sets _closing: the pill stays visible until the close spring has finished
        ApplyAppearance();
    }

    /// Shows the next queued ask's toast (the pill springs into the toast shape), or takes the toast away when none waits:
    /// the pill springs back to the closed shape, or stays open. Called when the shown ask leaves, and by its ×
    /// (Mac dropAskToasts / dismissToast). The window never takes focus.
    private void ShowNextToast()
    {
        _toastAsk = null;
        AgentAsk? next = null;
        if (!_open && _toastQueue.Count > 0)
        {
            next = _toastQueue[0];
            _toastQueue.RemoveAt(0);
        }
        if (next is not null)
        {
            _toastAsk = next;
            Toast.Show(next);
            // Topmost is asserted again when a toast shows, as OpenPill does; the window itself never activates.
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        Animate(next is not null ? Mode.Toast : _open ? Mode.Open : Mode.Closed);
        ApplyAppearance();
    }

    /// The Mac's spring for the shape, towards the size of the mode. The card and the toast fade and the top row fades
    /// out behind a toast, over min(Settle, 0.35 s); the card un-blurs when it shows, and only opacity moves when reduced.
    /// Returns the width animation (the one that finishes last on the way down).
    private DoubleAnimation Animate(Mode mode)
    {
        var settings = _state.Settings;
        var animationsOn = SystemParameters.ClientAreaAnimation;
        var reduced = !animationsOn || settings.Motion == "reduced";
        var (width, height, radius) = Target(mode);
        var curve = SpringCurve.For(settings.Motion, mode != Mode.Closed, animationsOn);

        var token = ++_animation;
        _closing = mode == Mode.Closed;
        _animating = true;
        (_targetWidth, _targetHeight, _targetRadius) = (width, height, radius);
        var widthAnimation = Spring(PillShape, FrameworkElement.WidthProperty, width, curve);
        Spring(PillShape, FrameworkElement.HeightProperty, height, curve);
        Spring(this, RadiusProperty, radius, curve);

        var fadeSeconds = Math.Min(curve.Settle, MaxFadeDuration.TotalSeconds);
        var fade = new SpringCurve(fadeSeconds, 1) { EaseInOut = true };
        var duration = new Duration(TimeSpan.FromSeconds(fadeSeconds));
        Fade(CardLayer, OpacityProperty, mode == Mode.Open ? 1 : 0, duration, fade);
        Fade(Toast, OpacityProperty, mode == Mode.Toast ? 1 : 0, duration, fade);
        Fade(TopRow, OpacityProperty, mode == Mode.Toast ? 0 : 1, duration, fade);
        var blur = mode == Mode.Open || reduced ? 0 : 6;
        Fade(_cardBlur, BlurEffect.RadiusProperty, blur, duration, fade);

        widthAnimation.Completed += (_, _) =>
        {
            if (token == _animation)
            {
                _closing = false;
                _animating = false;
                ApplyAppearance();
                ReevaluateClickThrough();
            }
        };
        // The pointer may already be over the target shape: the window must take its clicks from this frame on.
        ReevaluateClickThrough();
        return widthAnimation;
    }

    /// Sets click-through from the pointer now, against the shape the pill is springing to as well as the one it is.
    /// The poll alone waits for the pointer to move, so a toast under a still pointer would pass its first click through.
    private void ReevaluateClickThrough()
    {
        if (Win32.GetCursorPos(out var point))
        {
            SetClickThrough(!IsOverShape(point));
        }
    }

    /// The width, height and radius a mode springs to. The open height follows the cards; the toast follows its text.
    private (double Width, double Height, double Radius) Target(Mode mode) => mode switch
    {
        Mode.Open => (PillGeometry.OpenWidth, OpenHeight(), PillGeometry.OpenRadius),
        Mode.Toast => (PillGeometry.AskToastWidth, ToastHeight(), PillGeometry.AskToastRadius),
        _ => (PillGeometry.ClosedWidth, PillGeometry.ClosedHeight, PillGeometry.ClosedRadius),
    };

    /// The open pill's height: the placeholder when no ask waits; otherwise the top row and the cards, up to MaxOpenHeight.
    private double OpenHeight()
    {
        if (_state.Asks.Count == 0)
        {
            return PillGeometry.OpenHeight;
        }
        AskScroll.Measure(new Size(PillGeometry.OpenWidth, double.PositiveInfinity));
        return Math.Min(PillGeometry.MaxOpenHeight, PillGeometry.ClosedHeight + AskScroll.DesiredSize.Height);
    }

    /// The toast's measured height for its current text.
    private double ToastHeight()
    {
        Toast.Measure(new Size(PillGeometry.AskToastWidth, double.PositiveInfinity));
        return Toast.DesiredSize.Height;
    }

    private static void Fade(IAnimatable target, DependencyProperty property, double to, Duration duration, SpringCurve fade) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = new SpringEase(fade) });

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
        return IsOverShape(point);
    }

    /// The pill's current shape, or, while a shape animation runs, the shape it springs to as well.
    private bool IsOverShape(NativePoint screenPoint) =>
        IsInsidePill(screenPoint) || (_animating && IsInsideShape(screenPoint, _targetWidth, _targetHeight, _targetRadius));

    /// Hit test in the animated geometry: the pill is centred at the top of the window, with the current size and radius.
    private bool IsInsidePill(NativePoint screenPoint) => IsInsideShape(screenPoint, PillShape.Width, PillShape.Height, Radius);

    private bool IsInsideShape(NativePoint screenPoint, double width, double height, double radius)
    {
        var client = screenPoint;
        Win32.ScreenToClient(_hwnd, ref client);
        var scale = Scale;
        var p = new Point(client.X / scale, client.Y / scale);

        var x = (PillGeometry.WindowWidth - width) / 2;
        return InRoundedRect(p, x, 0, width, height, radius);
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

    /// Brings the cards, the toast, the open pill's height and the count badge in line with Asks. A new ask shows its
    /// toast while the pill is closed; a toast whose ask has left closes; a toast shows the newest ask.
    private void OnAsksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncCards();
        var asks = _state.Asks;
        var waiting = new HashSet<string>(asks.Select(a => a.Id), StringComparer.Ordinal);
        // Each new ask queues its toast once; a queued toast for an ask that has left is dropped (Mac dropAskToasts).
        _queued.RemoveWhere(id => !waiting.Contains(id));
        foreach (var ask in asks)
        {
            if (_queued.Add(ask.Id))
            {
                _toastQueue.Add(ask);
            }
        }
        _toastQueue.RemoveAll(a => !waiting.Contains(a.Id));

        // The shown toast's ask leaving shows the next queued toast, or none (Mac showNextToast).
        if (_toastAsk is null || !waiting.Contains(_toastAsk.Id))
        {
            if (_toastAsk is not null || (!_open && _toastQueue.Count > 0))
            {
                ShowNextToast();
            }
        }

        if (_open)
        {
            Animate(Mode.Open);  // the open pill's height follows the cards
            if (asks.Count > 0)
            {
                if (!_waitTimer.IsEnabled)
                {
                    _waitTimer.Start();
                }
                TickWaiting();
            }
            else
            {
                _waitTimer.Stop();
            }
        }
        UpdateCountBadge();
        ApplyAppearance();
    }

    /// One card per waiting ask, oldest first. A card is kept while its ask waits, so what was picked in it is not lost
    /// when another ask comes or goes. The placeholder shows when no ask waits.
    private void SyncCards()
    {
        var asks = _state.Asks;
        var waiting = new HashSet<string>(asks.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var id in _cards.Keys.Where(id => !waiting.Contains(id)).ToList())
        {
            _cards.Remove(id);
        }

        AskList.Children.Clear();
        foreach (var ask in asks)
        {
            if (!_cards.TryGetValue(ask.Id, out var card))
            {
                card = new AskCard(ask);
                var id = ask.Id;
                card.Replied += reply => _state.Answer(id, reply);
                _cards[ask.Id] = card;
            }
            card.Margin = new Thickness(0, AskList.Children.Count == 0 ? 0 : 8, 0, 0);
            AskList.Children.Add(card);
        }
        AskScroll.Visibility = asks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CardText.Visibility = asks.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Each card's "Waiting m:ss", once a second while the open pill shows them.
    private void TickWaiting()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var card in _cards.Values)
        {
            card.UpdateWaiting(now);
        }
    }

    /// The closed pill's count of waiting asks, as an orange badge at the end of the right wing, when more than one waits.
    private void UpdateCountBadge()
    {
        for (var i = RightWing.Children.Count - 1; i >= 0; i--)
        {
            if (RightWing.Children[i] is FrameworkElement { Tag: "ask-count" })
            {
                RightWing.Children.RemoveAt(i);
            }
        }
        var count = _state.Asks.Count;
        if (count <= 1)
        {
            return;
        }
        RightWing.Children.Add(new Border
        {
            Tag = "ask-count",
            Background = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))),
            CornerRadius = new CornerRadius(8),
            Height = 16,
            MinWidth = 16,
            Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = count.ToString(CultureInfo.InvariantCulture),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
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
        // Idle (no badges, no ask, closed) hides the pill only once the close spring has finished; its hover area stays.
        // Waiting asks show the pill too: a toast, or the count badge when more than one waits.
        PillShape.Opacity = _open || _closing || _toastAsk is not null || _hasCollapsed || _state.Asks.Count > 1 ? 1 : 0;
        // Only the open cards and the toast take the pointer; the closed pill's wings never do.
        CardLayer.IsHitTestVisible = _open;
        Toast.IsHitTestVisible = _toastAsk is not null;

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

        // The shadow and glow effects live on the backing borders behind the pill; setting them here is per state change only.
        ShadowShape.Opacity = PillShape.Opacity;
        ShadowShape.Effect = _open
            ? Shadow(opacity: 0.45, blur: 14, depth: 6)
            : Shadow(opacity: 0.3, blur: 6, depth: 2);

        var glow = settings.Background == "glow" && (_open || _hasCollapsed);
        var accentBrush = Frozen(new SolidColorBrush(_accent) { Opacity = 0.9 });
        GlowBacking.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        GlowBacking.BorderBrush = accentBrush;
        GlowBacking.Effect = new DropShadowEffect { Color = _accent, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 };
        GlowRing.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        GlowRing.BorderBrush = accentBrush;
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
        UpdateCountBadge();
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
        _waitTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _state.PropertyChanged -= OnStateChanged;
        _state.Asks.CollectionChanged -= OnAsksChanged;
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
