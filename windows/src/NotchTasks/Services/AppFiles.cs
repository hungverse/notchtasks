using System.IO;

namespace NotchTasks.Services;

/// The files and folders the permission and agent panes point at.
public static class AppFiles
{
    /// The app's own exe: what starts at login and what the firewall rule allows.
    public static string AppExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NotchTasks.exe");

    /// The exe the agents' hooks run.
    public static string HookExe => Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe");

    /// The user's profile folder: where the agents keep their settings.
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
