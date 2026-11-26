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

/// What one reply of `claude -p /usage` says about the sign-in. Unknown is no clear answer (no reply, a timeout, an empty output):
/// it never changes the state and never opens the sign-in terminal.
public enum ClaudeReply
{
    Unknown,
    SignedIn,
    SignedOut,
}

/// The opt-in to read Claude's 5 h and 7 d limits from `claude -p /usage`: the row's state, what a reply says, and when the fast
/// re-check runs. The installer command is Anthropic's official one, run in a visible window only after the user's Allow.
public static partial class ClaudeLimitsSetup
{
    /// The program of Anthropic's installer, and its arguments, exactly as the setup runs them.
    public const string InstallerFile = "powershell";
    public const string InstallerArguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"";

    /// How often the sign-in and the CLI are checked again while they are not both good: every minute, in the background.
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

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

    /// What a reply says. SignedIn: a "Current session" line and no log-in request. SignedOut: a log-in request, or a cost summary
    /// ("Total cost" or "Usage:") with no limits line. Anything else, and no reply at all, is Unknown.
    public static ClaudeReply Classify(string? cliText)
    {
        if (string.IsNullOrWhiteSpace(cliText))
        {
            return ClaudeReply.Unknown;
        }
        if (LoginRequestRe().IsMatch(cliText))
        {
            return ClaudeReply.SignedOut;
        }
        if (cliText.Split('\n').Any(line => line.TrimStart().StartsWith("Current session", StringComparison.Ordinal)))
        {
            return ClaudeReply.SignedIn;
        }
        return CostSummaryRe().IsMatch(cliText) ? ClaudeReply.SignedOut : ClaudeReply.Unknown;
    }

    /// True when the reply shows the user's limits (Classify is SignedIn).
    public static bool SignedIn(string? cliText) => Classify(cliText) == ClaudeReply.SignedIn;

    /// The wait before the next check while the setup is not working: the sign-in is not known to be good, or the CLI is not known to
    /// be here. Null when both are known good: the 30 min reading is enough then.
    public static TimeSpan? RecheckEvery(bool? signedIn, bool? cliFound) =>
        signedIn == true && cliFound == true ? null : RecheckInterval;

    [GeneratedRegex(@"/login|log in|sign in to|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRequestRe();

    [GeneratedRegex(@"Total cost|^Usage:", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CostSummaryRe();
}

/// What the app knows of the Claude CLI: whether it is here, its sign-in, and the last reading of the limits. LimitsSource and the
/// setup record each reply. Unknown replies change nothing. Changed is raised on the thread that records.
public sealed class ClaudeLimitsStatus
{
    private readonly object _gate = new();
    private bool? _signedIn;
    private bool? _cliFound;
    private DateTimeOffset? _readAt;

    public event Action? Changed;

    /// Whether the last clear reply showed the user's limits: null until one is known.
    public bool? SignedIn
    {
        get { lock (_gate) return _signedIn; }
    }

    /// Whether the CLI was found at the last check: null until a check ran.
    public bool? CliFound
    {
        get { lock (_gate) return _cliFound; }
    }

    /// When the last signed-in reply came, or null.
    public DateTimeOffset? ReadAt
    {
        get { lock (_gate) return _readAt; }
    }

    /// Records a reply at `at`. A signed-in reply sets ReadAt; a signed-out one clears SignedIn; an unknown one changes nothing.
    public void Record(string? cliText, DateTimeOffset at)
    {
        var reply = ClaudeLimitsSetup.Classify(cliText);
        if (reply == ClaudeReply.Unknown)
        {
            return;
        }
        lock (_gate)
        {
            _signedIn = reply == ClaudeReply.SignedIn;
            if (reply == ClaudeReply.SignedIn)
            {
                _readAt = at;
            }
        }
        Changed?.Invoke();
    }

    /// Records whether the CLI was found. Changed is raised only when this changes the value.
    public void RecordCli(bool found)
    {
        bool changed;
        lock (_gate)
        {
            changed = _cliFound != found;
            _cliFound = found;
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }
}
