using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/src/core/watchitems.js (presets, cpuThreshold, matches, roots) and the Mac WatchItems.swift.
public sealed class WatchItemTests
{
    private const int SelfPid = 999;

    private static ProcInfo P(int pid, int ppid, string name) => new(pid, ppid, name, 1, 0);

    private static IReadOnlyList<string> Argv(string cmd) => ProcTable.SplitArgs(cmd);

    private static IReadOnlyList<string> ArgvOf(ProcInfo p) => p.Name switch
    {
        "node.exe" => Argv(@"node C:\x\node_modules\gemini-cli\dist\index.js"),
        _ => Argv(p.Name),
    };

    private static WatchItem Goose(string process = "goose", string? args = null, bool childTasks = false, double? threshold = null) =>
        new(Guid.NewGuid().ToString(), "Goose", WatchKind.Process, Process: process, Args: args, ChildTasks: childTasks, Threshold: threshold);

    private static WatchItem Gemini() =>
        new(Guid.NewGuid().ToString(), "Gemini CLI", WatchKind.Process, Process: "", Args: "gemini-cli", Cli: "gemini");

    [Fact]
    public void Presets_MatchElectron()
    {
        var names = WatchItems.Presets.Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "Claude", "Codex", "Gemini CLI", "Antigravity agent", "Cursor CLI", "GitHub Copilot CLI", "Qwen Code",
                "OpenCode", "Amp", "Aider", "Goose", "Crush", "Muse Code", "Hermes Agent", "Augment (auggie)", "Kimi CLI",
                "Kiro CLI", "OpenClaw", "ChatGPT", "Cursor", "Windsurf", "Antigravity", "Kiro", "Zed", "Perplexity",
                "LM Studio", "Ollama",
            },
            names);

        var claude = WatchItems.Presets[0];
        Assert.Equal(WatchKind.Claude, claude.Kind);
        Assert.Equal(1.5, WatchItems.CpuThreshold(claude));

        var openClaw = WatchItems.Presets.Single(p => p.Name == "OpenClaw");
        Assert.Equal(15, WatchItems.CpuThreshold(openClaw));

        var chatGpt = WatchItems.Presets.Single(p => p.Name == "ChatGPT");
        Assert.Equal(WatchKind.App, chatGpt.Kind);
        Assert.Equal(15, WatchItems.CpuThreshold(chatGpt));

        Assert.Equal(new[] { "LM Studio", "Ollama" }, WatchItems.PresetGroups.Last().Items.Select(i => i.Name));
    }

    [Fact]
    public void BuildPresets_AndBuildItem()
    {
        Assert.Equal(
            new[] { "Visual Studio", "Android Studio", "Docker", "Unity Hub", "Blender", "DaVinci Resolve", "MSBuild", "Gradle daemon" },
            WatchItems.BuildPresets.Select(p => p.Name));
        Assert.All(WatchItems.BuildPresets, p => Assert.True(WatchItems.InBuild(p)));
        Assert.Equal("B0B0B0B0-0000-4000-8000-000000000001", WatchItems.BuildItem.Id);
        Assert.Equal(WatchKind.Dev, WatchItems.BuildItem.Kind);
        Assert.True(WatchItems.InBuild(WatchItems.BuildItem));
    }

    [Fact]
    public void CpuThreshold_Defaults()
    {
        Assert.Equal(5, WatchItems.CpuThreshold(new WatchItem("a", "App", WatchKind.App)));
        Assert.Equal(0.5, WatchItems.CpuThreshold(Goose(childTasks: true)));
        Assert.Equal(1.5, WatchItems.CpuThreshold(Goose()));
        Assert.Equal(7, WatchItems.CpuThreshold(Goose(threshold: 7)));
    }

    [Fact]
    public void Matches_ByArgs_NodeRunningGeminiCli()
    {
        var node = P(10, 1, "node.exe");
        Assert.True(WatchItems.Matches(Gemini(), node, ArgvOf, SelfPid));
    }

    [Fact]
    public void Matches_ByName_CaseInsensitive_NoExe()
    {
        Assert.True(WatchItems.Matches(Goose(), P(20, 1, "GOOSE.EXE"), ArgvOf, SelfPid));
        Assert.True(WatchItems.Matches(Goose(), P(20, 1, "goose"), ArgvOf, SelfPid));
        Assert.False(WatchItems.Matches(Goose(), P(20, 1, "gooseberry.exe"), ArgvOf, SelfPid));
    }

    [Fact]
    public void Matches_ShellWithArgsMention_IsNotTheTool()
    {
        var bash = P(30, 1, "bash.exe");
        Assert.False(WatchItems.Matches(Gemini(), bash, _ => Argv("bash -c gemini-cli"), SelfPid));
    }

    [Fact]
    public void Matches_NeverSelf()
    {
        Assert.False(WatchItems.Matches(Goose(), P(SelfPid, 1, "goose.exe"), ArgvOf, SelfPid));
    }

    [Fact]
    public void Matches_OnlyProcessKind()
    {
        var claude = new WatchItem("c", "Claude", WatchKind.Claude, Process: "claude");
        Assert.False(WatchItems.Matches(claude, P(40, 1, "claude.exe"), ArgvOf, SelfPid));
    }

    [Fact]
    public void Roots_DropChildOfMatchingParent()
    {
        var snapshot = new ProcSnapshot(new[]
        {
            P(10, 1, "goose.exe"),
            P(11, 10, "goose.exe"),   // child of a match: belongs to the parent's session
            P(12, 1, "goose.exe"),
            P(13, 12, "sh.exe"),      // not a match
        });
        var roots = WatchItems.Roots(Goose(), snapshot, ArgvOf);
        Assert.Equal(new[] { 10, 12 }, roots.Select(p => p.Pid));
    }

    [Fact]
    public void Symbol_UsesSymbolNameThenKindDefault()
    {
        Assert.Equal("sparkles", WatchItems.Symbol(Gemini() with { SymbolName = "sparkles" }));
        Assert.Equal("hammer.fill", WatchItems.Symbol(WatchItems.BuildItem));
        Assert.Equal("terminal.fill", WatchItems.Symbol(Goose()));
    }

    [Fact]
    public void AppExe_FromBundleIdOrExplicitExe()
    {
        var cursor = new WatchItem("a", "Cursor", WatchKind.App, BundleID: "com.todesktop.230313mzl4w4u92");
        Assert.Equal("Cursor", WatchItems.AppExe(cursor));

        var picked = new WatchItem("b", "Code", WatchKind.App, BundleID: "Code.exe");
        Assert.Equal("Code.exe", WatchItems.AppExe(picked));

        var unknown = new WatchItem("c", "X", WatchKind.App, BundleID: "com.example.x");
        Assert.Null(WatchItems.AppExe(unknown));
    }

    [Fact]
    public void Defaults_AreClaudeAndCodex_WithFreshIds()
    {
        var defaults = WatchItems.Defaults();
        Assert.Equal(new[] { "Claude", "Codex" }, defaults.Select(d => d.Name));
        Assert.Equal(new[] { WatchKind.Claude, WatchKind.Codex }, defaults.Select(d => d.Kind));
        Assert.DoesNotContain(defaults, d => WatchItems.Presets.Any(p => p.Id == d.Id));
    }

    [Fact]
    public void FindCli_FindsCommandInPathDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nt-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "gemini.CMD"), "");
            Assert.True(WatchItems.FindCli("gemini", dir, ".EXE;.CMD;.BAT", home: dir, nvmSymlink: null, appData: null));
            Assert.False(WatchItems.FindCli("qwen", dir, ".EXE;.CMD;.BAT", home: dir, nvmSymlink: null, appData: null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
