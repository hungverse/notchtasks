using System.IO;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The CLI launcher: a real .exe is preferred, a clean npm shim runs only through its node entry or with constant arguments, a
/// .ps1 is never run, and user text never reaches cmd.exe. The temp folders stand in for the CLI folders: no real CLI is run.
public sealed class CliLauncherTests
{
    private static string Folder(TempRoot root, string name)
    {
        var dir = Path.Combine(root.Folder, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string name, string text = "")
    {
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, text);
        return file;
    }

    /// A home with no CLIs in it, and an APPDATA with no npm folder.
    private static string Home(TempRoot root) => Path.Combine(root.Folder, "home");

    private static string AppData(TempRoot root) => Path.Combine(root.Folder, "appdata");

    [Fact]
    public void Resolve_PrefersARealExe()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        Write(dir, "claude.cmd", "@echo off\r\n");
        var exe = Write(dir, "claude.exe");

        var command = CliLauncher.Resolve("claude", Home(root), dir, AppData(root));

        Assert.NotNull(command);
        Assert.Equal(CliKind.Direct, command!.Kind);
        Assert.Equal(exe, command.FileName);
    }

    [Fact]
    public void Resolve_AcceptsACleanCmdShim_WhenItHasNoNodeEntry()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        var shim = Write(dir, "claude.cmd", "@echo off\r\n");

        var command = CliLauncher.Resolve("claude", Home(root), dir, AppData(root));

        Assert.NotNull(command);
        Assert.Equal(CliKind.Shim, command!.Kind);
        Assert.Equal(shim, command.FileName);
    }

    [Fact]
    public void Resolve_RefusesAShimInAFolderWithCmdCharacters()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "a&b");
        Write(dir, "claude.cmd", "@echo off\r\n");

        Assert.Null(CliLauncher.Resolve("claude", Home(root), dir, AppData(root)));
    }

    [Fact]
    public void Resolve_NeverRunsA_Ps1()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        Write(dir, "claude.ps1", "Write-Output 1\r\n");

        Assert.Null(CliLauncher.Resolve("claude", Home(root), dir, AppData(root)));
    }

    [Fact]
    public void ResolveDirect_ShimWithANodeEntry_RunsNodeAndTheScript()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "npm");
        var shim = Write(dir, "codex.cmd",
            "@ECHO off\r\n\"%_prog%\" \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");
        var script = Write(Folder(root, "npm/node_modules/@openai/codex/bin"), "codex.js");
        var node = Write(dir, "node.exe");

        var command = CliLauncher.ResolveDirect("codex", Home(root), dir, AppData(root));

        Assert.NotNull(command);
        Assert.Equal(CliKind.Direct, command!.Kind);
        Assert.Equal(node, command.FileName);
        Assert.Equal(Path.GetFullPath(script), Assert.Single(command.Prefix));
        Assert.True(File.Exists(shim));
    }

    [Fact]
    public void ResolveDirect_ShimWithoutANodeEntry_IsNull()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "npm");
        Write(dir, "codex.cmd", "@ECHO off\r\n\"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");

        Assert.Null(CliLauncher.ResolveDirect("codex", Home(root), dir, AppData(root)));
    }

    [Fact]
    public void ResolveDirect_AScriptOutsideTheShimsFolder_IsNull()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "npm");
        Write(dir, "codex.cmd", "\"%dp0%\\..\\evil.js\" %*\r\n");
        Write(root.Folder, "evil.js");
        Write(dir, "node.exe");

        Assert.Null(CliLauncher.ResolveDirect("codex", Home(root), dir, AppData(root)));
    }

    [Fact]
    public void Run_NeverPassesUserTextThroughCmd()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        var shim = new CliCommand(CliKind.Shim, Write(dir, "codex.cmd", "@echo off\r\n"), Array.Empty<string>());

        // a message with cmd's characters is refused before anything starts
        var output = CliLauncher.Run(shim, new[] { "queue", "--message", "hello & calc" }, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(output);

        // and so is a message with a space: cmd.exe only ever sees the app's constant words
        var spaced = CliLauncher.Run(shim, new[] { "queue", "--message", "hello world" }, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Null(spaced);
    }

    [Fact]
    public void Run_AMissingProgram_IsNull()
    {
        using var root = new TempRoot();
        var missing = new CliCommand(CliKind.Direct, Path.Combine(root.Folder, "nope.exe"), Array.Empty<string>());

        Assert.Null(CliLauncher.Run(missing, Array.Empty<string>(), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public void QueueArgs_AreTheMacsCommand()
    {
        Assert.Equal(new[] { "queue", "--thread", "t1", "--message", "Yes" }, CodexCli.QueueArgs("t1", "Yes"));
    }

    [Theory]
    [InlineData("bad|name.cmd")]  // a character Windows refuses in a path: ReadAllText throws ArgumentException
    [InlineData("half:colon.cmd")]  // a colon in the middle: NotSupportedException
    public void NodeEntry_APathWindowsRefuses_IsNullNotAnException(string name)
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");

        Assert.Null(CliLauncher.NodeEntry(Path.Combine(dir, name), dir));
    }

    [Fact]
    public void NodeEntry_AScriptNameWindowsRefuses_IsNullNotAnException()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        // the shim is readable, but the script it names has a colon in the middle, which GetFullPath refuses
        var shim = Write(dir, "codex.cmd", @"node ""%dp0%\a:b.js"" %*");

        Assert.Null(CliLauncher.NodeEntry(shim, dir));
    }

    [Fact]
    public void Run_NonZeroExit_IsNullWhenTheExitMustBeZero()
    {
        // cmd.exe stands in for a program that prints and then fails; no real CLI runs.
        var failing = new CliCommand(CliKind.Direct, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            new[] { "/d", "/c", "echo partial & exit 3" });
        var limit = TimeSpan.FromSeconds(20);

        Assert.Null(CliLauncher.Run(failing, Array.Empty<string>(), limit, CancellationToken.None, requireZeroExit: true));
        Assert.Equal("partial", CliLauncher.Run(failing, Array.Empty<string>(), limit, CancellationToken.None)?.Trim());
    }

    [Fact]
    public void ResolveDirect_SqliteCmdEarlierOnPath_ExeLaterOnPathWins()
    {
        using var root = new TempRoot();
        var cmdDir = Folder(root, "cmd");
        var exeDir = Folder(root, "exe");
        Write(cmdDir, "sqlite3.cmd", "@echo off\r\n");
        var exe = Write(exeDir, "sqlite3.exe");

        var command = CliLauncher.ResolveDirect("sqlite3", Home(root), cmdDir + Path.PathSeparator + exeDir, AppData(root));

        Assert.NotNull(command);
        Assert.Equal(CliKind.Direct, command!.Kind);
        Assert.Equal(exe, command.FileName);
        Assert.Empty(command.Prefix);
    }

    [Fact]
    public void ResolveDirect_OnlyASqliteCmd_IsRefused()
    {
        using var root = new TempRoot();
        var dir = Folder(root, "bin");
        Write(dir, "sqlite3.cmd", "@echo off\r\n");
        Write(dir, "sqlite3.bat", "@echo off\r\n");

        Assert.Null(CliLauncher.ResolveDirect("sqlite3", Home(root), dir, AppData(root)));
    }

    [Fact]
    public void Resolve_ARelativeExe_IsNotAWinner_AndAFullyQualifiedShimIsUsed()
    {
        using var root = new TempRoot();
        var exeDir = Folder(root, "relexe");
        Write(exeDir, "claude.exe");
        var shimDir = Folder(root, "shim");
        var shim = Write(shimDir, "claude.cmd", "@echo off\r\n");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), exeDir);
        Assert.False(Path.IsPathFullyQualified(relative));

        var command = CliLauncher.Resolve("claude", Home(root), relative + Path.PathSeparator + shimDir, AppData(root));

        Assert.NotNull(command);
        Assert.Equal(CliKind.Shim, command!.Kind);
        Assert.Equal(shim, command.FileName);
        Assert.Null(CliLauncher.Resolve("claude", Home(root), relative, AppData(root)));
    }
}
