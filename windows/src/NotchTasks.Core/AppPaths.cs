namespace NotchTasks.Core;

/// Where the app keeps its files: %APPDATA%\NotchTasks, or ~\.notchtasks when APPDATA is unset (as the Electron build does).
public static class AppPaths
{
    private static string? _root;

    /// The data folder. Defaults to the location above; tests point it at a temp folder.
    public static string Root
    {
        get => _root ?? DefaultRoot();
        set => _root = value;
    }

    public static string DataDir => Root;

    public static string AsksDir => Path.Combine(DataDir, "Asks");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string StateFile => Path.Combine(DataDir, "state.json");

    public static string PidFile => Path.Combine(DataDir, "app.pid");

    /// Where the visitor came from, written by install.ps1 (the site link's ?src=); read once for first_launch.
    public static string InstallSourceFile => Path.Combine(DataDir, "install-src");

    private static string DefaultRoot()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrEmpty(appData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".notchtasks")
            : Path.Combine(appData, "NotchTasks");
    }
}
