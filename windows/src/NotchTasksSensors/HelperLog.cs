namespace NotchTasksSensors;

/// The helper's error log. The first failure of a run is reported; the rest are not. It always goes to stderr. A file log is written
/// only when the helper runs from Program Files (the logon copy): %ProgramData%\NotchTasks\sensors.log. A helper started from the
/// user's own app folder (the runas modes) writes no file at all, so the user-writable folder gets no new file from it.
internal static class HelperLog
{
    private static bool _written;

    /// True when this helper runs from the Program Files copy.
    public static bool RunningFromProgramFiles =>
        string.Equals(Trim(AppContext.BaseDirectory), Trim(NotchTasks.Core.Sensors.SensorNames.ProgramFilesFolder), StringComparison.OrdinalIgnoreCase);

    public static void Once(Exception error)
    {
        if (_written)
        {
            return;
        }
        _written = true;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}";
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception)
        {
            // No stderr: nothing else is written.
        }
        if (!RunningFromProgramFiles)
        {
            return;
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NotchTasks");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "sensors.log"), line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Logging never throws.
        }
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');
}
