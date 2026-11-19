using System.IO;
using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The downloads module's feed. Start checks the folder (a missing folder is logged once and nothing runs), scans once for the
/// baseline, then watches the folder for created, renamed and deleted names. The watcher's events come on thread-pool threads,
/// so each is marshalled to the dispatcher, and a burst of them scans once after it settles (a trailing 400 ms debounce: each
/// event restarts the one-shot timer). Nothing polls, and no timer runs while idle. Stop disposes the watcher, stops the timer
/// and forgets the baseline, so the next Start starts again.
public sealed class DownloadsSource : IModuleSource
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly DownloadsModule _module;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce = new() { Interval = Debounce };
    private FileSystemWatcher? _watcher;
    private bool _started;
    private bool _missingLogged;
    private bool _scanFailureLogged;

    /// The source of `module`. It is made on the UI thread, which the events are marshalled to.
    public DownloadsSource(DownloadsModule module)
    {
        _module = module;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Scan();
        };
    }

    /// Checks the folder, takes the baseline and starts the watcher. A missing folder is logged once and nothing more runs.
    public void Start()
    {
        var folder = _module.Folder;
        if (!Directory.Exists(folder))
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                ErrorLog.Write(new DirectoryNotFoundException("NotchTasks: the Downloads folder is missing: " + folder));
            }
            return;
        }

        _started = true;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, _) => Post();
            watcher.Deleted += (_, _) => Post();
            watcher.Renamed += (_, _) => Post();
            // An overflow (Error) means events were lost: a rescan is the only way to catch up.
            watcher.Error += (_, _) => Post();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorLog.Write(e);
        }
        // Armed before the baseline: a file that lands in between is seen by the next scan, not lost. Events are marshalled to
        // the dispatcher, so they run after this baseline.
        Scan();
    }

    /// Disposes the watcher, stops the timer and forgets the baseline.
    public void Stop()
    {
        _started = false;
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _debounce.Stop();
        _module.Reset();
    }

    /// Called on a thread-pool thread: the change is handled on the UI thread, where it restarts the debounce.
    private void Post() => _dispatcher.BeginInvoke(new Action(OnChange));

    /// Restarts the trailing debounce: the scan runs once the events have settled.
    private void OnChange()
    {
        if (!_started)
        {
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    /// Lists the folder's files (name and creation time) and feeds the module. An IO error is logged once and the scan is
    /// skipped, so the baseline is kept.
    private void Scan()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            var folder = _module.Folder;
            var entries = Directory.EnumerateFiles(folder)
                .Select(path => (
                    Name: Path.GetFileName(path),
                    Created: new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero)))
                .ToList();
            _module.Scan(entries, DateTimeOffset.Now);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_scanFailureLogged)
            {
                _scanFailureLogged = true;
                ErrorLog.Write(e);
            }
        }
    }
}
