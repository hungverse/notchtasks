using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;

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
    /// The app state over a temp settings file and a temp asks folder (both under TempRoot).
    public static AppState NewState()
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir));
    }

    /// Creates a window or page through its constructor, without the public Show that would open it.
    public static T Create<T>(params object?[] args) where T : class =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;

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
