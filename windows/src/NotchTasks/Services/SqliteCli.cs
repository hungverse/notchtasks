using System.Diagnostics;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;

namespace NotchTasks.Services;

/// Runs sqlite3 for the Antigravity scanner: `sqlite3 -readonly -json <file> <sql>`, with no shell and no window, and a 2 s limit.
/// sqlite3 is found by AgentSetup.FindCli (ruling P3b-R4: with no sqlite3 there are no Antigravity jobs).
public sealed class SqliteCli : ISqliteReader
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private readonly Lazy<string?> _exe;

    /// home is the user's profile folder, searched for sqlite3 after PATH.
    public SqliteCli(string home)
    {
        _exe = new Lazy<string?>(() => AgentSetup.FindCli("sqlite3", home));
    }

    /// The rows as sqlite3 -json prints them ("" for no rows). Null when there is no sqlite3, the query fails or times out.
    public string? Query(string dbFile, string sql)
    {
        var exe = _exe.Value;
        // a script needs a shell, which is not started here
        if (exe is null || exe.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
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
            info.ArgumentList.Add("-readonly");
            info.ArgumentList.Add("-json");
            info.ArgumentList.Add(dbFile);
            info.ArgumentList.Add(sql);
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            // both pipes are read while it runs, so a chatty process cannot stall on a full pipe
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Limit))
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
