using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private SettingsStore? _settings;
    private AppState? _state;
    private Tray? _tray;
    private bool _primary;

    /// The running app.
    public static App Instance => (App)Current;

    /// The app state. Available after start-up.
    public AppState State => _state ?? throw new InvalidOperationException("The app has not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject.ToString()));

        // Before anything else: a second launch exits at once and creates nothing.
        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
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

        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        L10n.Init(Path.Combine(AppContext.BaseDirectory, "l10n"), _settings.Language);
        Sounds.Attach(_settings);

        var store = new AskStore(AppPaths.AsksDir);
        store.MarkAppRunning(Environment.ProcessId);
        MigrateHooks();

        _state = new AppState(_settings, store);
        _state.StartWatching();
        _tray = new Tray(() => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_primary)
        {
            _tray?.Dispose();
            _state?.Dispose();
            _settings?.Dispose();
            RemovePidFile();
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// Moves agents' hooks from the old Electron exe to NotchTasksHook.exe. One broken agent config does not stop the others.
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
                Debug.WriteLine($"NotchTasks: hook migration failed for {kind.Id()}: {e}");
            }
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
