using System.IO;
using System.Diagnostics;
using NotchTasks.Core.Modules;
using NotchTasks.Pill;

namespace NotchTasks.Services;

/// Brings a call app's main window forward (the Mac's openCallApp). The call's app id is mapped to the exe names that run it,
/// the first running process with a main window is restored and raised, and nothing happens when no such window is found.
internal static class CallOpener
{
    /// Restores and raises the call app's window. True when a window was found.
    public static bool Open(string appId)
    {
        foreach (var exe in ExeNames(appId))
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (process)
                {
                    var window = process.MainWindowHandle;
                    if (window == 0)
                    {
                        continue;
                    }
                    // restore only a minimised window: a normal one keeps its size and place
                    if (Win32.IsIconic(window))
                    {
                        Win32.ShowWindow(window, Win32.SW_RESTORE);
                    }
                    Win32.SetForegroundWindow(window);
                    return true;
                }
            }
        }
        return false;
    }

    /// The exe names of a call app: its id when that is an exe, else the exe ids the CallApps table gives its display name
    /// (a packaged id such as msteams_8wekyb3d8bbwe maps to ms-teams.exe and teams.exe). Empty when none is known.
    public static IReadOnlyList<string> ExeNames(string appId)
    {
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { appId };
        }
        if (!CallApps.Names.TryGetValue(appId, out var name))
        {
            return Array.Empty<string>();
        }
        return CallApps.Names
            .Where(pair => pair.Value == name && pair.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .ToList();
    }
}
