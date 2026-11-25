using System.IO;
using System.Security;
using NotchTasks.Core;
using NotchTasks.Core.Permissions;

namespace NotchTasks.Services;

/// Start at login at the app's start. An Electron install's Run value is moved to ours first (its on-state carries over). The
/// first run then turns it on once (as the Electron build did); a failure is logged and the next start tries again.
public static class LoginItems
{
    public static void ApplyFirstRun()
    {
        try
        {
            var registry = new RegistryAccess();
            StartAtLogin.MigrateElectron(registry, AppFiles.AppExe);
            StartAtLogin.ApplyFirstRun(registry, AppFiles.AppExe, AppPaths.StateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            ErrorLog.Write(e);
        }
    }
}
