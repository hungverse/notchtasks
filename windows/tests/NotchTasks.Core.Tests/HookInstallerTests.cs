using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/asks.test.js (the install, settings and command tests). Every test uses a temp home.
// Shares the "L10n" collection so the CLAUDE_CONFIG_DIR change below cannot race AgentKindTests.
[Collection("L10n")]
public sealed class HookInstallerTests : IDisposable
{
    private const string Exe = @"C:\Program Files\NotchTasks\NotchTasksHook.exe";
    private const string ElectronExe = @"C:\Program Files\NotchTasks\NotchTasks.exe";
    private const string Other = @"C:\Other Tools\NotchTasksHook.exe";

    private readonly string _home;
    private readonly string? _oldClaudeDir;

    public HookInstallerTests()
    {
        // CLAUDE_CONFIG_DIR would send the Claude settings outside the temp home.
        _oldClaudeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        _home = Path.Combine(Path.GetTempPath(), "nt-hooks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _oldClaudeDir);
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A file still open elsewhere; the temp cleaner takes it later.
        }
    }

    private string SettingsOf(AgentKind kind) => AgentKinds.Spec(kind).SettingsFile(_home);

    private static JsonObject ReadJson(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;

    private static void Write(string file, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    private const string OtherHookJson =
        """{"model":"opus","hooks":{"PermissionRequest":[{"matcher":"Bash","hooks":[{"type":"command","command":"other"}]}]}}""";

    [Fact]
    public void Install_Claude_AddsGroupAndKeepsOtherHooks()
    {
        var file = SettingsOf(AgentKind.Claude);
        Write(file, OtherHookJson);

        HookInstaller.Install(AgentKind.Claude, Exe, _home);
        HookInstaller.Install(AgentKind.Claude, Exe, _home);  // twice: still one of ours

        var root = ReadJson(file);
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var groups = root["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("Bash", groups[0]!["matcher"]!.GetValue<string>());
        var ours = groups[1]!;
        Assert.Equal("*", ours["matcher"]!.GetValue<string>());
        var handler = ours["hooks"]![0]!;
        Assert.Equal($"\"{Exe}\" --hook claude", handler["command"]!.GetValue<string>());
        Assert.Equal(600, handler["timeout"]!.GetValue<int>());  // wait 590 + 10 seconds
        Assert.True(HookInstaller.IsInstalled(AgentKind.Claude, _home));
        Assert.True(HookInstaller.PointsAt(AgentKind.Claude, Exe, _home));
        Assert.True(File.Exists(file + ".notchtasks-backup"));
    }

    [Fact]
    public void Install_ReplacesElectronCommand()
    {
        var file = SettingsOf(AgentKind.Claude);
        // The Electron hook: "<exe>" --hook claude, under the same event
        Write(file, """{"hooks":{"PermissionRequest":[{"matcher":"*","hooks":[{"type":"command","command":"\"C:\\Program Files\\NotchTasks\\NotchTasks.exe\" --hook claude"}]}]}}""");
        Assert.True(HookInstaller.IsInstalled(AgentKind.Claude, _home));  // the old command counts as ours
        Assert.False(HookInstaller.PointsAt(AgentKind.Claude, Exe, _home));

        HookInstaller.Install(AgentKind.Claude, Exe, _home);

        var groups = ReadJson(file)["hooks"]!["PermissionRequest"]!.AsArray();
        Assert.Single(groups);
        Assert.Equal($"\"{Exe}\" --hook claude", groups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(HookInstaller.PointsAt(AgentKind.Claude, Exe, _home));
        Assert.DoesNotContain("NotchTasks.exe", File.ReadAllText(file));
    }

    [Fact]
    public void Uninstall_RemovesOnlyOurs_KeepsTheRest()
    {
        var file = SettingsOf(AgentKind.Claude);
        Write(file, OtherHookJson);
        HookInstaller.Install(AgentKind.Claude, Exe, _home);

        HookInstaller.Uninstall(AgentKind.Claude, _home);

        Assert.False(HookInstaller.IsInstalled(AgentKind.Claude, _home));
        Assert.False(HookInstaller.PointsAt(AgentKind.Claude, Exe, _home));
        var root = ReadJson(file);
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal(
            "[{\"hooks\":[{\"command\":\"other\",\"type\":\"command\"}],\"matcher\":\"Bash\"}]",
            root["hooks"]!["PermissionRequest"]!.ToJsonString());
    }

    [Fact]
    public void Install_IntoMissingFile_CreatesIt_UninstallLeavesEmptyObject()
    {
        var file = SettingsOf(AgentKind.Claude);

        HookInstaller.Install(AgentKind.Claude, Exe, _home);

        Assert.True(HookInstaller.IsInstalled(AgentKind.Claude, _home));
        Assert.False(File.Exists(file + ".notchtasks-backup"));  // there was no old file to save
        HookInstaller.Uninstall(AgentKind.Claude, _home);
        Assert.Equal("{}", File.ReadAllText(file));
    }

    [Fact]
    public void Uninstall_MissingFile_WritesNothing()
    {
        var file = SettingsOf(AgentKind.Claude);
        Assert.False(File.Exists(file));

        HookInstaller.Uninstall(AgentKind.Claude, _home);

        Assert.False(File.Exists(file), "no settings file is created by an uninstall");
        Assert.False(File.Exists(file + ".notchtasks-backup"));
    }

    [Fact]
    public void Claude_SettingsPath_FollowsTheHomeGiven()
    {
        Assert.Equal(Path.Combine(_home, ".claude", "settings.json"), SettingsOf(AgentKind.Claude));
    }

    [Fact]
    public void Gemini_NamedHookWithTimeoutInMilliseconds()
    {
        var file = SettingsOf(AgentKind.Gemini);

        HookInstaller.Install(AgentKind.Gemini, Exe, _home);

        var group = ReadJson(file)["hooks"]!["BeforeTool"]!.AsArray()[0]!;
        Assert.Equal("run_shell_command|write_file|replace|web_fetch|ask_user", group["matcher"]!.GetValue<string>());
        var handler = group["hooks"]![0]!;
        Assert.Equal("notchtasks", handler["name"]!.GetValue<string>());
        Assert.Equal(600000, handler["timeout"]!.GetValue<int>());
        Assert.Equal($"\"{Exe}\" --hook gemini", handler["command"]!.GetValue<string>());
        Assert.True(HookInstaller.IsInstalled(AgentKind.Gemini, _home));

        HookInstaller.Uninstall(AgentKind.Gemini, _home);
        Assert.False(HookInstaller.IsInstalled(AgentKind.Gemini, _home));
    }

    [Fact]
    public void Codex_HookInHooksJson_OthersKept()
    {
        var file = SettingsOf(AgentKind.Codex);
        Assert.EndsWith(Path.Combine(".codex", "hooks.json"), file);
        Write(file, """{"hooks":{"PermissionRequest":[{"matcher":"x","hooks":[{"type":"command","command":"mine"}]}]}}""");

        HookInstaller.Install(AgentKind.Codex, Exe, _home);
        Assert.True(HookInstaller.IsInstalled(AgentKind.Codex, _home));
        Assert.True(HookInstaller.PointsAt(AgentKind.Codex, Exe, _home));
        Assert.Equal(2, ReadJson(file)["hooks"]!["PermissionRequest"]!.AsArray().Count);

        HookInstaller.Uninstall(AgentKind.Codex, _home);
        Assert.Single(ReadJson(file)["hooks"]!["PermissionRequest"]!.AsArray());
    }

    [Fact]
    public void Antigravity_KeyedByName_RemovedByName()
    {
        var file = SettingsOf(AgentKind.Antigravity);
        Assert.EndsWith(Path.Combine(".gemini", "config", "hooks.json"), file);

        HookInstaller.Install(AgentKind.Antigravity, Exe, _home);

        var root = ReadJson(file);
        Assert.Equal($"\"{Exe}\" --hook antigravity",
            root["notchtasks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(HookInstaller.IsInstalled(AgentKind.Antigravity, _home));
        Assert.True(HookInstaller.PointsAt(AgentKind.Antigravity, Exe, _home));

        HookInstaller.Uninstall(AgentKind.Antigravity, _home);
        Assert.False(HookInstaller.IsInstalled(AgentKind.Antigravity, _home));
        Assert.Equal("{}", File.ReadAllText(file));
    }

    [Fact]
    public void Cursor_FlatList_Version1_OthersKept()
    {
        var file = SettingsOf(AgentKind.Cursor);
        Assert.EndsWith(Path.Combine(".cursor", "hooks.json"), file);

        // A missing file gets version 1
        HookInstaller.Install(AgentKind.Cursor, Exe, _home);
        Assert.Equal(1, ReadJson(file)["version"]!.GetValue<int>());
        HookInstaller.Uninstall(AgentKind.Cursor, _home);

        Write(file, """{"version":1,"hooks":{"beforeShellExecution":[{"command":"mine.sh"}]}}""");
        HookInstaller.Install(AgentKind.Cursor, Exe, _home);
        HookInstaller.Install(AgentKind.Cursor, Exe, _home);

        var root = ReadJson(file);
        var list = root["hooks"]!["beforeShellExecution"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal($"\"{Exe}\" --hook cursor", list[1]!["command"]!.GetValue<string>());
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.True(HookInstaller.IsInstalled(AgentKind.Cursor, _home));
        Assert.True(HookInstaller.PointsAt(AgentKind.Cursor, Exe, _home));

        HookInstaller.Uninstall(AgentKind.Cursor, _home);
        Assert.Equal("[{\"command\":\"mine.sh\"}]", ReadJson(file)["hooks"]!["beforeShellExecution"]!.ToJsonString());
        Assert.False(HookInstaller.IsInstalled(AgentKind.Cursor, _home));
    }

    [Fact]
    public void OpenCode_WritesPluginWithExePath_BacksUpOldFile_RemovedOnUninstall()
    {
        var file = SettingsOf(AgentKind.OpenCode);
        Assert.EndsWith(Path.Combine(".config", "opencode", "plugins", "notchtasks.js"), file);
        Write(file, "// someone else\n");

        HookInstaller.Install(AgentKind.OpenCode, Exe, _home);

        var text = File.ReadAllText(file);
        // The exe path is written as a JSON string literal, backslashes doubled
        Assert.Contains(@"spawn(""C:\\Program Files\\NotchTasks\\NotchTasksHook.exe"", [""--hook"", ""opencode""]", text);
        Assert.True(HookInstaller.IsInstalled(AgentKind.OpenCode, _home));
        Assert.True(HookInstaller.PointsAt(AgentKind.OpenCode, Exe, _home));
        Assert.False(HookInstaller.PointsAt(AgentKind.OpenCode, Other, _home));
        Assert.Equal("// someone else\n", File.ReadAllText(file + ".notchtasks-backup"));

        HookInstaller.Uninstall(AgentKind.OpenCode, _home);
        Assert.False(File.Exists(file));
        Assert.False(HookInstaller.IsInstalled(AgentKind.OpenCode, _home));
    }

    [Fact]
    public void OpenCodePlugin_IsTheSameTextAsTheElectronPlugin()
    {
        var text = HookInstaller.OpenCodePlugin(Exe);
        Assert.StartsWith("// Written by NotchTasks: answers OpenCode's permission prompts from the notch.", text);
        Assert.Contains("id: \"notchtasks\"", text);
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void UnreadableSettings_Throw_AndAreNeverOverwritten()
    {
        var file = SettingsOf(AgentKind.Claude);
        Write(file, "{ not json");
        Assert.Throws<InvalidDataException>(() => HookInstaller.Install(AgentKind.Claude, Exe, _home));
        Assert.Throws<InvalidDataException>(() => HookInstaller.Uninstall(AgentKind.Claude, _home));
        Assert.Equal("{ not json", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".notchtasks-backup"));
        Assert.False(HookInstaller.IsInstalled(AgentKind.Claude, _home));

        Write(file, "[1, 2]");
        var ex = Assert.Throws<InvalidDataException>(() => HookInstaller.Install(AgentKind.Claude, Exe, _home));
        Assert.Contains("not a JSON object", ex.Message);
        Assert.Equal("[1, 2]", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".notchtasks-backup"));

        Write(file, "null");
        Assert.Throws<InvalidDataException>(() => HookInstaller.Install(AgentKind.Claude, Exe, _home));
        Assert.Equal("null", File.ReadAllText(file));
    }

    [Fact]
    public void Command_QuotedExePathWithHook()
    {
        Assert.Equal($"\"{Exe}\" --hook codex", HookInstaller.Command(AgentKind.Codex, Exe));
    }

    [Fact]
    public void PointsAt_OnlyTheExactHookExe()
    {
        HookInstaller.Install(AgentKind.Claude, Exe, _home);

        Assert.True(HookInstaller.PointsAt(AgentKind.Claude, Exe, _home));
        Assert.False(HookInstaller.PointsAt(AgentKind.Claude, Other, _home));
        Assert.False(HookInstaller.PointsAt(AgentKind.Claude, ElectronExe, _home));
        Assert.True(HookInstaller.IsInstalled(AgentKind.Claude, _home));
    }

    [Fact]
    public void EveryAgent_InstallThenUninstall_RoundTrips_NothingInstalledAtStart()
    {
        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            Assert.False(HookInstaller.IsInstalled(kind, _home));
            Assert.False(HookInstaller.PointsAt(kind, Exe, _home));

            HookInstaller.Install(kind, Exe, _home);
            Assert.True(HookInstaller.IsInstalled(kind, _home), kind.ToString());
            Assert.True(HookInstaller.PointsAt(kind, Exe, _home), kind.ToString());

            HookInstaller.Uninstall(kind, _home);
            Assert.False(HookInstaller.IsInstalled(kind, _home), kind.ToString());
        }
    }

    [Fact]
    public void SortedJson_SortsKeys_TwoSpaceIndent_NoTrailingNewline_KeepsNonAscii()
    {
        var node = JsonNode.Parse("""{"b":1,"a":{"d":[{"z":true,"y":null}],"c":"Nguyễn <tag> & 'q'"}}""")!;

        var text = HookInstaller.SortedJson(node);

        var expected = "{\n  \"a\": {\n    \"c\": \"Nguyễn <tag> & 'q'\",\n    \"d\": [\n      {\n        \"y\": null,\n        \"z\": true\n      }\n    ]\n  },\n  \"b\": 1\n}";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void SortedJson_WindowsPath_EscapesBackslashesAndQuotesOnly()
    {
        var node = JsonNode.Parse("""{"command":"\"C:\\Users\\Test\\x.exe\" --hook claude"}""")!;

        var text = HookInstaller.SortedJson(node);

        Assert.Equal("{\n  \"command\": \"\\\"C:\\\\Users\\\\Test\\\\x.exe\\\" --hook claude\"\n}", text);
    }
}
