namespace NotchTasks.Core;

/// The app's own exe: what starts at login and what the firewall rule allows (AppFiles.AppExe). Run as `dotnet NotchTasks.dll`
/// the process is dotnet.exe, so the exe is NotchTasks.exe beside the DLL then.
public static class AppExe
{
    /// The exe for a process path (Environment.ProcessPath) and the app's base folder. A dotnet host, or no path, gives
    /// NotchTasks.exe in the base folder.
    public static string Resolve(string? processPath, string baseDirectory)
    {
        if (string.IsNullOrEmpty(processPath)
            || string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(baseDirectory, "NotchTasks.exe");
        }
        return processPath;
    }
}
