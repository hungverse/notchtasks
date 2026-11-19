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
    private static readonly string[] ProcessNames = { "codex" };

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
        return command is null ? null : CliLauncher.Run(command, QueueArgs(thread, message), QueueLimit, CancellationToken.None);
    });

    public bool BringToFront()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    var window = process.MainWindowHandle;
                    if (window == 0)
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
                finally
                {
                    process.Dispose();
                }
            }
        }
        return false;
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
