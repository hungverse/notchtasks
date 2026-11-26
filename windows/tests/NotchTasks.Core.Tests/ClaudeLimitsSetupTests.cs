using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The Plan limits row's state, the sign-in check of `claude -p /usage` output, and the installer command. The text samples are
/// the outputs seen on this PC: the signed-in one, the cost-only one of a signed-out run, and an empty one.
public sealed class ClaudeLimitsSetupTests
{
    private const string SignedIn =
        "You are currently using your subscription to power your Claude Code usage\n\n"
        + "Current session: 56% used · resets Nov 27, 5:09am (Asia/Ho_Chi_Minh)\n"
        + "Current week (all models): 34% used · resets Dec 3, 1:59pm (Asia/Ho_Chi_Minh)\n";

    private const string CostOnly =
        "Total cost: $0.00\nTotal duration (API): 0s\nUsage: 0 input, 0 output, 0 cache read, 0 cache write\n";

    [Theory]
    [InlineData(false, false, false, ClaudeLimitsState.Off)]
    [InlineData(false, true, false, ClaudeLimitsState.Off)]
    [InlineData(true, false, false, ClaudeLimitsState.Off)]
    [InlineData(true, true, false, ClaudeLimitsState.Off)]
    [InlineData(false, false, true, ClaudeLimitsState.NotInstalled)]
    [InlineData(false, true, true, ClaudeLimitsState.NotInstalled)]
    [InlineData(true, false, true, ClaudeLimitsState.SignInNeeded)]
    [InlineData(true, true, true, ClaudeLimitsState.Working)]
    public void State_EveryCombination(bool cliFound, bool signedIn, bool enabled, ClaudeLimitsState expected)
    {
        Assert.Equal(expected, ClaudeLimitsSetup.State(cliFound, signedIn, enabled));
    }

    [Fact]
    public void State_OffWinsOverEverythingElse()
    {
        Assert.Equal(ClaudeLimitsState.Off, ClaudeLimitsSetup.State(cliFound: false, signedIn: false, enabled: false));
        Assert.Equal(ClaudeLimitsState.Off, ClaudeLimitsSetup.State(cliFound: true, signedIn: true, enabled: false));
    }

    [Fact]
    public void State_NotInstalledComesBeforeSignIn()
    {
        Assert.Equal(ClaudeLimitsState.NotInstalled, ClaudeLimitsSetup.State(cliFound: false, signedIn: false, enabled: true));
    }

    [Fact]
    public void SignedIn_TheCliPrintedItsLimits_IsSignedIn()
    {
        Assert.True(ClaudeLimitsSetup.SignedIn(SignedIn));
    }

    [Fact]
    public void SignedIn_CostOnlyOutput_IsNotSignedIn()
    {
        Assert.False(ClaudeLimitsSetup.SignedIn(CostOnly));
    }

    [Fact]
    public void SignedIn_EmptyOrMissingOutput_IsNotSignedIn()
    {
        Assert.False(ClaudeLimitsSetup.SignedIn(""));
        Assert.False(ClaudeLimitsSetup.SignedIn("   \n"));
        Assert.False(ClaudeLimitsSetup.SignedIn(null));
    }

    [Fact]
    public void SignedIn_AskingToLogIn_IsNotSignedIn()
    {
        var asking = "Current session: 5% used · resets Nov 27, 5:09am (Asia/Ho_Chi_Minh)\nPlease run /login to continue\n";
        Assert.False(ClaudeLimitsSetup.SignedIn(asking));
    }

    [Fact]
    public void InstallerCommand_IsAnthropicsOfficialInstallerExactly()
    {
        Assert.Equal("powershell", ClaudeLimitsSetup.InstallerFile);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"",
            ClaudeLimitsSetup.InstallerArguments);
    }

    [Fact]
    public void Status_StartsUnknown_AndRecordsTheReading()
    {
        var status = new ClaudeLimitsStatus();
        Assert.Null(status.SignedIn);
        Assert.Null(status.ReadAt);

        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var raised = 0;
        status.Changed += () => raised++;

        status.Record(CostOnly, at);
        Assert.False(status.SignedIn);
        Assert.Null(status.ReadAt);

        status.Record(SignedIn, at);
        Assert.True(status.SignedIn);
        Assert.Equal(at, status.ReadAt);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Status_NullOutputIsNoAnswer_SoItChangesNothing()
    {
        var status = new ClaudeLimitsStatus();
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        status.Record(SignedIn, at);

        status.Record(null, at.AddMinutes(5));

        Assert.True(status.SignedIn);
        Assert.Equal(at, status.ReadAt);
    }

    [Fact]
    public void Classify_SignedInSample_IsSignedIn()
    {
        Assert.Equal(ClaudeReply.SignedIn, ClaudeLimitsSetup.Classify(SignedIn));
    }

    [Fact]
    public void Classify_CostSummary_IsSignedOut()
    {
        Assert.Equal(ClaudeReply.SignedOut, ClaudeLimitsSetup.Classify(CostOnly));
        Assert.Equal(ClaudeReply.SignedOut, ClaudeLimitsSetup.Classify("Usage: 0 input, 0 output\n"));
        Assert.Equal(ClaudeReply.SignedOut, ClaudeLimitsSetup.Classify("Total cost: $0.00\n"));
    }

    [Theory]
    [InlineData("Not logged in · Run /login")]
    [InlineData("Please log in to continue")]
    [InlineData("Run /login to start")]
    [InlineData("NOT LOGGED IN")]
    [InlineData("you must Log In first")]
    public void Classify_LogInMessage_IsSignedOut(string text)
    {
        Assert.Equal(ClaudeReply.SignedOut, ClaudeLimitsSetup.Classify(text));
    }

    [Fact]
    public void Classify_LogInMessageWinsOverALimitsLine()
    {
        var text = "Current session: 5% used · resets Nov 27, 5:09am (Asia/Ho_Chi_Minh)\nPlease run /login\n";
        Assert.Equal(ClaudeReply.SignedOut, ClaudeLimitsSetup.Classify(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("hello there")]
    [InlineData("some unrelated text\nwith two lines")]
    public void Classify_NothingClear_IsUnknown(string? text)
    {
        Assert.Equal(ClaudeReply.Unknown, ClaudeLimitsSetup.Classify(text));
    }

    [Fact]
    public void RecheckEvery_WorkingStopsTheFastRecheck()
    {
        Assert.Null(ClaudeLimitsSetup.RecheckEvery(signedIn: true, cliFound: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    public void RecheckEvery_NotWorking_IsOneMinute(bool? signedIn, bool? cliFound)
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ClaudeLimitsSetup.RecheckEvery(signedIn, cliFound));
    }

    [Fact]
    public void Status_SignedOutReply_ClearsSignedIn_AndUnknownChangesNothing()
    {
        var status = new ClaudeLimitsStatus();
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        status.Record(SignedIn, at);

        status.Record(CostOnly, at.AddMinutes(1));
        Assert.False(status.SignedIn);

        status.Record(SignedIn, at.AddMinutes(2));
        status.Record("", at.AddMinutes(3));
        status.Record("hello", at.AddMinutes(4));
        Assert.True(status.SignedIn);
        Assert.Equal(at.AddMinutes(2), status.ReadAt);
    }

    [Fact]
    public void Status_CliFound_RaisesChangedOnlyWhenItChanges()
    {
        var status = new ClaudeLimitsStatus();
        var raised = 0;
        status.Changed += () => raised++;
        Assert.Null(status.CliFound);

        status.RecordCli(false);
        status.RecordCli(false);
        Assert.False(status.CliFound);
        Assert.Equal(1, raised);

        status.RecordCli(true);
        Assert.True(status.CliFound);
        Assert.Equal(2, raised);
    }
}
