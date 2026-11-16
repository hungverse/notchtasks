using System.Diagnostics;
using System.Text;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// The regression test for the Electron bug: the console hook reads the payload an agent pipes to its stdin.
// Runs the Debug build of NotchTasksHook.exe, copied next to this test assembly by the test project.
public sealed class HookExeTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A file still open elsewhere; the temp cleaner takes it later.
            }
        }
    }

    [Fact]
    public async Task HookExe_PipedStdin_WritesAskFile()
    {
        // APPDATA points at a temp folder, so the hook's data folder is <temp>\NotchTasks and the app.pid below is ours.
        var appData = AsksFixtures.NewTempDir();
        _dirs.Add(appData);
        var root = Path.Combine(appData, "NotchTasks");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.pid"), Environment.ProcessId.ToString());
        var asks = Path.Combine(root, "Asks");
        var payload = File.ReadAllText(Path.Combine(AsksFixtures.RepoRoot(), "electron", "test", "fixtures", "agents", "claude-bash.json"));

        var exe = Path.Combine(AppContext.BaseDirectory, "NotchTasksHook.exe");
        var start = new ProcessStartInfo(exe, "--hook claude")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.Environment["APPDATA"] = appData;
        using var hook = Process.Start(start)!;
        var stdoutTask = hook.StandardOutput.ReadToEndAsync();
        await hook.StandardInput.WriteAsync(payload);
        hook.StandardInput.Close();

        var sw = Stopwatch.StartNew();
        string? askPath = null;
        while (askPath is null && sw.ElapsedMilliseconds < 1000)
        {
            askPath = Directory.Exists(asks) ? Directory.GetFiles(asks, "*.json").FirstOrDefault() : null;
            if (askPath is null)
            {
                await Task.Delay(20);
            }
        }
        Assert.NotNull(askPath);
        var id = Path.GetFileNameWithoutExtension(askPath);

        new AskStore(asks).Reply(id, new AskReply(ReplyKinds.Deny, null));
        var stdout = await stdoutTask;
        Assert.True(hook.WaitForExit(5000));

        Assert.Equal(0, hook.ExitCode);
        Assert.Equal(
            "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"The user denied this from the NotchTasks notch.\"}}}\n",
            stdout);
        Assert.Empty(Directory.GetFiles(asks));
    }
}
