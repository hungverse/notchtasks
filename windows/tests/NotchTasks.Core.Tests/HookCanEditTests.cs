using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Temp homes only. Codex's hooks.json is used: its path does not depend on CLAUDE_CONFIG_DIR.
public sealed class HookCanEditTests : IDisposable
{
    private readonly string _home;

    public HookCanEditTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "nt-canedit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CodexFile()
    {
        var file = Path.Combine(_home, ".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }

    [Fact]
    public void CanEdit_MissingFile_IsTrue()
    {
        Assert.True(HookInstaller.CanEdit(AgentKind.Codex, _home));
    }

    [Fact]
    public void CanEdit_ObjectFile_IsTrue()
    {
        File.WriteAllText(CodexFile(), "{\"hooks\":{}}");

        Assert.True(HookInstaller.CanEdit(AgentKind.Codex, _home));
    }

    [Fact]
    public void CanEdit_CorruptFile_IsFalse_AndInstallLeavesItUntouched()
    {
        var file = CodexFile();
        File.WriteAllText(file, "{ not json");

        Assert.False(HookInstaller.CanEdit(AgentKind.Codex, _home));
        Assert.Throws<InvalidDataException>(() =>
            HookInstaller.Install(AgentKind.Codex, @"C:\NotchTasks\NotchTasksHook.exe", _home));
        Assert.Equal("{ not json", File.ReadAllText(file));
    }
}
