using NotchTasks.Core.Limits;

namespace NotchTasks.Services;

/// Runs `claude -p /usage --no-session-persistence` for the limits (Mac: Model.refreshClaudeUsage; Electron: runClaudeUsage),
/// through CliLauncher: the temp folder, no window, killed after 30 s. Null on any failure, and when a call is already running
/// (only one call runs at a time).
public sealed class ClaudeCli : IClaudeCli
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly string[] Args = { "-p", "/usage", "--no-session-persistence" };

    private readonly string _home;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// home is the user's profile folder, searched for the claude CLI.
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
            var command = CliLauncher.Resolve("claude", _home);
            return command is null ? null : CliLauncher.Run(command, Args, Limit, ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
