using System.IO;
using Xunit;

// The windows are WPF objects on STA threads and share AppPaths.Root and the one Application: one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

/// Points the app's error log at a temp file before any test runs, so the suite never writes the developer's %APPDATA% log.
internal static class ErrorLogRedirect
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Redirect() =>
        NotchTasks.Services.ErrorLog.FileOverride = Path.Combine(
            Path.GetTempPath(), "NotchTasks.AppTests", "errorlog-" + Guid.NewGuid().ToString("N"), "windows-errors.log");
}
