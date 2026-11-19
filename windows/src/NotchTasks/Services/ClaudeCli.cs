using System.IO;
using System.Diagnostics;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;

namespace NotchTasks.Services;

/// Runs `claude -p /usage --no-session-persistence` for the limits (Mac: Model.refreshClaudeUsage; Electron: runClaudeUsage).
/// It runs in the temp folder with no window, its stderr is discarded, and it is killed after 30 s. It returns null on any
/// failure. Only one call runs at a time: a call made while one is running gives null at once.
public sealed class ClaudeCli : IClaudeCli
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly string _home;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// home is the user's profile folder, searched for the claude CLI (AgentSetup.FindCli).
    public ClaudeCli(string home)
    {
        _home = home;
    }

    public Task<string?> UsageAsync(CancellationToken ct) => Task.Run(() => Run(ct));

    private string? Run(CancellationToken ct)
    {
        if (!_gate.Wait(0))
        {
            return null;
        }
        try
        {
            var cli = AgentSetup.FindCli("claude", _home);
            // a .cmd or .ps1 needs a shell, which is not started here (as LmsRunner does)
            if (cli is null || !cli.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var info = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            info.ArgumentList.Add("-p");
            info.ArgumentList.Add("/usage");
            info.ArgumentList.Add("--no-session-persistence");
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var stop = ct.Register(() => Kill(process));
            if (!process.WaitForExit(Limit))
            {
                Kill(process);
                return null;
            }
            // the text is used whatever the exit code, as the Mac and Electron do
            return output.Wait(TimeSpan.FromSeconds(1)) ? output.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // it has just exited
        }
    }
}
