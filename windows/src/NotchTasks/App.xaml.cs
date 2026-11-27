using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Demo;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sync;
using NotchTasks.Core.Updates;
using NotchTasks.Demo;
using NotchTasks.Onboarding;
using NotchTasks.Pill;
using NotchTasks.Services;
using NotchTasks.Services.Notifications;

namespace NotchTasks;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private SettingsStore? _settings;
    private AppState? _state;
    private Tray? _tray;
    private PillWindow? _pill;
    private bool _primary;
    private DispatcherTimer? _demoTimer;  // the demo run's clock: it plays the timeline while the app runs

    /// The running app.
    public static App Instance => (App)Current;

    /// The app state. Available after start-up.
    public AppState State => _state ?? throw new InvalidOperationException("The app has not started.");

    /// True while the app is quitting: set by Quit (the tray's and the pill's Quit) and by the end of the Windows session, before
    /// the windows close. A window that closes with the app reads it: the first-run guide then stays unfinished, and the pill is
    /// not shown on the way out. Dispatcher.HasShutdownStarted is not enough: it is still false while the windows close.
    public static bool Quitting { get; set; }

    /// Quits the app. Quitting goes up first, so the windows that close with the app see it.
    public static void Quit()
    {
        Quitting = true;
        Instance.Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Quitting = true;
        base.OnSessionEnding(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));

        // `--quit` (the installer, before it installs or removes the app) comes first. A running app is asked to quit, through its
        // normal exit path, and this process waits up to 5 s for it to exit. With no app running, there is nothing to quit: this
        // process exits at once, holding nothing. It always exits 0: the installer waits for it, and a dialog would hang that.
        if (e.Args.Contains("--quit"))
        {
            QuitRunningApp();
            Shutdown(0);
            return;
        }
        // `--demo` is a run of its own, for the README's recording: no single instance, tray, hooks, sync, update check or autostart.
        if (e.Args.Contains("--demo"))
        {
            StartDemo(Environment.GetEnvironmentVariable("NT_FRAMES"));
            return;
        }
        // The app's identity before any window or toast: Windows reads the AUMID from the process, so the toasts and the taskbar button
        // belong to the installed app (the installer's shortcut carries the same id).
        AppIdentity.Apply();
        // Before anything else: a second launch exits at once and creates nothing. First it asks the first instance to show
        // its window (the guide, or the Control Panel).
        try
        {
            _instance = new SingleInstance();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            Shutdown(1);
            return;
        }
        if (!_instance.IsFirst)
        {
            _instance.SignalFirst();
            Shutdown(0);
            return;
        }
        _primary = true;

        try
        {
            Start();
        }
        catch (Exception ex)
        {
            // A failed start must not leave an invisible app holding the mutex: log it and exit with code 1.
            ErrorLog.Write(ex);
            Shutdown(1);
        }
    }

    /// The demo run: the pill with the made-up state of DemoSession, and nothing else of the app. Its data folder is a temp folder of its
    /// own, so no file under the user's data folder is read or written, and the settings are the defaults in memory. Without NT_FRAMES
    /// the pill runs its timeline until it is quit. With NT_FRAMES=<folder> one loop is captured to that folder (DemoCapture), then the app exits.
    private void StartDemo(string? framesFolder)
    {
        _primary = true;  // the exit path closes the pill and the state, as a normal run does
        AppPaths.Root = Path.Combine(Path.GetTempPath(), "NotchTasks.Demo", Guid.NewGuid().ToString("N"));
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.Handled = true;
        };
        L10n.Init(Path.Combine(AppContext.BaseDirectory, "l10n"), string.Empty);

        var state = DemoSession.NewState();
        _state = state;
        _pill = new PillWindow(state);
        var session = new DemoSession(state, _pill.DemoPressAllow);
        session.Start(DateTimeOffset.Now);
        _pill.Show();

        if (!string.IsNullOrWhiteSpace(framesFolder))
        {
            var content = (FrameworkElement)_pill.Content;
            DemoCapture.Run(content, framesFolder, DemoTimeline.FrameCount, DemoTimeline.Fps,
                time => session.Tick(time, DateTimeOffset.Now), _pill.DpiScale * 2);  // 2x the monitor's scale: the sharp copy
            Shutdown(0);
            return;
        }
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => session.Tick(clock.Elapsed, DateTimeOffset.Now);
        timer.Start();
        _demoTimer = timer;
    }

    /// The installer's `--quit`: asks the running app to quit and waits up to 5 s for it. Never throws, so the process always exits 0
    /// without a crash dialog.
    private static void QuitRunningApp()
    {
        try
        {
            using var instance = new SingleInstance();
            if (!instance.IsFirst)
            {
                instance.SignalQuit();
                instance.WaitForFirstExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    private void Start()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        _settings = settings;
        var l10nFolder = Path.Combine(AppContext.BaseDirectory, "l10n");
        L10n.Init(l10nFolder, settings.Language);
        // A language change applies at once: the texts re-read on the UI thread (the Changed event may come from any thread).
        settings.Changed += keys =>
        {
            if (keys.Contains("language"))
            {
                Dispatcher.BeginInvoke(new Action(() => L10n.Init(l10nFolder, settings.Language)));
            }
            // The tray icon follows the menuBarIcon setting at once.
            if (keys.Contains("menuBarIcon"))
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyTrayVisibility(settings)));
            }
        };
        Sounds.Attach(settings);

        var store = new AskStore(AppPaths.AsksDir);
        store.MarkAppRunning(Environment.ProcessId);
        MigrateHooks();
        LoginItems.ApplyFirstRun();

        _state = new AppState(settings, store, AppStateOptions.Production with { Sync = deps => new SyncHub(deps) });
        _state.StartWatching();
        // The installers an earlier run downloaded and did not start are removed before the first check can download another.
        UpdateClient.RemoveLeftovers(Path.GetTempPath());
        _state.Updates.Start();
        _ = _state.Bridge?.StartAsync();  // the phone sync: nothing runs for an unpaired PC beyond the vault read
        // The Sync motion moment, so the animated icons are in step from the first frame.
        SpriteClock.SetEpoch(AnimationEpoch.Read(AppPaths.StateFile));
        _pill = new PillWindow(_state);
        if (settings.OnboardingDone)
        {
            _pill.Show();
        }
        else
        {
            // The first run: the guide opens and the pill stays hidden until the guide closes.
            var pill = _pill;
            OnboardingWindow.Show(_state, () => pill.Show());
        }
        _tray = new Tray(() => ControlPanelWindow.Show(_state), () => OnboardingWindow.Show(_state), Quit);
        ApplyTrayVisibility(settings);
        _instance?.Listen(() => Dispatcher.BeginInvoke(new Action(ShowFromSecondLaunch)));
        // `--quit` runs the tray's Quit on the UI thread, so OnExit removes app.pid like a Quit from the tray does.
        _instance?.ListenQuit(() => Dispatcher.BeginInvoke(new Action(() => Quit())));
    }

    /// A second launch shows this instance's window: the guide when it is open, else the Control Panel. Runs on the UI thread.
    private void ShowFromSecondLaunch()
    {
        if (_state is null)
        {
            return;
        }
        if (OnboardingWindow.IsOpen)
        {
            OnboardingWindow.Show(_state);
        }
        else
        {
            ControlPanelWindow.Show(_state);
        }
    }

    /// Shows or hides the tray icon as the menuBarIcon setting says. Runs on the UI thread.
    private void ApplyTrayVisibility(SettingsStore settings)
    {
        if (_tray is not null)
        {
            _tray.Visible = settings.MenuBarIcon;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _demoTimer?.Stop();
        if (_primary)
        {
            _pill?.Close();
            _tray?.Dispose();
            _state?.Dispose();
            _settings?.Dispose();
            RemovePidFile();
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// Moves agents' hooks from the old Electron exe to NotchTasksHook.exe, and our Claude status line to this exe when the
    /// old one is gone. One broken agent config does not stop the others.
    private static void MigrateHooks()
    {
        var hookExe = Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            try
            {
                if (HookInstaller.IsInstalled(kind, home) && !HookInstaller.PointsAt(kind, hookExe, home))
                {
                    HookInstaller.Install(kind, hookExe, home);
                }
            }
            catch (Exception e)
            {
                ErrorLog.Write(new InvalidOperationException($"NotchTasks: hook migration failed for {kind.Id()}", e));
            }
        }
        try
        {
            StatuslineInstaller.Repoint(hookExe, home);
        }
        catch (Exception e)
        {
            ErrorLog.Write(new InvalidOperationException("NotchTasks: statusline migration failed", e));
        }
    }

    /// Deletes app.pid, but only while it still names this process.
    private static void RemovePidFile()
    {
        try
        {
            var text = AtomicFile.ReadAllTextOrNull(AppPaths.PidFile);
            if (text is not null && text.Trim() == Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            {
                File.Delete(AppPaths.PidFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("NotchTasks: could not remove app.pid: " + e);
        }
    }
}
