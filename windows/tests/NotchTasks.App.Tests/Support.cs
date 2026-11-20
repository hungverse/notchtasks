using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Devices;
using NotchTasks.Services.Media;
using NotchTasks.Services.Modules;

namespace NotchTasks.AppTests;

/// Runs each WPF test on an STA thread, waits for it, and rethrows what the action threw.
///
/// All the tests share one Application, and an Application's resources belong to the thread that made it: a window
/// on another thread that reads a shared resource throws (seen when each test had a thread of its own). So the STA
/// thread is made once, hosts the Application and runs a dispatcher; each test's action runs on it, synchronously.
internal static class Sta
{
    private static readonly Lazy<Dispatcher> Ui = new(StartUiThread);

    public static void Run(Action action)
    {
        Exception? failure = null;
        Ui.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Dispatcher StartUiThread()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            WpfHost.EnsureApplication();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromMinutes(1)))
        {
            throw new TimeoutException("The WPF test thread did not start within one minute.");
        }
        return dispatcher!;
    }
}

/// The application the real App.xaml makes, without the App class. App.xaml is an Application definition: loading it
/// would create NotchTasks.App, which these tests never do. So the resources it merges are loaded by pack URI instead:
/// ThemeMode System, and ControlPanel/Controls.xaml (the shared styles every window and pane uses).
internal static class WpfHost
{
    private static readonly object Gate = new();

    public static void EnsureApplication()
    {
        lock (Gate)
        {
            if (Application.Current is not null)
            {
                return;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.ThemeMode = ThemeMode.System;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/NotchTasks;component/ControlPanel/Controls.xaml", UriKind.Absolute),
            });
        }
    }
}

/// A data folder of its own for one test: AppPaths.Root points here, so settings, state and asks never reach %APPDATA%.
internal sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Folder = Path.Combine(Path.GetTempPath(), "NotchTasks.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        AppPaths.Root = Folder;
    }

    public string Folder { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A watcher may still hold a file for a moment; the temp folder is left for the system to clear.
        }
    }
}

internal static class Fixture
{
    /// The app state over a temp settings file and a temp asks folder (both under TempRoot), with inert OS readers (Inert).
    public static AppState NewState()
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), Inert(settings));
    }

    /// The OS readers every App test runs on. Nothing here touches the real system: the Downloads folder is a folder that does
    /// not exist under the temp root (so nothing is watched), the network reads no adapter and subscribes to nothing, the power is
    /// a PC with no battery, the disk has 100 GB free, the sensors read quiet and watch nothing, the mic is not muted, and the
    /// helper is a fake that is off and never launches or pipes. Tests replace one reader with `with`.
    public static AppStateOptions Inert(SettingsStore settings) => new(
        DownloadsFolder: Path.Combine(AppPaths.Root, "NoDownloads"),
        Network: new NetworkReaders(() => new List<NetIf>(), _ => new NoopWatch()),
        Power: () => new PowerFacts(HasBattery: false, Percent: 0, Charging: false, Plugged: false),
        Health: new HealthReaders(() => new CpuTimes(0, 0, 0), () => 0.4, _ => 100L * 1_000_000_000L),
        Sensors: new SensorReaders(() => QuietSensors, _ => new NoopWatch(), _ => false, () => false),
        Helper: new SensorHelper(settings, new InertLauncher(), new InertPipe(), new InertStop(), "NotchTasksSensors.exe",
            "S-1-5-21-0-0-0-1001", 1, _ => false),
        Processes: new NoProcesses(),
        Sqlite: new NoSqlite(),
        Http: new NoHttp(),
        HttpGet: new NoHttp(),
        ClaudeCli: new NoClaudeCli(),
        Home: Path.Combine(AppPaths.Root, "Home"),
        LmsPsJson: _ => null,
        Codex: new NoCodex(),
        Ci: new CiReaders(
            new NoProcesses(),
            new NoGitHub(),
            Path.Combine(AppPaths.Root, "Home"),
            string.Empty,
            Path.Combine(AppPaths.Root, "NoGitLab"),
            Path.Combine(AppPaths.Root, "NoRunner"),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        Media: new NoMedia(),
        Devices: new NoDevices(),
        Camera: () => new NoCamera());

    /// The camera seam with no camera: a start finds none, and nothing is captured.
    public sealed class NoCamera : ICameraSession
    {
        public Task<CameraStart> Start(Action<int, int, byte[]> frame) => Task.FromResult(CameraStart.NoCamera);

        public Task Stop() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// The media seam with no SMTC: no session is listed, nothing is sent, and there is no artwork.
    public sealed class NoMedia : IMediaReader
    {
        public event Action Changed
        {
            add
            {
            }
            remove
            {
            }
        }

        public Task Start() => Task.CompletedTask;

        public void Stop()
        {
        }

        public Task<IReadOnlyList<MediaSessionFact>> Read() =>
            Task.FromResult<IReadOnlyList<MediaSessionFact>>(Array.Empty<MediaSessionFact>());

        public Task Send(string source, MediaCommand command) => Task.CompletedTask;

        public Task Seek(string source, double seconds) => Task.CompletedTask;

        public Task<byte[]?> Artwork(string source) => Task.FromResult<byte[]?>(null);
    }

    /// The device seam with no enumeration: no container is listed, and nothing is watched.
    public sealed class NoDevices : IDeviceReader
    {
        public event Action Changed
        {
            add
            {
            }
            remove
            {
            }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public Task<IReadOnlyList<DeviceFact>?> Read() => Task.FromResult<IReadOnlyList<DeviceFact>?>(Array.Empty<DeviceFact>());

        public Task<IReadOnlyList<DeviceFact>?> ReadBatteries() => Task.FromResult<IReadOnlyList<DeviceFact>?>(Array.Empty<DeviceFact>());
    }

    /// The GitHub seam with no network: every request is unanswered and there is no gh token.
    public sealed class NoGitHub : IGitHubApi
    {
        public Task<System.Text.Json.JsonDocument?> Get(string route, string? token) =>
            Task.FromResult<System.Text.Json.JsonDocument?>(null);

        public Task<string?> Token() => Task.FromResult<string?>(null);
    }

    /// The Codex seam with no codex: nothing is queued, and the window is never found.
    public sealed class NoCodex : ICodexCli
    {
        public Task<string?> QueueAsync(string thread, string message) => Task.FromResult<string?>(null);

        public bool BringToFront() => false;
    }

    /// The Codex seam that records what it was asked: each queued (thread, message), and the number of front-brings.
    public sealed class RecordingCodex : ICodexCli
    {
        public List<(string Thread, string Message)> Queued { get; } = new();

        public int Fronted { get; private set; }

        public Task<string?> QueueAsync(string thread, string message)
        {
            Queued.Add((thread, message));
            return Task.FromResult<string?>("queued");
        }

        public bool BringToFront()
        {
            Fronted++;
            return true;
        }
    }

    /// A process table with one process (named as given) whose CPU time grows on every read, so between two reads it is busy.
    public sealed class BusyProcesses : IProcessReader
    {
        private readonly string _exe;
        private long _reads;

        public BusyProcesses(string exe) => _exe = exe;

        public ProcSnapshot Snapshot()
        {
            _reads++;
            return new ProcSnapshot(new[] { new ProcInfo(100, 1, _exe, 1, _reads * 10_000_000_000L) });
        }

        public string? CommandLine(ProcInfo p) => null;

        public string? ImagePath(ProcInfo p) => null;
        public string? Cwd(ProcInfo p) => null;
    }

    /// A process table with no processes: nothing is matched and no command line is read.
    public sealed class NoProcesses : IProcessReader
    {
        public ProcSnapshot Snapshot() => new(Array.Empty<ProcInfo>());

        public string? CommandLine(ProcInfo p) => null;

        public string? ImagePath(ProcInfo p) => null;
        public string? Cwd(ProcInfo p) => null;
    }

    /// A sqlite that answers nothing: no Antigravity conversations.
    public sealed class NoSqlite : ISqliteReader
    {
        public string? Query(string dbFile, string sql) => null;
    }

    /// A local server that never answers: no Ollama, no Antigravity server.
    public sealed class NoHttp : IHttpPost, IHttpGet
    {
        public Task<string?> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, TimeSpan timeout) =>
            Task.FromResult<string?>(null);

        public Task<string?> GetAsync(string url, TimeSpan timeout) => Task.FromResult<string?>(null);
    }

    /// A Claude CLI that is not there: no usage is read.
    public sealed class NoClaudeCli : IClaudeCli
    {
        public Task<string?> UsageAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    /// Runs the UI thread's dispatcher until done() is true or the timeout passes. The background work of the sources posts its
    /// results to the dispatcher, so a test that waits for one waits here (it never blocks the thread).
    public static void PumpUntil(Func<bool> done, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    /// The sensors of a quiet PC: no mic app, no camera, not muted.
    public static readonly SensorState QuietSensors = new(Array.Empty<string>(), false, false);

    /// A watch that does nothing. It is both the sensor watch and the network watch's disposable.
    public sealed class NoopWatch : ISensorWatch, IDisposable
    {
        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class InertLauncher : IProcessLauncher
    {
        public Task<System.Diagnostics.Process?> RunAsAsync(string fileName, string arguments) =>
            Task.FromResult<System.Diagnostics.Process?>(null);
    }

    private sealed class InertPipe : ISensorPipe
    {
        public Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ThermalReading>?>(null);
    }

    private sealed class InertStop : IHelperStop
    {
        public bool Signal() => false;
    }

    /// Runs the posted work queued before this call on the UI thread (the sensor and settings callbacks post to it).
    public static void Flush() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.Background);

    /// Creates a window or page through its constructor, without the public Show that would open it.
    public static T Create<T>(params object?[] args) where T : class =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;

    /// Reads a private field of an object.
    public static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name);
        return (T)field.GetValue(target)!;
    }

    /// Calls a private method. A throw inside it reaches the test as the original exception.
    public static void Call(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        try
        {
            method.Invoke(target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }

    /// Shows a window off screen (Left and Top at -10000) without taking the focus, then lays it out.
    public static void ShowOffScreen(Window window)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = -10000;
        window.Show();
        window.UpdateLayout();
    }

    /// A named element of a window's XAML. FindName reaches the elements the generated fields hide.
    public static T Named<T>(FrameworkElement root, string name) where T : class =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"The XAML has no element named {name}.");
}
