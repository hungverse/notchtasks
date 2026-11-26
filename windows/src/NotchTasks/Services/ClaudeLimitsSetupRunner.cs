using System.Diagnostics;
using System.IO;
using NotchTasks.Core.Limits;

namespace NotchTasks.Services;

/// What the Plan limits setup can do on this PC. The real one (ClaudeSetupHost) finds the CLI, runs `claude -p /usage` and opens
/// the visible windows; tests pass a fake, so no installer, CLI or terminal runs in a test.
public interface IClaudeSetupHost
{
    /// The CLI that would run now, or null when it is not installed.
    CliCommand? FindCli();

    /// Runs `claude -p /usage` once, in the background, and returns its text (null when it did not answer).
    string? ProbeUsage(CliCommand cli);

    /// Starts a visible window running the program with these arguments. The task completes when the window closes; null when it
    /// could not start.
    Task? StartInstaller(string file, string arguments);

    /// Starts a visible terminal that runs the CLI in the user's profile folder, so the user picks a theme and signs in. The task
    /// completes when that process exits; null when it could not start.
    Task? StartSignIn(CliCommand cli);
}

/// The Plan limits setup, in the order the user agreed to: the installer when the CLI is missing, then a terminal to sign in when
/// `claude -p /usage` shows no limits. Each step runs only when it is needed. While a sign-in terminal lives, the CLI is checked again
/// every few seconds, and once when it closes. Nothing here reads or writes a credential: the user signs in by themselves.
public sealed class ClaudeLimitsSetupRunner
{
    private static readonly TimeSpan DefaultRecheck = TimeSpan.FromSeconds(10);

    private readonly IClaudeSetupHost _host;
    private readonly TimeSpan _recheck;
    private bool _busy;

    /// host does the work; status receives each reply of the CLI; recheck is how often the sign-in is checked while the terminal lives.
    public ClaudeLimitsSetupRunner(IClaudeSetupHost host, ClaudeLimitsStatus? status = null, TimeSpan? recheck = null)
    {
        _host = host;
        Status = status ?? new ClaudeLimitsStatus();
        _recheck = recheck ?? DefaultRecheck;
    }

    /// What the app knows of the sign-in and the last reading.
    public ClaudeLimitsStatus Status { get; }

    /// The CLI that would run now, or null. The Plan limits row reads it.
    public CliCommand? Cli() => _host.FindCli();

    /// Allow: the installer when the CLI is missing; then the sign-in terminal when `claude -p /usage` shows no limits. The task
    /// completes when the steps are done, including the watch of a sign-in terminal. A second call while one runs does nothing.
    public async Task AllowAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            var cli = _host.FindCli() ?? await InstallCoreAsync();
            if (cli is null)
            {
                return;
            }
            await SignInIfNeededAsync(cli);
        }
        finally
        {
            _busy = false;
        }
    }

    /// The Install button: the installer, and nothing else. Completes with the CLI found afterwards, or null.
    public async Task<CliCommand?> InstallAsync()
    {
        if (_busy)
        {
            return null;
        }
        _busy = true;
        try
        {
            return await InstallCoreAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    /// The Sign in button: the terminal for sign-in (and the watch of it), without the installer.
    public async Task SignInAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            if (_host.FindCli() is { } cli)
            {
                await SignInIfNeededAsync(cli);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<CliCommand?> InstallCoreAsync()
    {
        var window = _host.StartInstaller(ClaudeLimitsSetup.InstallerFile, ClaudeLimitsSetup.InstallerArguments);
        if (window is not null)
        {
            await window;
        }
        return _host.FindCli();
    }

    private async Task SignInIfNeededAsync(CliCommand cli)
    {
        var text = await ProbeAsync(cli);
        if (ClaudeLimitsSetup.SignedIn(text))
        {
            return;
        }
        var terminal = _host.StartSignIn(cli);
        if (terminal is null)
        {
            return;
        }
        await WatchSignInAsync(cli, terminal);
    }

    /// Checks the CLI every recheck while the terminal lives, stopping at the first signed-in reply; then one check after it closes.
    private async Task WatchSignInAsync(CliCommand cli, Task terminal)
    {
        while (!terminal.IsCompleted)
        {
            await Task.WhenAny(terminal, Task.Delay(_recheck));
            if (terminal.IsCompleted)
            {
                break;
            }
            if (ClaudeLimitsSetup.SignedIn(await ProbeAsync(cli)))
            {
                return;
            }
        }
        await ProbeAsync(cli);
    }

    /// One `claude -p /usage` on a pool thread; its reply goes to the status.
    private async Task<string?> ProbeAsync(CliCommand cli)
    {
        var text = await Task.Run(() => _host.ProbeUsage(cli));
        Status.Record(text, DateTimeOffset.UtcNow);
        return text;
    }
}

/// The real host: CliLauncher finds and runs the CLI, and the installer and the terminal are visible windows of this user.
public sealed class ClaudeSetupHost : IClaudeSetupHost
{
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(30);
    private static readonly string[] Args = { "-p", "/usage", "--no-session-persistence" };

    private readonly string _home;
    private readonly string? _path;
    private readonly string? _appData;
    private readonly string? _localAppData;

    /// home is the user's profile folder; the other three are the folders the CLI is searched in (null: this PC's).
    public ClaudeSetupHost(string home, string? path = null, string? appData = null, string? localAppData = null)
    {
        _home = home;
        _path = path;
        _appData = appData;
        _localAppData = localAppData;
    }

    public CliCommand? FindCli() => CliLauncher.Resolve("claude", _home, _path, _appData, _localAppData);

    public string? ProbeUsage(CliCommand cli) => CliLauncher.Run(cli, Args, ProbeLimit, CancellationToken.None);

    public Task? StartInstaller(string file, string arguments) => StartVisible(file, arguments, workingDir: null);

    public Task? StartSignIn(CliCommand cli)
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? _home;
        var wt = WindowsTerminal();
        // wt.exe opens a tab in Windows Terminal; else a console window runs the CLI through cmd /k
        return wt is not null
            ? StartVisible(wt, $"-d \"{profile}\" \"{cli.FileName}\"", profile)
            : StartVisible("cmd.exe", $"/k \"\"{cli.FileName}\"\"", profile);
    }

    private static Task? StartVisible(string file, string arguments, string? workingDir)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments) { UseShellExecute = true, WorkingDirectory = workingDir ?? string.Empty };
            var process = Process.Start(info);
            return process is null ? null : process.WaitForExitAsync();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ErrorLog.Write(e);
            return null;
        }
    }

    /// The path of wt.exe when it is here (the App Execution Alias folder, or PATH), else null.
    private string? WindowsTerminal()
    {
        var alias = _localAppData is null ? null : Path.Combine(_localAppData, "Microsoft", "WindowsApps", "wt.exe");
        if (alias is not null && File.Exists(alias))
        {
            return alias;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, "wt.exe");
            if (File.Exists(full))
            {
                return full;
            }
        }
        return null;
    }
}
