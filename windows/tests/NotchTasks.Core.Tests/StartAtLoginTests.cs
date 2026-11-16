using System.Text.Json.Nodes;
using NotchTasks.Core.Permissions;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class StartAtLoginTests : IDisposable
{
    private const string Exe = @"C:\Program Files\NotchTasks\NotchTasks.exe";
    private readonly string _dir;

    public StartAtLoginTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void StartAtLogin_SetOnOff_FakeRegistry()
    {
        var reg = new FakeRegistry();
        Assert.False(StartAtLogin.IsOn(reg, Exe));

        StartAtLogin.Set(reg, Exe, true);
        Assert.True(StartAtLogin.IsOn(reg, Exe));
        Assert.Equal("\"" + Exe + "\"", reg.GetString(StartAtLogin.KeyPath, "NotchTasks"));

        StartAtLogin.Set(reg, Exe, false);
        Assert.False(StartAtLogin.IsOn(reg, Exe));
        Assert.Null(reg.GetString(StartAtLogin.KeyPath, "NotchTasks"));
    }

    [Fact]
    public void StartAtLogin_QuotesPath()
    {
        var reg = new FakeRegistry();
        const string spaced = @"C:\Program Files (x86)\Notch Tasks\NotchTasks.exe";

        StartAtLogin.Set(reg, spaced, true);

        Assert.Equal("\"" + spaced + "\"", reg.GetString(StartAtLogin.KeyPath, "NotchTasks"));
        Assert.True(StartAtLogin.IsOn(reg, spaced));
    }

    [Fact]
    public void StartAtLogin_IsOff_WhenTheValuePointsElsewhere()
    {
        var reg = new FakeRegistry();
        reg.SetString(StartAtLogin.KeyPath, "NotchTasks", "\"C:\\Old\\NotchTasks.exe\"");

        Assert.False(StartAtLogin.IsOn(reg, Exe));
    }

    [Fact]
    public void FirstRun_TurnsOnOnceAndRemembers()
    {
        var reg = new FakeRegistry();
        var state = Path.Combine(_dir, "state.json");

        StartAtLogin.ApplyFirstRun(reg, Exe, state);

        Assert.True(StartAtLogin.IsOn(reg, Exe));
        Assert.True(JsonNode.Parse(File.ReadAllText(state))!["loginItemSet"]!.GetValue<bool>());
    }

    [Fact]
    public void FirstRun_DoesNothingOnLaterRuns()
    {
        var reg = new FakeRegistry();
        var state = Path.Combine(_dir, "state.json");
        StartAtLogin.ApplyFirstRun(reg, Exe, state);
        StartAtLogin.Set(reg, Exe, false);

        StartAtLogin.ApplyFirstRun(reg, Exe, state);

        Assert.False(StartAtLogin.IsOn(reg, Exe));
    }
}
