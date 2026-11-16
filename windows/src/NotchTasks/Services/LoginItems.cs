using System.IO;
using System.Security;
using NotchTasks.Core;
using NotchTasks.Core.Permissions;

namespace NotchTasks.Services;

/// Start at login at the app's start. The first run turns it on once (as the Electron build did); a failure is logged and the
/// next start tries again.
public static class LoginItems
{
    public static void ApplyFirstRun()
    {
        try
        {
            StartAtLogin.ApplyFirstRun(new RegistryAccess(), AppFiles.AppExe, AppPaths.StateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            ErrorLog.Write(e);
        }
    }
}
