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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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
        // The app counts as running only as NotchTasks.exe: a copy of ping.exe under that name stands in for it.
        var fakeApp = Path.Combine(appData, "NotchTasks.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), fakeApp);
        using var app = Process.Start(new ProcessStartInfo(fakeApp, "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        File.WriteAllText(Path.Combine(root, "app.pid"), app.Id.ToString());
        var asks = Path.Combine(root, "Asks");
        var payload = File.ReadAllText(Path.Combine(AsksFixtures.RepoRoot(), "windows", "tests", "fixtures", "agents", "claude-bash.json"));

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
        // A correctness check, not a timing one: the window is wide so a busy machine does not fail it.
        while (askPath is null && sw.ElapsedMilliseconds < 5000)
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
        // The fake app holds its copied exe open until it has exited: wait, or Dispose cannot delete the folder.
        app.Kill();
        app.WaitForExit(5000);
    }
}
