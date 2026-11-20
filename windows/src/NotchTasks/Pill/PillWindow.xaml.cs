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
using NotchTasks.Core.Modules;
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

    private readonly AppState _state;
    private readonly DispatcherTimer _pollTimer = new() { Interval = SlowPollInterval };
    private bool _fastPoll;  // the poll interval in use: fast near the pill, slow otherwise
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastTick };
    private readonly BlurEffect _cardBlur = new() { Radius = 6 };
    // The pill's one shape: the black, the clip, the shadow, the tint and the glow all draw this rectangle. Its Rect (in the
    // layout, see PillGeometry.ShapeRect) and its radii animate; the layout itself never does.
    private readonly RectangleGeometry _shape = new()
    {
        Rect = ToRect(PillGeometry.ShapeRect(PillGeometry.ClosedWidth, PillGeometry.ClosedHeight)),
        RadiusX = PillGeometry.ClosedRadius,
        RadiusY = PillGeometry.ClosedRadius,
    };
    // The shadows are made once: ApplyAppearance runs on every publish, so it only picks one of these (never makes a new one).
    private readonly DropShadowEffect _openShadow = Shadow(opacity: 0.45, blur: 14, depth: 6);
    private readonly DropShadowEffect _closedShadow = Shadow(opacity: 0.3, blur: 6, depth: 2);
    private DropShadowEffect? _glow;  // made again only when the accent changes
    private SolidColorBrush? _accentBrush;  // the glow's stroke, made again only when the accent changes
    private SolidColorBrush? _glassBrush;  // the glass surface: one frozen brush
    private LinearGradientBrush? _gradientBrush;  // the gradient surface, made again when its colours change
    private string _gradientKey = string.Empty;
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
    private readonly AskSection _asks = new();  // the agents' section's asks: one card per waiting ask
    private readonly AgentCards _agentCards = new();  // the agents' section's item cards, kept per item
    private readonly Func<AppState, FrameworkElement?> _agentsSection;  // the builder this pill registered for "agents"
    private readonly TimerCard _timerCard = new();  // the timer's card: one element, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _timerSection;  // the builder this pill registered for "timer"
    private readonly CallCard _callCard = new();  // the live call's card: one element, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _callSection;  // the builder this pill registered for "calls"
    private readonly SensorRow _sensorRow = new();  // the mic and camera row, outside a call
    private readonly Func<AppState, FrameworkElement?> _privacySection;  // the builder this pill registered for "privacy"
    private readonly CiCards _ciCards;  // the Build & CI section's alert cards and build cards, kept per id
    private readonly Func<AppState, FrameworkElement?> _ciSection;  // the builder this pill registered for "ci"
    private readonly NowPlayingCard _mediaCard = new();  // the Now Playing card: one element kept for the pill's life, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _mediaSection;  // the builder this pill registered for "media"
    private readonly DevicesCard _devicesCard = new();  // the Devices row: one element kept for the pill's life, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _devicesSection;  // the builder this pill registered for "devices"
    private readonly ShelfCard _shelfCard = new();  // the File shelf card: one element kept for the pill's life, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _shelfSection;  // the builder this pill registered for "shelf"
    private readonly MirrorCard _mirrorCard = new();  // the camera mirror card: one element kept for the pill's life, refreshed by its builder
    private readonly Func<AppState, FrameworkElement?> _mirrorSection;  // the builder this pill registered for "mirror"
    private readonly DispatcherTimer _shelfLeaveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };  // a drag that left clears the drop prompt after this
    private readonly LoadMeter _cpuMeter = new("CPU", LoadMeterKind.Cpu, 0);  // the header's CPU meter, shown while SystemLoad is on
    private readonly LoadMeter _ramMeter = new("RAM", LoadMeterKind.Memory, 0) { Margin = new Thickness(10, 0, 0, 0) };  // 10 DIP after the CPU
    private Toast? _shownToast;  // the toast the pill shows now; null when none
    private int _animation;  // counts the animations; only the last one's completion clears _closing
    private bool _animating;  // a shape animation runs: the poll evaluates click-through on every tick
    private double _targetWidth, _targetHeight, _targetRadius;  // the shape the running animation springs to
    private Mode _targetMode = Mode.Closed;  // the mode of that shape
    private bool _warm;  // the card layer is laid out at zero opacity while the pointer is near (WarmCard)
    private bool _openRefreshPending;  // a publish came during an open: it is applied once when the open finishes
    private double _openHeight;  // the open pill's height, as last measured
    private bool _openHeightValid;  // false once the sections changed size or were changed: OpenHeight measures again

    /// Creates the pill for the app state. Call Show next (the window is placed when its handle exists).
    public PillWindow(AppState state)
    {
        _state = state;
        InitializeComponent();
        // A section that changed size (its content changed) makes the open height measure again; nothing else does.
        SectionList.SizeChanged += (_, _) => _openHeightValid = false;
        EmptyNote.SizeChanged += (_, _) => _openHeightValid = false;

        // The layout is sized once: the shape's paths and the clip draw the shared rectangle inside it.
        PillShape.Width = PillGeometry.LayoutWidth;
        PillShape.Height = PillGeometry.MaxOpenHeight;
        foreach (var layer in new[] { ShapeFill, Tint, GlowRing })
        {
            layer.Width = PillGeometry.LayoutWidth;
            layer.Height = PillGeometry.MaxOpenHeight;
        }
        foreach (var layer in new[] { ShadowShape, GlowBacking })
        {
            layer.Width = PillGeometry.LayoutWidth;
            layer.Height = PillGeometry.MaxOpenHeight;
            layer.Data = _shape;
        }
        ShapeFill.Data = _shape;
        Tint.Data = _shape;
        GlowRing.Data = _shape;
        PillContent.Clip = _shape;
        // The wings start where the closed pill's edges are; they spring to the open shape's from there.
        LeftWingShift.X = PillGeometry.WingOffset(PillGeometry.ClosedWidth);
        RightWingShift.X = -PillGeometry.WingOffset(PillGeometry.ClosedWidth);

        LoadMeters.Children.Add(_cpuMeter);
        LoadMeters.Children.Add(_ramMeter);
        _cpuMeter.Clicked += OpenTaskManager;
        _ramMeter.Clicked += OpenTaskManager;
        SyncHeader();
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
        ToastBox.Answered += (ask, reply) => _state.Answer(ask.Id, reply);
        ToastBox.Opened += OnToastOpened;
        ToastBox.Dismissed += () => _state.Toasts.Dismiss(DateTimeOffset.Now);
        // The file shelf takes drops on the pill (Mac receiveDrop): a drag over the shape opens the pill with the drop prompt, and a
        // drop adds its files. The picker closes the pill before its dialog shows.
        PillShape.AllowDrop = true;
        PillShape.DragEnter += OnShelfDragEnter;
        PillShape.DragOver += OnShelfDragOver;
        PillShape.DragLeave += OnShelfDragLeave;
        PillShape.Drop += OnShelfDrop;
        _shelfLeaveTimer.Tick += (_, _) =>
        {
            _shelfLeaveTimer.Stop();
            _state.ShelfTargeted = false;
        };
        _state.ShelfPickStarting += ClosePill;

        // The agents' section: the ask cards first, then one card per running agent (not a build). Other modules register the same way.
        _agentsSection = state => _agentCards.Sync(state.Asks.Count > 0 ? _asks.Panel : null, state.Snapshot.Running,
            state.Snapshot.Limits, state.Settings.ModuleColors, _chosenAccent, state.Processes, DateTimeOffset.Now);
        SectionRegistry.Register("agents", _agentsSection);
        // The timer's section is its card while a timer runs.
        _timerSection = state => _timerCard.Update(state, _chosenAccent) ? _timerCard : null;
        SectionRegistry.Register("timer", _timerSection);
        // The call's card while a call is on: its Open button closes the pill, as the Mac's openCallApp does. The privacy row
        // shows while something uses the mic or camera outside a call.
        _callCard.OpenRequested += ClosePill;
        _callSection = state => _callCard.Update(state) ? _callCard : null;
        SectionRegistry.Register("calls", _callSection);
        _privacySection = state => _sensorRow.Update(state) ? _sensorRow : null;
        SectionRegistry.Register("privacy", _privacySection);
        // The Build & CI section: the failure alerts, then the working build rows. Dismissing an alert publishes at once, so it leaves now.
        _ciCards = new CiCards(id =>
        {
            _state.Ci.Dismiss(id);
            _state.Publish();
        }, ItemCard.OpenUrl);
        _ciSection = state => _ciCards.Sync(state.Snapshot.Alerts, state.Snapshot.BuildRunning, state.Settings.ToolIcons,
            state.Settings.ModuleColors, _chosenAccent, state.Processes, DateTimeOffset.Now);
        SectionRegistry.Register("ci", _ciSection);
        // The Now Playing card while a session is shown: its art, seek bar and buttons act on the state (AppState's media calls).
        _mediaSection = state => _mediaCard.Update(state, _chosenAccent) ? _mediaCard : null;
        SectionRegistry.Register("media", _mediaSection);
        // The Devices row while a device is listed: its capsules, refreshed by the state (the devices module's own list).
        _devicesSection = state => _devicesCard.Update(state) ? _devicesCard : null;
        SectionRegistry.Register("devices", _devicesSection);
        // The File shelf while it holds files or one is dragged over the pill: its tiles, refreshed by the state.
        _shelfSection = state => _shelfCard.Update(state, _accent) ? _shelfCard : null;
        SectionRegistry.Register("shelf", _shelfSection);
        // The camera mirror while it is open: its frames, or the message for why there is no picture. The camera runs in the state.
        _mirrorSection = state => _mirrorCard.Update(state) ? _mirrorCard : null;
        SectionRegistry.Register("mirror", _mirrorSection);

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _state.PropertyChanged += OnStateChanged;
        _state.Asks.CollectionChanged += OnAsksChanged;
        _state.NotchChanged += OnNotchChanged;
        _state.LoadChanged += SyncHeader;  // a load tick moves the meters only
        _state.ThermalChanged += SyncHeader;  // a temperature reading moves the header's temperature only
        _state.Toasts.Changed += OnToastsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SpriteClock.WatchSystem();

        Refresh();
        _asks.Sync(_state.Asks, _state.Answer);
        SyncSections();
        SyncToast();
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
            case Win32.WM_MOUSEHWHEEL:
                // A horizontal wheel (a touchpad swipe) scrolls the row under the pointer, whichever window has focus.
                handled = RouteHorizontalWheel(lParam, wParam);
                break;
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
        SetPollRate(IsNearShape(point));  // a change to near warms the card; leaving the near zone cools it again
        // Safety net: a drop prompt whose DragLeave never came clears once the pointer is off the pill with no button down. Only a
        // prompt that is up needs the check, so the hit test and the button read are skipped the rest of the time.
        if (_state.ShelfTargeted && ShelfCard.ClearsTarget(true, IsOverShape(point), Win32.GetAsyncKeyState(Win32.VK_LBUTTON) < 0))
        {
            _state.ShelfTargeted = false;
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
        if (near)
        {
            WarmCard();
        }
        else
        {
            CoolCard();
        }
    }

    /// The pointer came near the pill: the card layer is laid out now, at zero opacity, so the first frames of an open do not carry
    /// the layout. Nothing is drawn while it is at zero opacity.
    private void WarmCard()
    {
        if (_open || _closing || _animating || _warm)
        {
            return;
        }
        _warm = true;
        CardLayer.Height = OpenHeight() - PillGeometry.ClosedHeight;
        CardLayer.Opacity = 0;
        CardLayer.Visibility = Visibility.Visible;
    }

    /// The pointer left the near zone without opening: the warmed card layer goes back to hidden.
    private void CoolCard()
    {
        if (!_warm)
        {
            return;
        }
        _warm = false;
        if (!_open && !_animating)
        {
            CardLayer.Visibility = Visibility.Hidden;
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
        if (!_open && _shownToast is null && _state.Settings.ExpandMode == "click")
        {
            OpenPill();
        }
    }

    /// True when a drag carries files, the shelf module is on, and the card is not dragging its own files out: the shelf takes the drop
    /// (its effect is Copy, else None).
    private bool ShelfAccepts(DragEventArgs e) =>
        ShelfCard.AcceptsDrop(_state.Snapshot.ActiveModules.Contains("shelf"), e.Data.GetDataPresent(DataFormats.FileDrop), _shelfCard.Busy);

    /// A drag enters the pill: with files and the shelf on, the pill opens and the shelf shows its drop prompt.
    private void OnShelfDragEnter(object sender, DragEventArgs e)
    {
        _shelfLeaveTimer.Stop();
        if (!ShelfAccepts(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        _state.ShelfTargeted = true;
        if (!_open)
        {
            OpenPill();
        }
        e.Handled = true;
    }

    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ShelfAccepts(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// A drag left the pill: the prompt clears after 100 ms, unless the drag came back in (DragEnter stops the timer).
    private void OnShelfDragLeave(object sender, DragEventArgs e)
    {
        _shelfLeaveTimer.Stop();
        _shelfLeaveTimer.Start();
        e.Handled = true;
    }

    /// Files dropped on the pill are added to the shelf; the prompt clears at once.
    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        _shelfLeaveTimer.Stop();
        _state.ShelfTargeted = false;
        if (ShelfAccepts(e) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            // A failed save leaves the shelf as it was, and the drop is refused.
            e.Effects = ShelfSave.TrySave(() => _state.Shelf.Add(paths)) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    /// Closes after the pointer has been outside the open shape for CloseDelay. Runs only while open. The pill also stays open while
    /// a file is dragged over it, while a shelf file is dragged out, and while a shelf tile's menu is open.
    private void OnCloseTick(object? sender, EventArgs e)
    {
        if (!_open)
        {
            _closeTimer.Stop();
            return;
        }
        var over = Win32.GetCursorPos(out var point) && IsOverShape(point);
        if (over || _state.ShelfTargeted || _shelfCard.Busy)
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
        _warm = false;
        _outsideSince = null;
        // Topmost is asserted again on every open; the window itself never activates.
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        // Opening the pill takes over the toast; its ask stays in the sections.
        _state.Toasts.Clear();
        Animate(Mode.Open);
        ApplyAppearance();
        _closeTimer.Start();
        // The work that is not drawn (the shelf's files, the wait ticks, the sensors and the load) comes after the open's first frame.
        Dispatcher.BeginInvoke(new Action(FinishOpen), DispatcherPriority.ContextIdle);
    }

    /// The open's deferred work, once its first frame is drawn. A pill closed again before this ran has nothing to finish.
    private void FinishOpen()
    {
        if (!_open)
        {
            return;
        }
        _shelfCard.InvalidateExistence();  // a file may have gone while the pill was closed
        Animate(Mode.Open);  // a file that went or came changes the height: the open follows it (a no-op when it did not)
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
        _openRefreshPending = false;
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
        SyncTemperature();
    }

    /// The header's temperature, at the right: the thermometer and the whole degrees in the reading's tint. The tooltip names the
    /// zone with one decimal. Nothing is shown without a reading, or while SystemLoad is off.
    private void SyncTemperature()
    {
        if (!_state.Settings.SystemLoad || _state.Thermal is not { } reading)
        {
            TempReading.Visibility = Visibility.Collapsed;
            return;
        }
        var brush = (Brush)new BrushConverter().ConvertFromString(Thermals.Tint(reading.Celsius))!;
        brush.Freeze();
        TempGlyph.Text = ModuleGlyphs.Get("thermometer");
        TempGlyph.Foreground = brush;
        TempText.Text = Math.Round(reading.Celsius, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "°";
        TempText.Foreground = brush;
        TempReading.ToolTip = L10n.L("Temperature") + ": " + reading.Name + " "
            + reading.Celsius.ToString("0.0", CultureInfo.InvariantCulture) + "°C";
        TempReading.Visibility = Visibility.Visible;
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
        FrameLog.Begin(mode.ToString());

        var token = ++_animation;
        _closing = mode == Mode.Closed;
        _animating = true;
        _targetMode = mode;
        (_targetWidth, _targetHeight, _targetRadius) = (width, height, radius);
        var spring = new Duration(TimeSpan.FromSeconds(curve.Settle));
        // The card's height is set once per animation, to the open shape's height below the top row: the quick bar sits at the
        // bottom of the shape. The card shows only for an open, and the toast only for a toast.
        if (mode == Mode.Open)
        {
            CardLayer.Height = height - PillGeometry.ClosedHeight;
            CardLayer.Visibility = Visibility.Visible;
        }
        if (mode == Mode.Toast)
        {
            ToastBox.Visibility = Visibility.Visible;
        }

        // The shape, its corners and the wings all spring with the same curve from their current values (no From), so a
        // mid-flight retarget carries on from where the shape is. Nothing here changes the layout.
        var rect = new RectAnimation(ToRect(PillGeometry.ShapeRect(width, height)), spring) { EasingFunction = new SpringEase(curve) };
        Spring(_shape, RectangleGeometry.RadiusXProperty, radius, curve, spring);
        Spring(_shape, RectangleGeometry.RadiusYProperty, radius, curve, spring);
        var offset = PillGeometry.WingOffset(width);
        Spring(LeftWingShift, TranslateTransform.XProperty, offset, curve, spring);
        Spring(RightWingShift, TranslateTransform.XProperty, -offset, curve, spring);

        var fadeSeconds = Math.Min(curve.Settle, MaxFadeDuration.TotalSeconds);
        var fade = new SpringCurve(fadeSeconds, 1) { EaseInOut = true };
        var duration = new Duration(TimeSpan.FromSeconds(fadeSeconds));
        Fade(CardLayer, OpacityProperty, mode == Mode.Open ? 1 : 0, duration, fade);
        Fade(ToastBox, OpacityProperty, mode == Mode.Toast ? 1 : 0, duration, fade);
        Fade(TopRow, OpacityProperty, mode == Mode.Toast ? 0 : 1, duration, fade);
        // The card blurs only while it closes, and the blur is attached only while it moves: an open (and reduced motion) fades on
        // opacity alone, so no effect pass runs for it.
        if (mode == Mode.Open || reduced)
        {
            _cardBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            _cardBlur.Radius = 0;  // so a later close blurs from 0 up to 6
            CardLayer.Effect = null;
        }
        else
        {
            CardLayer.Effect = _cardBlur;
            Fade(_cardBlur, BlurEffect.RadiusProperty, 6, duration, fade);
        }

        // The shape's spring is the longest of these, so its completion is the end of the animation. The handler is attached
        // before the animation starts: WPF does not raise Completed for a handler added after BeginAnimation.
        rect.Completed += (_, _) =>
        {
            if (token == _animation)
            {
                _closing = false;
                _animating = false;
                FrameLog.End();
                CardLayer.Effect = null;
                if (mode != Mode.Open)
                {
                    CardLayer.Visibility = Visibility.Hidden;
                }
                if (mode != Mode.Toast)
                {
                    ToastBox.Visibility = Visibility.Hidden;
                }
                ApplyAppearance();
                ReevaluateClickThrough();
                if (_openRefreshPending)
                {
                    // The publishes that came during the open are applied now, once: the sections' height is measured again.
                    _openRefreshPending = false;
                    if (_open)
                    {
                        Animate(Mode.Open);
                    }
                }
            }
        };
        _shape.BeginAnimation(RectangleGeometry.RectProperty, rect);
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

    /// The open pill's height: the top row, then the body (the sections, or the "Nothing running" note when none has anything),
    /// up to MaxOpenHeight. The body's DesiredSize holds its own margins (the 26 DIP header above, the 32 DIP quick bar below)
    /// and its padding, so they are counted once, here. It is measured again only after the sections changed or changed size
    /// (see the constructor and SyncSections); a publish that leaves them as they are reuses the last height.
    private double OpenHeight()
    {
        if (_openHeightValid)
        {
            return _openHeight;
        }
        FrameworkElement body = SectionList.Children.Count > 0 ? SectionScroll : EmptyNote;
        body.Measure(new Size(PillGeometry.OpenWidth, double.PositiveInfinity));
        _openHeight = Math.Min(PillGeometry.MaxOpenHeight, PillGeometry.ClosedHeight + body.DesiredSize.Height);
        _openHeightValid = true;
        return _openHeight;
    }

    /// The toast's measured height for its current text.
    private double ToastHeight()
    {
        ToastBox.Measure(new Size(ToastBox.Width, double.PositiveInfinity));
        return ToastBox.DesiredSize.Height;
    }

    private static void Fade(IAnimatable target, DependencyProperty property, double to, Duration duration, SpringCurve fade) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = new SpringEase(fade) });

    /// Springs a property to a value from its current value. The duration is given (the shape's, for the shape's spring).
    private static void Spring(IAnimatable target, DependencyProperty property, double to, SpringCurve curve, Duration duration) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = new SpringEase(curve) });

    private static Rect ToRect((double X, double Y, double W, double H) r) => new(r.X, r.Y, r.W, r.H);

    /// Sends a horizontal wheel turn to the RowScroller under the pointer. False when the pointer is over no row.
    private bool RouteHorizontalWheel(nint lParam, nint wParam)
    {
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        if (delta == 0 || PresentationSource.FromVisual(this) is null)
        {
            return false;
        }
        var screen = new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        if (VisualTreeHelper.HitTest(this, PointFromScreen(screen))?.VisualHit is not DependencyObject hit)
        {
            return false;
        }
        for (DependencyObject? node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is RowScroller row)
            {
                row.ScrollNotches(delta);
                return true;
            }
        }
        return false;
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

    /// Hit test in the animated geometry: the shape's current rectangle and corner radius.
    private bool IsInsidePill(NativePoint screenPoint) => IsInsideRect(screenPoint, _shape.Rect, _shape.RadiusX);

    private bool IsInsideShape(NativePoint screenPoint, double width, double height, double radius) =>
        IsInsideRect(screenPoint, ToRect(PillGeometry.ShapeRect(width, height)), radius);

    private bool IsInsideRect(NativePoint screenPoint, Rect layoutRect, double radius)
    {
        var r = OnWindow(layoutRect);
        return PillHitTest.InRoundedRect(ToShapePoint(screenPoint), r.X, r.Y, r.Width, r.Height, radius);
    }

    /// The shape's rectangle in the window: the layout is centred in the window, and the shape sits in the layout.
    private static Rect OnWindow(Rect layoutRect) =>
        new(layoutRect.X + (PillGeometry.WindowWidth - PillGeometry.LayoutWidth) / 2, layoutRect.Y, layoutRect.Width, layoutRect.Height);

    /// True within NearMargin of the pill's current shape, or of the shape it springs to while an animation runs.
    private bool IsNearShape(NativePoint screenPoint)
    {
        var p = ToShapePoint(screenPoint);
        if (PillHitTest.DistanceToShape(p, OnWindow(_shape.Rect), _shape.RadiusX) <= NearMargin)
        {
            return true;
        }
        return _animating
            && PillHitTest.DistanceToShape(p, OnWindow(ToRect(PillGeometry.ShapeRect(_targetWidth, _targetHeight))), _targetRadius) <= NearMargin;
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
            _openHeightValid = false;
            // A section that is gone is removed at once: the shape's spring already moves. A section that is new fades in (no scale).
            var wanted = new HashSet<object>(sections, ReferenceEqualityComparer.Instance);
            for (var i = SectionList.Children.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(SectionList.Children[i]))
                {
                    SectionList.Children.RemoveAt(i);
                }
            }
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (SectionList.Children.Contains(section))
                {
                    if (SectionList.Children.IndexOf(section) != i)
                    {
                        SectionList.Children.Remove(section);
                        SectionList.Children.Insert(i, section);
                    }
                    continue;
                }
                SectionList.Children.Insert(i, section);
                if (_open)
                {
                    PillMotion.Enter(section, scale: false, _state.Settings.Motion, SystemParameters.ClientAreaAnimation);  // a closed pill shows nothing to enter
                }
            }
        }
        for (var i = 0; i < sections.Count; i++)
        {
            sections[i].Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
        }
        SectionScroll.Visibility = sections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncEmptyNote(sections.Count == 0);
    }

    /// The body when no section has anything: "Nothing running", as the Mac's expandedView shows it, but only with no badge or
    /// chip (the Mac's hasCollapsed). The note keeps its margins even without text, so the open height still counts them.
    private void SyncEmptyNote(bool noSections)
    {
        _openHeightValid = false;  // the body (the note or the sections) may have changed
        var showText = noSections && !_hasCollapsed;
        EmptyNote.Visibility = noSections ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = L10n.L("Nothing running");
        EmptyText.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        EmptyNote.Padding = showText ? new Thickness(0, 6, 0, 6) : new Thickness(0);
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
            RefreshOpen();  // the open pill's height follows its sections; the same height again does nothing
        }
        ApplyAppearance();
    }

    /// The open pill follows a publish: it is retargeted now, unless an open is running, in which case the publish is applied once
    /// when the open finishes (a publish during the first frames would otherwise restart the spring each time).
    private void RefreshOpen()
    {
        if (_animating && _targetMode == Mode.Open)
        {
            _openRefreshPending = true;
            return;
        }
        Animate(Mode.Open);
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
        SyncSections();  // the "Nothing running" note follows the badges and chips it is hidden by
        if (_open)
        {
            RefreshOpen();  // the open pill's height follows the note
        }
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
        var fill = ShapeFillFor(background, settings);
        if (!ReferenceEquals(ShapeFill.Fill, fill))
        {
            ShapeFill.Fill = fill;  // assigned only when the brush changed: a publish with the same look assigns nothing
        }
        Tint.Opacity = background switch
        {
            "glass" => 0.4,
            "gradient" => 0.3,
            _ => 0,
        };

        // The shadow and glow effects live on the backing borders behind the pill; setting them here is per state change only.
        ShadowShape.Opacity = PillShape.Opacity;
        ShadowShape.Effect = _open ? _openShadow : _closedShadow;

        var glow = settings.Background == "glow" && (_open || _hasCollapsed);
        var accentBrush = AccentBrush();
        GlowBacking.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(GlowBacking.Stroke, accentBrush))
        {
            GlowBacking.Stroke = accentBrush;
        }
        if (_glow is null || _glow.Color != _accent)
        {
            _glow = new DropShadowEffect { Color = _accent, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 };
        }
        GlowBacking.Effect = _glow;
        GlowRing.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(GlowRing.Stroke, accentBrush))
        {
            GlowRing.Stroke = accentBrush;
        }
    }

    /// The pill's black surface for a background: the glass and gradient brushes are made once and kept while their inputs hold.
    private Brush ShapeFillFor(string background, SettingsStore settings) => background switch
    {
        "glass" => _glassBrush ??= Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1E))),
        "gradient" => GradientFor(settings.GradientA, settings.GradientB),
        _ => Brushes.Black,
    };

    /// The gradient surface, made again only when its two colours change.
    private LinearGradientBrush GradientFor(string from, string to)
    {
        var key = from + "|" + to;
        if (_gradientBrush is null || _gradientKey != key)
        {
            _gradientKey = key;
            _gradientBrush = Frozen(new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString(from)!,
                (Color)ColorConverter.ConvertFromString(to)!,
                new Point(0, 0),
                new Point(1, 1)));
        }
        return _gradientBrush;
    }

    /// The glow's accent brush (90 % opaque), made again only when the accent changes.
    private SolidColorBrush AccentBrush()
    {
        if (_accentBrush is null || _accentBrush.Color != _accent)
        {
            _accentBrush = Frozen(new SolidColorBrush(_accent) { Opacity = 0.9 });
        }
        return _accentBrush;
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
            _open = false;  // a deferred open step still queued must not run after the close
        }
        _pollTimer.Stop();
        _closeTimer.Stop();
        _waitTimer.Stop();
        _toastTimer.Stop();
        FrameLog.End();  // an animation cut short by the close still writes its line, and no Rendering handler outlives the window
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _state.PropertyChanged -= OnStateChanged;
        _state.Asks.CollectionChanged -= OnAsksChanged;
        _state.NotchChanged -= OnNotchChanged;
        _state.LoadChanged -= SyncHeader;
        _state.ThermalChanged -= SyncHeader;
        _state.Toasts.Changed -= OnToastsChanged;
        L10n.Changed -= OnLanguageChanged;
        // Only this pill's own builder: a newer pill may have registered its own since.
        SectionRegistry.Unregister("agents", _agentsSection);
        SectionRegistry.Unregister("timer", _timerSection);
        SectionRegistry.Unregister("calls", _callSection);
        SectionRegistry.Unregister("privacy", _privacySection);
        SectionRegistry.Unregister("ci", _ciSection);
        SectionRegistry.Unregister("media", _mediaSection);
        SectionRegistry.Unregister("devices", _devicesSection);
        SectionRegistry.Unregister("shelf", _shelfSection);
        SectionRegistry.Unregister("mirror", _mirrorSection);
        _state.ShelfPickStarting -= ClosePill;
        _shelfLeaveTimer.Stop();
    }

    /// Re-reads the pill's own texts after a language change. The ask cards and the toast keep the text they were made
    /// with until they are made again: rebuilding a card would lose what was picked in it.
    private void OnLanguageChanged()
    {
        SyncHeader();
        RebuildChips();
        SyncEmptyNote(SectionList.Children.Count == 0);
        ApplyAppearance();
    }
}
