using System.Management;
using System.Runtime.InteropServices;
using NotchTasks.Core.Levels;

namespace NotchTasks.Services.Levels;

/// The laptop panel's brightness through WMI (root\WMI, the Mac's DisplayServices read). WmiMonitorBrightness gives the level, and a
/// ManagementEventWatcher on WmiMonitorBrightnessEvent gives each change, so nothing polls. A desktop has no panel: the query is empty or
/// throws, Read is null and the bar has no brightness. Each such failure is logged once.
public sealed class WmiBrightness : IBrightness
{
    private const string Scope = @"root\WMI";
    private ManagementEventWatcher? _watcher;
    private static int _logged;  // 1 once an absence was logged: shared by every instance, so one line per app run

    /// Raised on the WMI thread with each new brightness (0 to 1) while the watch is on.
    public event Action<double>? Changed;

    /// The panel's brightness from 0 to 1, or null when no panel reports one (a desktop, or WMI refuses the query).
    public double? Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    if (item["CurrentBrightness"] is byte level)
                    {
                        return Math.Clamp(level / 100.0, 0, 1);
                    }
                }
            }
            LogOnce("no panel reports a brightness");
            return null;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            LogOnce(e);
            return null;
        }
    }

    /// Starts the change feed (on) or stops it (off). A WMI that refuses the event query is logged once and the feed stays off.
    public void Watch(bool on)
    {
        if (!on)
        {
            Stop();
            return;
        }
        if (_watcher is not null)
        {
            return;
        }
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(Scope), new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            watcher.EventArrived += OnEvent;
            watcher.Start();
            _watcher = watcher;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException)
        {
            LogOnce(e);
        }
    }

    public void Dispose() => Stop();

    /// A change: the event's own brightness when it carries one, else a fresh read.
    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        var changed = Changed;
        if (changed is null)
        {
            return;
        }
        if (e.NewEvent?["Brightness"] is byte level)
        {
            changed(Math.Clamp(level / 100.0, 0, 1));
        }
        else if (Read() is { } read)
        {
            changed(read);
        }
    }

    private void Stop()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return;
        }
        watcher.EventArrived -= OnEvent;
        watcher.Stop();
        watcher.Dispose();
    }

    /// Logs the reason once for the whole app run: a desktop has no panel, so every start would otherwise log it again.
    private static void LogOnce(string reason)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }
        ErrorLog.Write(new InvalidOperationException("NotchTasks: brightness is not shown: " + reason));
    }

    /// The exception form of LogOnce, for a WMI call that threw.
    private static void LogOnce(Exception e)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }
        ErrorLog.Write(new InvalidOperationException("NotchTasks: brightness is not shown", e));
    }
}
