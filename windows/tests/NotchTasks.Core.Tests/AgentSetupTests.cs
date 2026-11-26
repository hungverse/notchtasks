using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/src/core/asks.js (codexTrusted, geminiSignedIn, setupStep, present, openInTerminal). Temp homes only.
[Collection("L10n")]
public sealed class AgentSetupTests : IDisposable
{
    private const string Exe = @"C:\Program Files\NotchTasks\NotchTasksHook.exe";
    private readonly string _home;

    public AgentSetupTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "nt-setup-" + Guid.NewGuid().ToString("N"));
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

    private void Write(string relative, string text)
    {
        var file = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    [Fact]
    public void CodexTrusted_ReadsConfigToml()
    {
        Assert.False(AgentSetup.CodexTrusted(_home));

        Write(".codex/config.toml", "model = \"x\"\n[hooks.state.\"C:\\\\h\\\\hooks.json:permission_request:0:0\"]\ntrusted = true\n");

        Assert.True(AgentSetup.CodexTrusted(_home));
    }

    [Fact]
    public void GeminiSignedIn_ApiKeyInEnvironment()
    {
        Assert.True(AgentSetup.GeminiSignedIn(_home, hasApiKey: true));
        Assert.False(AgentSetup.GeminiSignedIn(_home, hasApiKey: false));
    }

    [Fact]
    public void GeminiSignedIn_SelectedTypeInSettings()
    {
        Write(".gemini/settings.json", "{\"security\":{\"auth\":{\"selectedType\":\"oauth-personal\"}}}");

        Assert.True(AgentSetup.GeminiSignedIn(_home, hasApiKey: false));
    }

    [Fact]
    public void Present_FindsTheCliOnTheGivenPath()
    {
        var bin = Path.Combine(_home, "bin");
        Write("bin/codex.exe", "");

        Assert.True(AgentSetup.Present(AgentKind.Codex, _home, bin));
        Assert.False(AgentSetup.Present(AgentKind.Gemini, _home, bin));
        Assert.True(AgentSetup.Present(AgentKind.Claude, _home, ""));
    }

    [Fact]
    public void Present_CodexDesktopApp_NoCliNeeded_OnItsMarker()
    {
        var local = Path.Combine(_home, "LocalAppData");
        Directory.CreateDirectory(local);
        Assert.False(AgentSetup.Present(AgentKind.Codex, _home, path: "", appData: local, localAppData: local));

        Write(".codex/.desktop-created", "");

        Assert.True(AgentSetup.Present(AgentKind.Codex, _home, path: "", appData: local, localAppData: local));
    }

    [Fact]
    public void Present_CodexDesktopApp_NoCliNeeded_OnItsPackageAliasFolder()
    {
        var local = Path.Combine(_home, "LocalAppData");
        Directory.CreateDirectory(Path.Combine(local, "Microsoft", "WindowsApps", "OpenAI.Codex_26.1101.1234.0_x64__2p2nqsd0c76g0"));

        Assert.True(AgentSetup.Present(AgentKind.Codex, _home, path: "", appData: local, localAppData: local));
        // the desktop app is Codex's signal only: it does not make Gemini present
        Assert.False(AgentSetup.Present(AgentKind.Gemini, _home, path: "", appData: local, localAppData: local));
    }

    [Fact]
    public void SetupStep_CodexNeedsTrustOnceTheHookIsOn()
    {
        Assert.Null(AgentSetup.SetupStep(AgentKind.Codex, _home, geminiKeySet: false, path: ""));

        HookInstaller.Install(AgentKind.Codex, Exe, _home);
        var step = AgentSetup.SetupStep(AgentKind.Codex, _home, geminiKeySet: false, path: "");
        Assert.NotNull(step);
        Assert.Equal("Not trusted in Codex yet", step!.Missing);
        Assert.Equal("Open Codex", step.Button);
        Assert.Equal("codex", step.Command);

        Write(".codex/config.toml", "[hooks.state.\"x:hooks.json:permission_request:0:0\"]\n");
        Assert.Null(AgentSetup.SetupStep(AgentKind.Codex, _home, geminiKeySet: false, path: ""));
    }

    [Fact]
    public void SetupStep_GeminiNeedsSignInWhenInstalled()
    {
        var bin = Path.Combine(_home, "bin");
        Write("bin/gemini.cmd", "");

        var step = AgentSetup.SetupStep(AgentKind.Gemini, _home, geminiKeySet: false, path: bin);

        Assert.NotNull(step);
        Assert.Equal("Not signed in", step!.Missing);
        Assert.Equal("Sign in", step.Button);
        Assert.Equal("gemini", step.Command);
    }

    [Fact]
    public void WriteTerminalScript_RunsTheCommandInTheHomeFolder()
    {
        var file = AgentSetup.WriteTerminalScript("codex", _home);

        Assert.Equal(Path.Combine(_home, "codex-setup.cmd"), file);
        var text = File.ReadAllText(file);
        Assert.Contains("codex", text);
        Assert.Contains("%USERPROFILE%", text);
    }
}
