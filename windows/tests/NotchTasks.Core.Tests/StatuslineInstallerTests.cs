using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// The Claude statusLine entry in settings.json (opt-in, from the Watching pane). Every test uses a temp home. Shares the "L10n"
// collection with the other tests that change CLAUDE_CONFIG_DIR, so they cannot race.
[Collection("L10n")]
public sealed class StatuslineInstallerTests : IDisposable
{
    private const string Exe = @"C:\Program Files\NotchTasks\NotchTasksHook.exe";
    private const string StaleExe = @"C:\Old Place\NotchTasksHook.exe";  // does not exist
    private const string Other = @"C:\Other Tools\NotchTasksHook.exe";

    private readonly string _home;
    private readonly string _settings;
    private readonly string? _oldClaudeDir;

    public StatuslineInstallerTests()
    {
        // CLAUDE_CONFIG_DIR would send the Claude settings outside the temp home.
        _oldClaudeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
        _home = Path.Combine(Path.GetTempPath(), "nt-statusline-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        _settings = Path.Combine(_home, ".claude", "settings.json");
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

    private void Write(string text) => File.WriteAllText(_settings, text);

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(_settings))!;

    [Fact]
    public void State_NoFile_IsOff()
    {
        Assert.Equal(StatuslineState.Off, StatuslineInstaller.State(_home));
    }

    [Fact]
    public void Install_EmptyHome_WritesTheStatusLine()
    {
        Assert.True(StatuslineInstaller.Install(Exe, _home));

        var line = Read()["statusLine"]!.AsObject();
        Assert.Equal("command", (string?)line["type"]);
        Assert.Equal("\"" + Exe + "\" --statusline", (string?)line["command"]);
        Assert.Equal(0, (int?)line["padding"]);
        Assert.Equal(StatuslineState.Ours, StatuslineInstaller.State(_home));
    }

    [Fact]
    public void Install_KeepsOtherKeys_AndSortsThem()
    {
        Write("{\n  \"theme\": \"dark\",\n  \"model\": \"opus\"\n}");

        StatuslineInstaller.Install(Exe, _home);

        var keys = Read().Select(p => p.Key).ToList();
        Assert.Equal(new[] { "model", "statusLine", "theme" }, keys);
        Assert.Equal("dark", (string?)Read()["theme"]);
        Assert.Equal("opus", (string?)Read()["model"]);
    }

    [Fact]
    public void Install_BacksUpTheOldFile()
    {
        const string before = "{\"theme\":\"dark\"}";
        Write(before);

        StatuslineInstaller.Install(Exe, _home);

        Assert.Equal(before, File.ReadAllText(_settings + ".notchtasks-backup"));
    }

    [Fact]
    public void Install_WhenItsOurs_ChangesNothing()
    {
        StatuslineInstaller.Install(Exe, _home);
        var before = File.ReadAllText(_settings);

        Assert.False(StatuslineInstaller.Install(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void Install_RefusesAnotherStatusLine()
    {
        const string before = "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-line.sh\"}}";
        Write(before);

        Assert.False(StatuslineInstaller.Install(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
        Assert.Equal(StatuslineState.Other, StatuslineInstaller.State(_home));
    }

    [Fact]
    public void Install_InvalidJson_ThrowsAndLeavesTheFile()
    {
        const string before = "{ this is not json";
        Write(before);

        Assert.Throws<InvalidDataException>(() => StatuslineInstaller.Install(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void State_AnotherCommandThatIsNotOurs_IsOther()
    {
        Write("{\"statusLine\":{\"type\":\"command\",\"command\":\"\\\"C:\\\\tools\\\\statusline.exe\\\" --statusline\"}}");

        Assert.Equal(StatuslineState.Other, StatuslineInstaller.State(_home));
    }

    [Fact]
    public void Uninstall_RemovesOurs_KeepsOtherKeys()
    {
        Write("{\"theme\":\"dark\"}");
        StatuslineInstaller.Install(Exe, _home);

        StatuslineInstaller.Uninstall(_home);

        Assert.False(Read().ContainsKey("statusLine"));
        Assert.Equal("dark", (string?)Read()["theme"]);
        Assert.Equal(StatuslineState.Off, StatuslineInstaller.State(_home));
    }

    [Fact]
    public void Uninstall_LeavesAnotherStatusLineAlone()
    {
        const string before = "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-line.sh\"}}";
        Write(before);

        StatuslineInstaller.Uninstall(_home);

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void Uninstall_NoFile_WritesNothing()
    {
        StatuslineInstaller.Uninstall(_home);

        Assert.False(File.Exists(_settings));
    }

    [Fact]
    public void Repoint_OursAtAnExeThatNoLongerExists_PointsAtTheNewExe()
    {
        StatuslineInstaller.Install(StaleExe, _home);

        Assert.True(StatuslineInstaller.Repoint(Exe, _home));

        Assert.Equal("\"" + Exe + "\" --statusline", (string?)Read()["statusLine"]!["command"]);
    }

    [Fact]
    public void Repoint_OursAtAnExeThatStillExists_IsLeftAlone()
    {
        var other = Path.Combine(_home, "NotchTasksHook.exe");
        File.WriteAllText(other, "");
        StatuslineInstaller.Install(other, _home);
        var before = File.ReadAllText(_settings);

        Assert.False(StatuslineInstaller.Repoint(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void Repoint_SamePath_ChangesNothing()
    {
        StatuslineInstaller.Install(Exe, _home);
        var before = File.ReadAllText(_settings);

        Assert.False(StatuslineInstaller.Repoint(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void Repoint_AnotherStatusLine_IsLeftAlone()
    {
        const string before = "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-line.sh\"}}";
        Write(before);

        Assert.False(StatuslineInstaller.Repoint(Exe, _home));

        Assert.Equal(before, File.ReadAllText(_settings));
    }

    [Fact]
    public void UninstallPointingAt_RemovesOursAtThatExe_KeepsOursAtAnother()
    {
        StatuslineInstaller.Install(Exe, _home);

        Assert.False(StatuslineInstaller.UninstallPointingAt(Other, _home));
        Assert.Equal(StatuslineState.Ours, StatuslineInstaller.State(_home));

        Assert.True(StatuslineInstaller.UninstallPointingAt(Exe, _home));
        Assert.Equal(StatuslineState.Off, StatuslineInstaller.State(_home));
    }
}
