using System.IO;

namespace NotchTasks.Services;

/// The files and folders the permission and agent panes point at.
public static class AppFiles
{
    /// The app's own exe: what starts at login and what the firewall rule allows. Run as `dotnet NotchTasks.dll` the process
    /// is dotnet.exe, which is never used (NotchTasks.Core.AppExe).
    public static string AppExe => NotchTasks.Core.AppExe.Resolve(Environment.ProcessPath, AppContext.BaseDirectory);

    /// The exe the agents' hooks run.
    public static string HookExe => Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe");

    /// The temperature helper next to the app. It runs elevated only after the user's click (SensorHelper).
    public static string SensorExe => Path.Combine(AppContext.BaseDirectory, NotchTasks.Core.Sensors.SensorNames.ExeName);

    /// The user's profile folder: where the agents keep their settings.
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
