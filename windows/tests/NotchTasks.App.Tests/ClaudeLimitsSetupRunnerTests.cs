using NotchTasks.Core.Limits;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The Plan limits setup: Allow runs the installer only when the CLI is missing, and the terminal for sign-in only when the CLI
/// says it is not signed in. Nothing starts when it is all set. The process starter is a fake; no process is started here.
public sealed class ClaudeLimitsSetupRunnerTests
{
    private const string SignedInText =
        "You are currently using your subscription to power your Claude Code usage\n\n"
        + "Current session: 56% used · resets Nov 27, 5:09am (Asia/Ho_Chi_Minh)\n";

    private const string CostOnlyText = "Total cost: $0.00\nUsage: 0 input, 0 output\n";

    private static readonly CliCommand Exe = new(CliKind.Direct, @"C:\Users\me\AppData\Local\Claude-Data\claude_x\claude-code\2.1.326\h\claude.exe", Array.Empty<string>());

    /// A host whose CLI, usage reply and processes are set by the test. The installer makes the CLI appear when InstallsTo is set.
    internal sealed class FakeHost : IClaudeSetupHost
    {
        private int _probes;

        public CliCommand? Cli { get; set; }
        public CliCommand? InstallsTo { get; set; }
        public string? Usage { get; set; }
        public TaskCompletionSource<bool> Exit { get; } = new();
        public bool ExitAtOnce { get; set; } = true;
        public List<string> InstallerCalls { get; } = new();
        public List<CliCommand> SignInCalls { get; } = new();
        public int Probes => Volatile.Read(ref _probes);
        public Action<int>? OnProbe { get; set; }

        public CliCommand? FindCli() => Cli;

        public string? ProbeUsage(CliCommand cli)
        {
            var n = Interlocked.Increment(ref _probes);
            OnProbe?.Invoke(n);
            return Usage;
        }

        public Task? StartInstaller(string file, string arguments)
        {
            InstallerCalls.Add(file + "|" + arguments);
            Cli = InstallsTo;
            return Task.CompletedTask;
        }

        public Task? StartSignIn(CliCommand cli)
        {
            SignInCalls.Add(cli);
            if (ExitAtOnce)
            {
                Exit.TrySetResult(true);
            }
            return Exit.Task;
        }
    }

    private static ClaudeLimitsSetupRunner Runner(FakeHost host, ClaudeLimitsStatus? status = null) =>
        new(host, status ?? new ClaudeLimitsStatus(), TimeSpan.FromMilliseconds(5));

    private static async Task Within(Task task)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(task, done);
        await task;
    }

    [Fact]
    public async Task Allow_CliMissing_StartsTheInstallerCommandExactly()
    {
        var host = new FakeHost { Cli = null, InstallsTo = Exe, Usage = SignedInText };
        var status = new ClaudeLimitsStatus();

        await Within(Runner(host, status).AllowAsync());

        Assert.Equal(new[] { "powershell|-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"" },
            host.InstallerCalls);
        Assert.Empty(host.SignInCalls);
        Assert.True(status.SignedIn);
    }

    [Fact]
    public async Task Allow_CliPresentButSignedOut_StartsTheTerminalWithTheResolvedExe()
    {
        var host = new FakeHost { Cli = Exe, Usage = CostOnlyText };
        var status = new ClaudeLimitsStatus();

        await Within(Runner(host, status).AllowAsync());

        Assert.Empty(host.InstallerCalls);
        var call = Assert.Single(host.SignInCalls);
        Assert.Equal(Exe.FileName, call.FileName);
        Assert.False(status.SignedIn);
    }

    [Fact]
    public async Task Allow_SignedIn_StartsNothing()
    {
        var host = new FakeHost { Cli = Exe, Usage = SignedInText };
        var status = new ClaudeLimitsStatus();

        await Within(Runner(host, status).AllowAsync());

        Assert.Empty(host.InstallerCalls);
        Assert.Empty(host.SignInCalls);
        Assert.Equal(1, host.Probes);
        Assert.True(status.SignedIn);
        Assert.NotNull(status.ReadAt);
    }

    [Fact]
    public async Task Allow_CliMissingAndInstallFails_StartsNothingElse()
    {
        var host = new FakeHost { Cli = null, InstallsTo = null, Usage = SignedInText };

        await Within(Runner(host).AllowAsync());

        Assert.Single(host.InstallerCalls);
        Assert.Empty(host.SignInCalls);
        Assert.Equal(0, host.Probes);
    }

    [Fact]
    public async Task WhileTheTerminalLives_TheCliIsCheckedAgain_UntilSignedIn()
    {
        var host = new FakeHost { Cli = Exe, Usage = CostOnlyText, ExitAtOnce = false };
        var status = new ClaudeLimitsStatus();
        // the user signs in after the third check: the watch stops there, with the terminal still open
        host.OnProbe = n =>
        {
            if (n >= 3)
            {
                host.Usage = SignedInText;
            }
        };

        await Within(Runner(host, status).AllowAsync());

        Assert.Single(host.SignInCalls);
        Assert.False(host.Exit.Task.IsCompleted);
        Assert.True(status.SignedIn);
        Assert.Equal(3, host.Probes);
        host.Exit.TrySetResult(true);
    }

    [Fact]
    public async Task WhenTheTerminalExits_TheCliIsCheckedOnceMore_AndTheWatchEnds()
    {
        var host = new FakeHost { Cli = Exe, Usage = CostOnlyText, ExitAtOnce = false };
        var status = new ClaudeLimitsStatus();
        var sawExitAtLastCheck = false;
        host.OnProbe = _ => sawExitAtLastCheck = host.Exit.Task.IsCompleted;
        var watch = Within(Runner(host, status).AllowAsync());

        await Task.Delay(30);
        host.Exit.TrySetResult(true);
        await watch;

        // the last check is the one made after the terminal went away, and the user is still not signed in
        Assert.True(sawExitAtLastCheck);
        Assert.False(status.SignedIn);
    }

    [Fact]
    public async Task InstallButton_RunsTheInstallerAndNoSignIn()
    {
        var host = new FakeHost { Cli = null, InstallsTo = Exe, Usage = CostOnlyText };

        await Within(Runner(host).InstallAsync());

        Assert.Single(host.InstallerCalls);
        Assert.Empty(host.SignInCalls);
    }

    [Fact]
    public async Task SignInButton_OpensTheTerminalOnly()
    {
        var host = new FakeHost { Cli = Exe, Usage = CostOnlyText };

        await Within(Runner(host).SignInAsync());

        Assert.Empty(host.InstallerCalls);
        Assert.Single(host.SignInCalls);
    }

    [Fact]
    public async Task ASecondStepWhileOneRuns_StartsNothing()
    {
        var host = new FakeHost { Cli = Exe, Usage = CostOnlyText, ExitAtOnce = false };
        var runner = Runner(host);
        var first = runner.SignInAsync();
        await Task.Delay(20);

        await Within(runner.SignInAsync());

        Assert.Single(host.SignInCalls);
        host.Usage = SignedInText;
        host.Exit.TrySetResult(true);
        await Within(first);
    }
}
