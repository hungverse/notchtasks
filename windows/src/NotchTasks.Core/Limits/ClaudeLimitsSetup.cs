using System.Text.RegularExpressions;

namespace NotchTasks.Core.Limits;

/// What the Plan limits row shows: off, working, the CLI missing, or the CLI not signed in.
public enum ClaudeLimitsState
{
    Off,
    Working,
    NotInstalled,
    SignInNeeded,
}

/// The opt-in to read Claude's 5 h and 7 d limits from `claude -p /usage`: the row's state, and whether a CLI reply means the user
/// is signed in. The installer command is Anthropic's official one, run in a visible window only after the user's Allow.
public static partial class ClaudeLimitsSetup
{
    /// The program of Anthropic's installer, and its arguments, exactly as the setup runs them.
    public const string InstallerFile = "powershell";
    public const string InstallerArguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"";

    /// The row's state. Off wins over everything; an enabled setup with no CLI needs the install; with a CLI that is not signed in,
    /// the sign-in; else it works.
    public static ClaudeLimitsState State(bool cliFound, bool signedIn, bool enabled)
    {
        if (!enabled)
        {
            return ClaudeLimitsState.Off;
        }
        if (!cliFound)
        {
            return ClaudeLimitsState.NotInstalled;
        }
        return signedIn ? ClaudeLimitsState.Working : ClaudeLimitsState.SignInNeeded;
    }

    /// True when the output of `claude -p /usage` shows the user's limits: a "Current session" line, and no request to log in.
    /// A cost-only reply (signed out), an empty reply, or no reply (null) is not signed in.
    public static bool SignedIn(string? cliText)
    {
        if (string.IsNullOrWhiteSpace(cliText))
        {
            return false;
        }
        if (LoginRequestRe().IsMatch(cliText))
        {
            return false;
        }
        return cliText.Split('\n').Any(line => line.TrimStart().StartsWith("Current session", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"/login|log in|sign in to|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRequestRe();
}

/// What the app knows of the Claude CLI's sign-in and of the last reading of the limits. LimitsSource records each reply of
/// `claude -p /usage`, and the setup records its own checks. Changed is raised on the thread that records.
public sealed class ClaudeLimitsStatus
{
    private readonly object _gate = new();
    private bool? _signedIn;
    private DateTimeOffset? _readAt;

    public event Action? Changed;

    /// Whether the last reply showed the user's limits: null until a reply is known.
    public bool? SignedIn
    {
        get { lock (_gate) return _signedIn; }
    }

    /// When the last reply showed the user's limits, or null.
    public DateTimeOffset? ReadAt
    {
        get { lock (_gate) return _readAt; }
    }

    /// Records a reply of the CLI at `at`. A null reply (the CLI did not answer) changes nothing: it is no answer.
    public void Record(string? cliText, DateTimeOffset at)
    {
        if (cliText is null)
        {
            return;
        }
        var signedIn = ClaudeLimitsSetup.SignedIn(cliText);
        lock (_gate)
        {
            _signedIn = signedIn;
            if (signedIn)
            {
                _readAt = at;
            }
        }
        Changed?.Invoke();
    }
}
