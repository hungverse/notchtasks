namespace NotchTasks.Core.Limits;

/// Runs `claude -p /usage --no-session-persistence` and returns its text, or null when it fails, times out or is missing.
/// The app's implementation runs the CLI (30 s limit, one call at a time); Core only parses the text (ClaudeUsage.Parse).
public interface IClaudeCli
{
    Task<string?> UsageAsync(CancellationToken ct);
}
