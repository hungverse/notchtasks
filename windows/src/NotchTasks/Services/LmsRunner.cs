using System.Diagnostics;

namespace NotchTasks.Services;

/// Runs `lms ps --json` for LocalModels.LmStudioLoaded. This is the app's side of that call: Core never starts a process
/// (Electron: providers.js lmStudioLoaded, execFileSync with a 5 s timeout and stdio ignored on stderr).
public static class LmsRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// The stdout of `<exe> ps --json`, or null when it fails, exits non-zero, times out, or is a .ps1 (a script needs a shell,
    /// which is not started here). Every exception gives null, AggregateException included.
    public static string? PsJson(string exe)
    {
        if (exe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("ps");
            info.ArgumentList.Add("--json");
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            // both pipes are read while it runs, so a chatty process cannot stall on a full pipe
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            if (process.ExitCode != 0 || !output.Wait(TimeSpan.FromSeconds(1)))
            {
                return null;
            }
            return output.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
