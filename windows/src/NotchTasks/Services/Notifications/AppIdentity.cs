using System.Runtime.InteropServices;

namespace NotchTasks.Services.Notifications;

/// The app's Application User Model ID. It is the id the installer gives the Start menu and desktop shortcuts
/// (installer/NotchTasks.iss), so Windows attributes the app's toasts and taskbar button to the installed app. The process
/// sets it at start-up, before any window or toast, because Windows reads it from the process, not from the shortcut.
internal static partial class AppIdentity
{
    /// The installer's AUMID. A toast posted under it shows the installed app's name and icon.
    public const string UserModelId = "com.notchtasks.windows";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// Sets the process's id. A failure is logged and the app runs on: the id only decides how Windows names the app.
    public static void Apply()
    {
        var hr = SetCurrentProcessExplicitAppUserModelID(UserModelId);
        if (hr != 0)
        {
            ErrorLog.Write(Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("SetCurrentProcessExplicitAppUserModelID failed."));
        }
    }
}
