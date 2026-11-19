using System.Globalization;

namespace NotchTasks.Core.Agents;

/// A titled group of presets, as the add-agent sheet lists them.
public sealed record WatchPresetGroup(string Title, IReadOnlyList<WatchItem> Items);

/// Watch list model and presets, ported from electron/src/core/watchitems.js (Mac: Sources/WatchItems.swift).
/// Pure: no storage, no process reads. Matching takes the process table and an argv function from the caller.
public static class WatchItems
{
    private const string BuildItemId = "B0B0B0B0-0000-4000-8000-000000000001";

    private static readonly string[] Shells =
    {
        "zsh", "bash", "sh", "fish", "dash", "grep", "rg", "login", "sudo", "tail", "less", "cmd", "powershell", "pwsh",
    };

    private static readonly string[] PathDirsHome = { ".local/bin", ".bun/bin", ".cargo/bin", ".npm-global/bin" };

    /// Process names as Windows reports them, for app rows. Unverified names: check on a real PC.
    public static readonly IReadOnlyDictionary<string, string> WinAppExe = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["com.todesktop.230313mzl4w4u92"] = "Cursor",
        ["com.exafunction.windsurf"] = "Windsurf",
        ["com.google.antigravity"] = "Antigravity",
        ["dev.zed.Zed"] = "zed",
        ["dev.kiro.desktop"] = "Kiro",
        ["com.openai.chat"] = "ChatGPT",
        ["ai.perplexity.mac"] = "Perplexity",
        ["ai.elementlabs.lmstudio"] = "LM Studio",
        ["org.blenderfoundation.blender"] = "blender",
        ["com.docker.docker"] = "Docker Desktop",
        ["com.unity3d.unityhub"] = "Unity Hub",
        ["com.google.android.studio"] = "studio64",
        ["com.blackmagic-design.DaVinciResolveLite"] = "Resolve",
        ["com.microsoft.VisualStudio"] = "devenv",
    };

    /// The preset groups. The first group's title is the Mac's literal "Agent trong terminal" too (WatchItems.swift), so it
    /// is not wrapped in L(), as in Electron. The other two use L() with the Mac's keys.
    public static readonly IReadOnlyList<WatchPresetGroup> PresetGroups = new[]
    {
        new WatchPresetGroup("Agent trong terminal", new[]
        {
            Item("Claude", WatchKind.Claude, process: "claude", bundleId: "com.anthropic.claudefordesktop"),
            Item("Codex", WatchKind.Codex, bundleId: "com.openai.codex"),
            Agent("Gemini CLI", "gemini", args: "gemini-cli"),
            Agent("Antigravity agent", "agy", process: "agy", icon: "com.google.antigravity"),
            Agent("Cursor CLI", "cursor-agent", args: "cursor-agent", icon: "com.todesktop.230313mzl4w4u92"),
            Agent("GitHub Copilot CLI", "copilot", args: "@github/copilot"),
            Agent("Qwen Code", "qwen", args: "qwen-code"),
            Agent("OpenCode", "opencode", process: "opencode"),
            Agent("Amp", "amp", args: "@sourcegraph/amp"),
            Agent("Aider", "aider", args: "bin/aider"),
            Agent("Goose", "goose", process: "goose"),
            Agent("Crush", "crush", process: "crush"),
            Agent("Muse Code", "muse", process: "muse"),
            Agent("Hermes Agent", "hermes", args: "bin/hermes"),
            Agent("Augment (auggie)", "auggie", args: "@augmentcode/auggie"),
            Agent("Kimi CLI", "kimi", args: "bin/kimi"),
            Agent("Kiro CLI", "kiro-cli", process: "kiro-cli"),
            Agent("OpenClaw", "openclaw", args: "openclaw/dist", threshold: 15), // its gateway idles at 1-4% CPU
        }),
        new WatchPresetGroup(L10n.L("AI apps"), new[]
        {
            AiApp("ChatGPT", "com.openai.chat", 15),
            AiApp("Cursor", "com.todesktop.230313mzl4w4u92", 30),
            AiApp("Windsurf", "com.exafunction.windsurf", 30),
            AiApp("Antigravity", "com.google.antigravity", 25),
            AiApp("Kiro", "dev.kiro.desktop", 30),
            AiApp("Zed", "dev.zed.Zed", 30),
            AiApp("Perplexity", "ai.perplexity.mac", 15),
        }),
        // local model runners: on the pill while they generate, with the model they have loaded
        new WatchPresetGroup(L10n.L("Model running locally"), new[]
        {
            AiApp("LM Studio", "ai.elementlabs.lmstudio", 8),
            Item("Ollama", WatchKind.Process, process: "ollama", bundleId: "com.electron.ollama", threshold: 8, cli: "ollama"),
        }),
    };

    /// Every preset, in group order.
    public static readonly IReadOnlyList<WatchItem> Presets = PresetGroups.SelectMany(g => g.Items).ToArray();

    /// Build tools and apps to watch on top of what Build & CI detects by itself.
    public static readonly IReadOnlyList<WatchItem> BuildPresets = new[]
    {
        BuildApp("Visual Studio", "com.microsoft.VisualStudio", 30),
        BuildApp("Android Studio", "com.google.android.studio", 40),
        BuildApp("Docker", "com.docker.docker", 20),
        BuildApp("Unity Hub", "com.unity3d.unityhub", 30),
        BuildApp("Blender", "org.blenderfoundation.blender", 60),
        BuildApp("DaVinci Resolve", "com.blackmagic-design.DaVinciResolveLite", 60),
        Item("MSBuild", WatchKind.Process, process: "msbuild", threshold: 20, symbolName: "hammer.fill", build: true),
        Item("Gradle daemon", WatchKind.Process, process: "java", args: "GradleDaemon", threshold: 20, symbolName: "hammer.fill", build: true),
    };

    /// The Build & CI module's row: not in the watch list, everything in it is detected.
    public static readonly WatchItem BuildItem = new(BuildItemId, "Build & CI", WatchKind.Dev);

    /// The first run's list: the first two presets (Claude and Codex), each with a fresh id.
    public static IReadOnlyList<WatchItem> Defaults() =>
        Presets.Take(2).Select(p => p with { Id = Guid.NewGuid().ToString() }).ToArray();

    /// The app's exe name on Windows for an app item. An app picked from the running list carries its process name as
    /// BundleID ("Code.exe"). Null when none is known.
    public static string? AppExe(WatchItem it)
    {
        if (!string.IsNullOrEmpty(it.Exe))
        {
            return it.Exe;
        }
        if (it.BundleID is not null && WinAppExe.TryGetValue(it.BundleID, out var exe))
        {
            return exe;
        }
        return it.BundleID is not null && it.BundleID.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? it.BundleID : null;
    }

    /// The CPU % above which the item counts as working: its own threshold, else the kind's default.
    public static double CpuThreshold(WatchItem it) => it.Threshold ?? DefaultThreshold(it.Kind, it.ChildTasks);

    /// Build & CI row: a dev row, or an item marked as a build tool.
    public static bool InBuild(WatchItem it) => it.Kind == WatchKind.Dev || it.Build;

    /// The line under an item's name in the list.
    public static string Summary(WatchItem it)
    {
        var cpu = "CPU > " + Math.Round(CpuThreshold(it), 1, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%";
        switch (it.Kind)
        {
            case WatchKind.Process:
            {
                var what = it.Process.Length == 0
                    ? L10n.L("Command contains “{0}”", it.Args ?? "")
                    : L10n.L("Process “{0}”", it.Process) + (it.Args is { Length: > 0 } a ? L10n.L(" · command contains “{0}”", a) : "");
                var parts = new List<string> { what };
                if (it.ChildTasks)
                {
                    parts.Add(L10n.L("one line per subprocess"));
                }
                parts.Add(cpu);
                return string.Join(" · ", parts);
            }
            case WatchKind.App:
                return L10n.L("App · {0}", cpu);
            case WatchKind.Codex:
            case WatchKind.Claude:
                return L10n.L("Tracks each session");
            case WatchKind.Dev:
                return L10n.L("Shows while running");
            default:
                return string.Empty;
        }
    }

    /// The icon name when there is no app to take it from.
    public static string Symbol(WatchItem it) => it.SymbolName ?? (it.Kind == WatchKind.Dev ? "hammer.fill" : "terminal.fill");

    /// Whether the item's process matches a process. Only Process items match. An empty Process matches any name, then
    /// Args must be in the command line. Shells never match by Args (a shell that only mentions the tool is not the tool).
    /// The item's own process (selfPid) never matches.
    public static bool Matches(WatchItem it, ProcInfo p, Func<ProcInfo, IReadOnlyList<string>> argv, int selfPid)
    {
        var needle = (it.Args ?? string.Empty).Replace('\\', '/');
        var want = ProcTable.NormName(it.Process);
        if (it.Kind != WatchKind.Process || (want.Length == 0 && needle.Length == 0) || p.Pid == selfPid)
        {
            return false;
        }
        var name = ProcTable.NormName(p.Name);
        if (want.Length > 0 && name != want)
        {
            return false;
        }
        if (want.Length == 0 && Shells.Contains(name))
        {
            return false;
        }
        return needle.Length == 0 || string.Join(" ", argv(p)).Replace('\\', '/').Contains(needle, StringComparison.Ordinal);
    }

    /// The outermost matching processes: a matching child belongs to its parent's session.
    public static IReadOnlyList<ProcInfo> Roots(WatchItem it, ProcSnapshot procs, Func<ProcInfo, IReadOnlyList<string>> argv)
    {
        var self = Environment.ProcessId;
        var found = procs.All.Where(p => Matches(it, p, argv, self)).ToList();
        var pids = found.Select(p => p.Pid).ToHashSet();
        return found.Where(p => !pids.Contains(p.Ppid)).ToList();
    }

    /// Whether the item's command is installed: its cli on PATH or in the usual per-user tool folders. Items without a
    /// cli are always installed (apps are shown; there is no bundle lookup on Windows).
    public static bool IsInstalled(WatchItem it)
    {
        if (it.Cli is null)
        {
            return true;
        }
        return FindCli(
            it.Cli,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("NVM_SYMLINK"),
            Environment.GetEnvironmentVariable("APPDATA"));
    }

    /// Finds cli in the PATH folders, the usual per-user tool folders under home, the nvm symlink folder and %APPDATA%\npm.
    /// pathExt is PATHEXT (default .EXE;.CMD;.BAT). The file must exist; a folder of the same name does not count.
    public static bool FindCli(string cli, string? pathVar, string? pathExt, string home, string? nvmSymlink, string? appData)
    {
        var exts = new List<string> { string.Empty };
        exts.AddRange((string.IsNullOrEmpty(pathExt) ? ".EXE;.CMD;.BAT" : pathExt).Split(';').Where(e => e.Length > 0));

        var dirs = new List<string>();
        dirs.AddRange((pathVar ?? string.Empty).Split(Path.PathSeparator));
        if (home.Length > 0)
        {
            dirs.AddRange(PathDirsHome.Select(d => Path.Combine(home, d)));
        }
        dirs.Add(nvmSymlink ?? string.Empty);
        if (!string.IsNullOrEmpty(appData))
        {
            dirs.Add(Path.Combine(appData, "npm"));
        }

        foreach (var dir in dirs.Where(d => d.Length > 0))
        {
            foreach (var ext in exts)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, cli + ext)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters a path cannot hold: not a match.
                }
            }
        }
        return false;
    }

    private static double DefaultThreshold(WatchKind kind, bool childTasks) => kind == WatchKind.App ? 5 : childTasks ? 0.5 : 1.5;

    /// CLI agent: often a node or python script, so matched by its command line; working = its process tree uses CPU.
    private static WatchItem Agent(string name, string cli, string process = "", string? args = null, string? icon = null, double threshold = 3) =>
        Item(name, WatchKind.Process, process: process, bundleId: icon, args: args ?? (process.Length > 0 ? null : cli),
            threshold: threshold, symbolName: "sparkles", cli: cli);

    /// GUI apps also use CPU for plain editing, so their bar is higher.
    private static WatchItem AiApp(string name, string bundleId, double threshold) =>
        Item(name, WatchKind.App, bundleId: bundleId, threshold: threshold);

    private static WatchItem BuildApp(string name, string bundleId, double threshold) =>
        Item(name, WatchKind.App, bundleId: bundleId, threshold: threshold, build: true);

    private static WatchItem Item(
        string name,
        WatchKind kind,
        string process = "",
        string? bundleId = null,
        string? args = null,
        double? threshold = null,
        bool childTasks = false,
        bool build = false,
        string? cli = null,
        string? symbolName = null) =>
        new(Guid.NewGuid().ToString(), name, kind, Process: process, Args: args, BundleID: bundleId, Threshold: threshold,
            ChildTasks: childTasks, Build: build, Cli: cli, SymbolName: symbolName);
}
