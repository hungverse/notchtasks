using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NotchTasks.ControlPanel;
using NotchTasks.Core.Notch;
using NotchTasks.Services;

namespace NotchTasks.Pill;

/// What a toast's click does when the toast is not an ask (Mac openToast): a Pane opens the Control Panel at that pane; a
/// downloaded file is shown in its folder and not opened (it may be an installer); any other link opens in its program.
public static class ToastActions
{
    /// Runs the toast's click action. The caller dismisses the toast afterwards.
    public static void Run(Toast t, AppState state)
    {
        if (t.Pane is { } pane && Enum.TryParse<PaneId>(pane, ignoreCase: true, out var id))
        {
            ControlPanelWindow.Show(state, id);
            return;
        }
        if (t.Url is not { } url)
        {
            return;
        }
        try
        {
            if (IsFile(url, out var path))
            {
                Launch(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Launch(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch (Win32Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    /// True when the url is a file on this PC; path is its local path. A full local path (C:\..., or a UNC path) is a file, as
    /// the downloads toast's url is; a file:// URI is one too. Anything else is a link.
    private static bool IsFile(string url, out string path)
    {
        if (Path.IsPathFullyQualified(url))
        {
            path = url;
            return true;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }
        path = url;
        return false;
    }

    /// Starts a program without waiting for it, and disposes the Process handle at once.
    private static void Launch(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
    }
}
