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
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Motion;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;

namespace NotchTasks.Pill;

/// The floating pill at the top centre of the primary screen. It never takes focus. The window is sized once; the
/// pill inside it animates. The window is click-through (WS_EX_TRANSPARENT|WS_EX_LAYERED) while the pointer is outside
/// the pill, so clicks reach the windows below; a poll of the pointer drives that, hover-open and the close check.
/// The poll runs at 30 Hz while the pointer is within NearMargin of the pill, so a click soon after entering reaches it.
/// The pill only reads AppState and draws it: the toasts, the chips and the sections are drawn by ToastView, ChipView
/// and SectionRegistry.
public partial class PillWindow : Window
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(33);  // 30 Hz, near the pill
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromMilliseconds(100);  // 10 Hz, otherwise
    private static readonly TimeSpan ToastTick = TimeSpan.FromMilliseconds(300);  // the toast queue, while a toast is up
    private const double NearMargin = 40;  // DIP from the pill's shape
    private static readonly TimeSpan MaxFadeDuration = TimeSpan.FromMilliseconds(350);

    /// The corner radius of the pill; animated by the spring, bound to every shape through RadiusConverter.
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(PillWindow),
        new PropertyMetadata(PillGeometry.ClosedRadius, (d, _) => ((PillWindow)d).UpdateClip()));

    private readonly AppState _state;
    private readonly DispatcherTimer _pollTimer = new() { Interval = SlowPollInterval };
    private bool _fastPoll;  // the poll interval in use: fast near the pill, slow otherwise
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastTick };
    private readonly BlurEffect _cardBlur = new() { Radius = 6 };
    private nint _hwnd;
    private bool _open;
    private bool _closing;
    private bool _clickThrough;
    private long? _outsideSince;
    private long? _enteredAt;
    private NativePoint _lastPoint;
    private bool _havePoint;
    private string _badgesKey = string.Empty;
    private int _badgeCount;
    private bool _hasCollapsed;
    private Color _accent = Color.FromRgb(0x32, 0xD2, 0xFF);  // the resolved accent: the badges' colour, and never null
    private Color? _chosenAccent;  // the accent the user chose: null for "auto" (the Mac's accentColor is nil then)
    private int _sectionRebuilds;  // how often the sections were taken out and put back (a rebuild, not a margin change)

    /// The shape the pill is animating to: closed, the toast, or open.
    private enum Mode
    {
        Closed,
        Toast,
        Open,
    }

    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly AskSection _asks = new();  // the agents' section: one card per waiting ask
    private readonly Func<AppState, FrameworkElement?> _agentsSection;  // the builder this pill registered for "agents"
    private readonly TimerCard _timerCard = new();  // the timer's card: one element, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _timerSection;  // the builder this pill registered for "timer"
    private readonly LoadMeter _cpuMeter = new("CPU", 0);  // the header's CPU meter, shown while SystemLoad is on
    private readonly LoadMeter _ramMeter = new("RAM", 0) { Margin = new Thickness(10, 0, 0, 0) };  // 10 DIP after the CPU
    private Toast? _shownToast;  // the toast the pill shows now; null when none
    private int _animation;  // counts the animations; only the last one's completion clears _closing
    private bool _animating;  // a shape animation runs: the poll evaluates click-through on every tick
    private double _targetWidth, _targetHeight, _targetRadius;  // the shape the running animation springs to
    private Mode _targetMode = Mode.Closed;  // the mode of that shape

    /// Creates the pill for the app state. Call Show next (the window is placed when its handle exists).
    public PillWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        LoadMeters.Children.Add(_cpuMeter);
        LoadMeters.Children.Add(_ramMeter);
        _cpuMeter.Clicked += OpenTaskManager;
        _ramMeter.Clicked += OpenTaskManager;
        SyncHeader();
        CardLayer.Effect = _cardBlur;
        // The gear opens the Control Panel and closes the pill; the power button quits (as the tray's Quit does).
        Bar.ControlPanelRequested += () =>
        {
            ClosePill();
            ControlPanelWindow.Show(_state);
        };
        Bar.QuitRequested += () => App.Quit();
        L10n.Changed += OnLanguageChanged;

        _pollTimer.Tick += OnPoll;
        _closeTimer.Tick += OnCloseTick;
        _waitTimer.Tick += (_, _) => TickWaiting();
        _toastTimer.Tick += (_, _) => TickToast();
        PillShape.MouseLeftButtonUp += OnPillClick;
        PillShape.SizeChanged += (_, _) => UpdateClip();
        ToastBox.Answered += (ask, reply) => _state.Answer(ask.Id, reply);
        ToastBox.Opened += OnToastOpened;
        ToastBox.Dismissed += () => _state.Toasts.Dismiss(DateTimeOffset.Now);

        // The agents' section is the ask cards. Other modules register their sections the same way.
        _agentsSection = _ => _state.Asks.Count > 0 ? _asks.Panel : null;
        SectionRegistry.Register("agents", _agentsSection);
        // The timer's section is its card while a timer runs.
        _timerSection = state => _timerCard.Update(state, _chosenAccent) ? _timerCard : null;
        SectionRegistry.Register("timer", _timerSection);

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _state.PropertyChanged += OnStateChanged;
        _state.Asks.CollectionChanged += OnAsksChanged;
        _state.NotchChanged += OnNotchChanged;
        _state.Toasts.Changed += OnToastsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SpriteClock.WatchSystem();

        Refresh();
        _asks.Sync(_state.Asks, _state.Answer);
        SyncSections();
        SyncToast();
    }

    /// The corner radius of the pill, animated.
    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private double Scale => _hwnd == nint.Zero ? 1 : Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// The open pill's element for a module's section, or null when the module has none to show.
    internal FrameworkElement? SectionFor(string module) => SectionRegistry.Section(module, _state);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, style | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        var displayState = Win32.GuidConsoleDisplayState;
        Win32.RegisterPowerSettingNotification(_hwnd, ref displayState, 0);  // a failed registration only loses the display pause
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
            case Win32.WM_POWERBROADCAST:
                // The console display turned off, on or dimmed: the sprites stand still while it is off.
                if (wParam == Win32.PBT_POWERSETTINGCHANGE && lParam != nint.Zero
                    && Marshal.PtrToStructure<Guid>(lParam) == Win32.GuidConsoleDisplayState)
                {
                    SpriteClock.SetPause(ClockPause.DisplayOff, Marshal.ReadInt32(lParam, Win32.PowerSettingDataOffset) == 0);
                }
                else if (wParam == Win32.PBT_APMPOWERSTATUSCHANGE)
                {
                    // The AC line or the battery changed: the battery module reads it now.
                    _state.PowerStatusChanged();
                }
                break;
            case Win32.WM_SETTINGCHANGE:
                {
                    // The work area moves the pill; the colour set (the Windows accent) refreshes it. The string form is read now,
                    // while the message's memory is valid.
                    var setting = lParam != nint.Zero ? Marshal.PtrToStringUni(lParam) : null;
                    if (wParam == Win32.SPI_SETWORKAREA || setting == "WindowMetrics")
                    {
                        Dispatcher.BeginInvoke(new Action(Place));
                    }
                    if (setting == "ImmersiveColorSet")
                    {
                        Dispatcher.BeginInvoke(new Action(Refresh));
                    }
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

    /// Sets click-through from the pointer's position, and opens the pill after HoverDelay over it (hover mode).
    /// An unmoved pointer costs nothing, unless a hover-open is still waiting for its delay. The interval changes only
    /// when the pointer crosses the NearMargin boundary.
    private void OnPoll(object? sender, EventArgs e)
    {
        if (!Win32.GetCursorPos(out var point))
        {
            return;
        }
        SetPollRate(IsNearShape(point));
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
        if (!_open && _shownToast is null && _state.Settings.ExpandMode == "hover"
            && Stopwatch.GetElapsedTime(_enteredAt.Value) >= TimeSpan.FromSeconds(_state.Settings.HoverDelay))
        {
            OpenPill();
        }
    }

    /// Sets the poll interval: fast while the pointer is near the pill, slow otherwise. Changes the timer only on a transition.
    private void SetPollRate(bool near)
    {
        if (near == _fastPoll)
        {
            return;
        }
        _fastPoll = near;
        _pollTimer.Interval = near ? FastPollInterval : SlowPollInterval;
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
        if (!_open && _shownToast is null && _state.Settings.ExpandMode == "click")
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
        // Opening the pill takes over the toast; its ask stays in the sections.
        _state.Toasts.Clear();
        Animate(Mode.Open);
        ApplyAppearance();
        _closeTimer.Start();
        SyncWaitTimer();
        _state.PillOpened(true);  // the load is sampled while the pill is open
    }

    /// The cards' "Waiting m:ss" ticks once a second, while the open pill shows a waiting ask.
    private void SyncWaitTimer()
    {
        if (_open && _state.Snapshot.AskCount > 0)
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
        SyncToast();  // a toast that came in while the pill was open shows now
        _state.PillOpened(false);  // no load is sampled while the pill is closed; the meters are forgotten
    }

    /// A meter was clicked: the pill closes and Task Manager opens.
    private void OpenTaskManager()
    {
        ClosePill();
        SystemActions.OpenTaskManager();
    }

    /// The header: the summary text, or the CPU and RAM meters while SystemLoad is on and a load is known. The meters ease to
    /// each new value unless animations are off or the motion is reduced.
    private void SyncHeader()
    {
        HeaderSummary.Text = PillSummary.For(_state.Snapshot);
        var load = _state.Snapshot.Load;
        var meters = _state.Settings.SystemLoad && load is not null;
        HeaderSummary.Visibility = meters ? Visibility.Collapsed : Visibility.Visible;
        LoadMeters.Visibility = meters ? Visibility.Visible : Visibility.Collapsed;
        if (load is not null)
        {
            var animate = SystemParameters.ClientAreaAnimation && _state.Settings.Motion != "reduced";
            _cpuMeter.Show(load.Cpu, animate);
            _ramMeter.Show(load.Memory, animate);
        }
    }

    /// Brings the pill's toast in line with the queue: the pill springs into the toast's shape while one is up and the pill is
    /// closed, and springs back when none is. The toast timer runs only while a toast is current, so an idle pill costs nothing.
    private void SyncToast()
    {
        var current = _state.Toasts.Current;
        _toastTimer.IsEnabled = current is not null;
        var target = _open ? null : current;
        if (ReferenceEquals(target, _shownToast))
        {
            return;
        }
        var wasShowing = _shownToast is not null;
        _shownToast = target;
        if (target is not null)
        {
            var ask = target.AskId is { } askId ? _state.Asks.FirstOrDefault(a => a.Id == askId) : null;
            ToastBox.Show(target, _state.Settings.ToastStyle, ask, _state.Settings.Motion);
            // Topmost is asserted again when a toast shows, as OpenPill does; the window itself never activates.
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            Animate(Mode.Toast);
        }
        else if (wasShowing)
        {
            Animate(_open ? Mode.Open : Mode.Closed);
        }
        ApplyAppearance();
    }

    /// The toast timer: the queue moves on when the toast's time is up and the pointer has been off the pill (Toasts.Tick).
    private void TickToast()
    {
        // A toast queued while the pill is open still expires; the pointer holds it only over the toast itself.
        var hovered = !_open && Win32.GetCursorPos(out var point) && IsInsidePill(point);
        _state.Toasts.Tick(DateTimeOffset.Now, hovered);
    }

    /// A toast was opened (a click on it, Reply or its action). An ask opens the pill, where every question is answered; any
    /// other toast runs its own action, and goes.
    private void OnToastOpened(Toast t)
    {
        if (t.AskId is not null)
        {
            OpenPill();
            return;
        }
        ToastActions.Run(t, _state);
        _state.Toasts.Dismiss(DateTimeOffset.Now);
    }

    /// The Mac's spring for the shape, towards the size of the mode. The card and the toast fade and the top row fades
    /// out behind a toast, over min(Settle, 0.35 s); the card un-blurs when it shows, and only opacity moves when reduced.
    /// A call for the target the shape already has does nothing.
    private void Animate(Mode mode)
    {
        var settings = _state.Settings;
        var animationsOn = SystemParameters.ClientAreaAnimation;
        var reduced = !animationsOn || settings.Motion == "reduced";
        var (width, height, radius) = Target(mode);
        if (mode == _targetMode && Near(width, _targetWidth) && Near(height, _targetHeight) && Near(radius, _targetRadius))
        {
            // The shape already springs to this target: a repeat (an ask, then the sections it changed) does not restart it.
            ReevaluateClickThrough();
            return;
        }
        var curve = SpringCurve.For(settings.Motion, mode != Mode.Closed, animationsOn);

        var token = ++_animation;
        _closing = mode == Mode.Closed;
        _animating = true;
        _targetMode = mode;
        (_targetWidth, _targetHeight, _targetRadius) = (width, height, radius);
        var widthAnimation = Spring(PillShape, FrameworkElement.WidthProperty, width, curve);
        Spring(PillShape, FrameworkElement.HeightProperty, height, curve);
        Spring(this, RadiusProperty, radius, curve);

        var fadeSeconds = Math.Min(curve.Settle, MaxFadeDuration.TotalSeconds);
        var fade = new SpringCurve(fadeSeconds, 1) { EaseInOut = true };
        var duration = new Duration(TimeSpan.FromSeconds(fadeSeconds));
        Fade(CardLayer, OpacityProperty, mode == Mode.Open ? 1 : 0, duration, fade);
        Fade(ToastBox, OpacityProperty, mode == Mode.Toast ? 1 : 0, duration, fade);
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
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;

    /// Sets click-through from the pointer now, against the shape the pill is springing to as well as the one it is.
    /// The poll alone waits for the pointer to move, so a toast under a still pointer would pass its first click through.
    private void ReevaluateClickThrough()
    {
        if (Win32.GetCursorPos(out var point))
        {
            SetClickThrough(!IsOverShape(point));
        }
    }

    /// The width, height and radius a mode springs to. The open height follows the sections; the toast follows its text.
    private (double Width, double Height, double Radius) Target(Mode mode) => mode switch
    {
        Mode.Open => (PillGeometry.OpenWidth, OpenHeight(), PillGeometry.OpenRadius),
        Mode.Toast => (ToastBox.Width, ToastHeight(), ToastBox.ShapeRadius),
        _ => (PillGeometry.ClosedWidth, PillGeometry.ClosedHeight, PillGeometry.ClosedRadius),
    };

    /// The open pill's height: the placeholder when no section has anything; otherwise the top row and the sections, up to MaxOpenHeight.
    private double OpenHeight()
    {
        if (SectionList.Children.Count == 0)
        {
            return PillGeometry.OpenHeight;
        }
        SectionScroll.Measure(new Size(PillGeometry.OpenWidth, double.PositiveInfinity));
        // DesiredSize includes the scroll's own margin (the 26 DIP header above, the 32 DIP quick bar below) and its padding: added once, here.
        return Math.Min(PillGeometry.MaxOpenHeight, PillGeometry.ClosedHeight + SectionScroll.DesiredSize.Height);
    }

    /// The toast's measured height for its current text.
    private double ToastHeight()
    {
        ToastBox.Measure(new Size(ToastBox.Width, double.PositiveInfinity));
        return ToastBox.DesiredSize.Height;
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
        var x = (PillGeometry.WindowWidth - width) / 2;
        return PillHitTest.InRoundedRect(ToShapePoint(screenPoint), x, 0, width, height, radius);
    }

    /// True within NearMargin of the pill's current shape, or of the shape it springs to while an animation runs.
    private bool IsNearShape(NativePoint screenPoint)
    {
        var p = ToShapePoint(screenPoint);
        if (PillHitTest.DistanceToShape(p, PillShape.Width, PillShape.Height, Radius) <= NearMargin)
        {
            return true;
        }
        return _animating && PillHitTest.DistanceToShape(p, _targetWidth, _targetHeight, _targetRadius) <= NearMargin;
    }

    /// The screen point in the pill's DIP space (the window's client area, unscaled).
    private Point ToShapePoint(NativePoint screenPoint)
    {
        var client = screenPoint;
        Win32.ScreenToClient(_hwnd, ref client);
        var scale = Scale;
        return new Point(client.X / scale, client.Y / scale);
    }

    /// Brings the cards, the sections, the open pill's height and the wait timer in line with Asks. The toasts follow the
    /// queue (OnToastsChanged).
    private void OnAsksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _asks.Sync(_state.Asks, _state.Answer);
        SyncSections();
        if (_open)
        {
            Animate(Mode.Open);  // the open pill's height follows the sections; the same height again does nothing
        }
        ApplyAppearance();
    }

    /// The open pill's sections, from the registry: the active modules' order, with calls first. The placeholder shows when
    /// no section has anything.
    /// The children are replaced only when the sections differ: a publish that changes nothing (the timer's tick) leaves them in
    /// place, so a press on one of them is not lost to a removal. Only the margins are set again.
    private void SyncSections()
    {
        var sections = SectionRegistry.For(_state);
        if (!SameChildren(sections))
        {
            _sectionRebuilds++;
            SectionList.Children.Clear();
            foreach (var section in sections)
            {
                SectionList.Children.Add(section);
            }
        }
        for (var i = 0; i < sections.Count; i++)
        {
            sections[i].Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
        }
        SectionScroll.Visibility = sections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// True when the panel holds exactly these elements, in this order.
    private bool SameChildren(IReadOnlyList<FrameworkElement> sections)
    {
        if (SectionList.Children.Count != sections.Count)
        {
            return false;
        }
        for (var i = 0; i < sections.Count; i++)
        {
            if (!ReferenceEquals(SectionList.Children[i], sections[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// Each card's "Waiting m:ss", once a second while the open pill shows them.
    private void TickWaiting() => _asks.UpdateWaiting(DateTimeOffset.UtcNow);

    /// The queue changed: the toast shown follows it.
    private void OnToastsChanged() => SyncToast();

    /// The app state's snapshot was rebuilt: the chips and the sections follow it.
    private void OnNotchChanged()
    {
        RebuildChips();
        SyncSections();
        Bar.Update(_state);
        SyncHeader();
        SyncWaitTimer();
        if (_open)
        {
            Animate(Mode.Open);  // the open pill's height follows its sections; the same height again does nothing
        }
        ApplyAppearance();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Settings))
        {
            Refresh();
        }
    }

    /// Brings the look in line with the settings: badges, chips, background, accent and motion (read again on the next open).
    private void Refresh()
    {
        var settings = _state.Settings;
        var badges = settings.Badges;
        var hex = Appearance.ResolveAccent(settings.Accent, settings.CustomAccent, SystemAccent.Hex()) ?? "#32D2FF";
        _accent = (Color)ColorConverter.ConvertFromString(hex)!;
        _chosenAccent = ChosenAccent(settings);
        // The badges take the accent when they have no colour of their own, so a new accent rebuilds them too.
        var key = hex + "|" + badges.ToJsonString();
        if (key != _badgesKey)
        {
            _badgesKey = key;
            RebuildBadges(badges);
        }
        RebuildChips();  // the chips follow the accent and the module colours
        Bar.Update(_state);
        SyncHeader();
        ApplyAppearance();
    }

    /// The accent the user chose, for the chips and the timer card: null for "auto", which uses the Mac's defaults (orange for
    /// the timer). The system accent is not used here, as the Mac's accentColor is nil for "auto".
    private static Color? ChosenAccent(SettingsStore settings)
    {
        if (settings.Accent == "auto" || Appearance.ResolveAccent(settings.Accent, settings.CustomAccent, null) is not { } hex)
        {
            return null;
        }
        return HexColor.TryNormalize(hex, out var normalized) ? HexColor.ToColor(normalized) : null;
    }

    /// The surface, shadow, glow and visibility for the current state (open, closing, closed, idle).
    private void ApplyAppearance()
    {
        var settings = _state.Settings;
        // Idle (no badges, no ask, closed) hides the pill only once the close spring has finished; its hover area stays.
        // A toast, a waiting ask and any chip or badge show the pill too.
        PillShape.Opacity = _open || _closing || _shownToast is not null || _hasCollapsed || _state.Snapshot.AskCount > 0 ? 1 : 0;
        // Only the open sections and the toast take the pointer; the closed pill's wings never do.
        CardLayer.IsHitTestVisible = _open;
        ToastBox.IsHitTestVisible = _shownToast is not null;

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

    /// The badges of the settings, in their wings. The chips go after them in RebuildChips.
    private void RebuildBadges(JsonArray badges) => _badgeCount = BadgeGlyph.FillWings(LeftWing, RightWing, badges, _accent);

    /// The closed pill's chips, after the badges in the wings. A pill with a badge or a chip shows.
    private void RebuildChips()
    {
        var chips = ChipView.FillWings(LeftWing, RightWing, _state, _chosenAccent);
        _hasCollapsed = _badgeCount > 0 || chips > 0;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_open)
        {
            _state.PillOpened(false);
        }
        _pollTimer.Stop();
        _closeTimer.Stop();
        _waitTimer.Stop();
        _toastTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _state.PropertyChanged -= OnStateChanged;
        _state.Asks.CollectionChanged -= OnAsksChanged;
        _state.NotchChanged -= OnNotchChanged;
        _state.Toasts.Changed -= OnToastsChanged;
        L10n.Changed -= OnLanguageChanged;
        // Only this pill's own builder: a newer pill may have registered its own since.
        SectionRegistry.Unregister("agents", _agentsSection);
        SectionRegistry.Unregister("timer", _timerSection);
    }

    /// Re-reads the pill's own texts after a language change. The ask cards and the toast keep the text they were made
    /// with until they are made again: rebuilding a card would lose what was picked in it.
    private void OnLanguageChanged()
    {
        SyncHeader();
        RebuildChips();
        ApplyAppearance();
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
