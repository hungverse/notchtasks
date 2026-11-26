using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchTasks.Services;

/// The Codex side of a pill answer: the question's reply is queued into its thread, and "Answer in Codex" brings the window up.
public interface ICodexCli
{
    /// Queues a message into a Codex thread (`codex queue --thread <thread> --message <text>`). Returns the output, or null when
    /// the message was not sent (no codex that runs without a shell, or it failed).
    Task<string?> QueueAsync(string thread, string message);

    /// Brings the Codex window to the front. True when a window was found and asked to come forward.
    bool BringToFront();
}

/// The app's ICodexCli. The message is user text, so only a Direct command runs it: a .cmd shim is never used for it.
public sealed partial class CodexCli : ICodexCli
{
    private static readonly TimeSpan QueueLimit = TimeSpan.FromSeconds(60);
    private readonly string _home;

    /// home is the user's profile folder, searched for the codex CLI.
    public CodexCli(string home)
    {
        _home = home;
    }

    /// The arguments of a queue call (AgentAsks.codexReply): queue --thread <thread> --message <text>.
    public static IReadOnlyList<string> QueueArgs(string thread, string message) =>
        new[] { "queue", "--thread", thread, "--message", message };

    public Task<string?> QueueAsync(string thread, string message) => Task.Run(() =>
    {
        if (thread.Length == 0 || message.Length == 0)
        {
            return null;
        }
        var command = CliLauncher.ResolveDirect("codex", _home);
        // A queue that exits non-zero did not send the message: null, so the caller brings Codex forward to answer there.
        return command is null
            ? null
            : CliLauncher.Run(command, QueueArgs(thread, message), QueueLimit, CancellationToken.None, requireZeroExit: true);
    });

    public bool BringToFront()
    {
        var processes = Process.GetProcessesByName("codex").Concat(Process.GetProcessesByName("ChatGPT")).ToList();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == 0 || !IsCodexWindow(process))
                    {
                        continue;
                    }
                    if (IsIconic(window))
                    {
                        ShowWindow(window, SwRestore);
                    }
                    return SetForegroundWindow(window);
                }
                catch (Exception)
                {
                    // the process has gone
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// True for a file in the Codex desktop app's package (...\WindowsApps\OpenAI.Codex_<version>_<arch>__<publisher>\...). The
    /// plain ChatGPT desktop app is another package, so it is not Codex. Compared without regard to case or separator style.
    public static bool IsCodexDesktopPath(string path) =>
        path.Replace('/', '\\').Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

    /// The codex CLI's window, or the Codex desktop app's (its main process is ChatGPT.exe in the Codex package).
    private static bool IsCodexWindow(Process process) =>
        process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase) || IsCodexDesktopPath(ImagePath(process));

    /// The process's executable path, or empty when it cannot be read (another user's process, or it has gone).
    private static string ImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private const int SwRestore = 9;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);
}
