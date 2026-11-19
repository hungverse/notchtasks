using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;
using Xunit;

namespace NotchTasks.Core.Tests;

// Uses the English keys (no language is set here), so it shares the "L10n" collection with the tests that change the language.
[Collection("L10n")]
public sealed class PermissionItemsTests
{
    private static PermItem Item(string id, params string[] modules) =>
        new(id, id, "", "", modules, PermState.NotAsked);

    private static PermissionFacts Facts(bool? toasts = null, bool login = false, bool? mic = null, bool? camera = null,
        bool? firewall = null, Dictionary<AgentKind, bool?>? hooks = null, Dictionary<AgentKind, bool>? present = null) =>
        new(toasts, login, mic, camera, firewall,
            hooks ?? new Dictionary<AgentKind, bool?>(),
            present ?? Enum.GetValues<AgentKind>().ToDictionary(kind => kind, _ => true));

    private static PermItem Find(IReadOnlyList<PermItem> items, string id) => items.Single(i => i.Id == id);

    [Fact]
    public void Build_NoFirewallRow_UntilPairing()
    {
        // The rule comes with pairing (Phase 4): until then the row is not built.
        Assert.False(PermissionItems.FirewallRowShown);
        Assert.DoesNotContain(PermissionItems.Build(Facts(firewall: true)), item => item.Id == "firewall");
    }

    [Fact]
    public void Needed_FollowsModule()
    {
        // The agent hook is the one row whose module is built in Phase 2.
        var hook = Item("hook.claude", "agents");

        Assert.True(PermissionItems.Needed(hook, new HashSet<string> { "agents" }, notifications: false));
        Assert.False(PermissionItems.Needed(hook, new HashSet<string> { "ci" }, notifications: false));
        Assert.False(PermissionItems.Needed(Item("firewall"), new HashSet<string> { "agents" }, notifications: false));
    }

    [Fact]
    public void Mic_RowHasNoModules_SoItIsNeverNeeded()
    {
        // Ruling P3-R4: the mic row is not tied to the privacy or calls modules, so it is Not in use even when both are on.
        var items = PermissionItems.Build(Facts(mic: false));
        var mic = Find(items, "mic");
        var modules = new HashSet<string> { "privacy", "calls" };

        Assert.Empty(mic.Modules);
        Assert.False(PermissionItems.Needed(mic, modules, notifications: false));
        Assert.Equal(PermGroup.NotInUse, PermissionItems.GroupOf(mic, modules, notifications: false));
        Assert.False(PermissionItems.Needed(Item("camera", "mirror"), new HashSet<string> { "mirror" }, notifications: false));
    }

    [Fact]
    public void Needed_NotificationsOnlyWithAgentsOrCi()
    {
        var notifications = Item("notifications");

        Assert.True(PermissionItems.Needed(notifications, new HashSet<string> { "agents" }, notifications: true));
        Assert.False(PermissionItems.Needed(notifications, new HashSet<string> { "ci" }, notifications: true));
        Assert.False(PermissionItems.Needed(notifications, new HashSet<string> { "media" }, notifications: true));
        Assert.False(PermissionItems.Needed(notifications, new HashSet<string> { "agents" }, notifications: false));
    }

    [Fact]
    public void Needed_AbsentAgent_NotNeeded()
    {
        var items = PermissionItems.Build(Facts(
            hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Gemini] = false },
            present: new Dictionary<AgentKind, bool> { [AgentKind.Gemini] = false }));

        var gemini = Find(items, "hook.gemini");
        Assert.False(gemini.Applies);
        Assert.False(PermissionItems.Needed(gemini, new HashSet<string> { "agents" }, notifications: true));
        Assert.Equal(PermGroup.NotInUse, PermissionItems.GroupOf(gemini, new HashSet<string> { "agents" }, notifications: true));
    }

    [Fact]
    public void Needed_PresentAgentWithoutHook_Needed()
    {
        var modules = new HashSet<string> { "agents" };
        var missing = PermissionItems.Build(Facts(hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Claude] = false }));
        var installed = PermissionItems.Build(Facts(hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Claude] = true }));

        Assert.True(PermissionItems.Needed(Find(missing, "hook.claude"), modules, notifications: true));
        Assert.Equal(PermGroup.Needed, PermissionItems.GroupOf(Find(missing, "hook.claude"), modules, notifications: true));
        Assert.Equal(PermGroup.Granted, PermissionItems.GroupOf(Find(installed, "hook.claude"), modules, notifications: true));
    }

    [Fact]
    public void Build_GroupsStates()
    {
        var items = PermissionItems.Build(Facts(
            toasts: null, login: true, mic: false, camera: null, firewall: false,
            hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Claude] = true, [AgentKind.Codex] = false }));

        Assert.Equal(PermState.Checking, Find(items, "notifications").State);
        Assert.Equal(PermState.Granted, Find(items, "login").State);
        Assert.Equal(PermState.Denied, Find(items, "mic").State);
        Assert.Equal(PermState.Checking, Find(items, "camera").State);
        Assert.Equal(PermState.Granted, Find(items, "hook.claude").State);
        Assert.Equal(PermState.NotAsked, Find(items, "hook.codex").State);
        Assert.Equal(PermState.Checking, Find(items, "hook.gemini").State);
    }

    [Fact]
    public void Group_PutsEachRowWhereItIsNeeded()
    {
        var modules = new HashSet<string> { "agents", "mirror", "privacy" };
        var items = PermissionItems.Build(Facts(
            toasts: true, login: true, mic: null, camera: null, firewall: false,
            hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Claude] = true, [AgentKind.Codex] = false }));

        Assert.Equal(PermGroup.Granted, PermissionItems.GroupOf(Find(items, "login"), modules, notifications: true));
        Assert.Equal(PermGroup.NotInUse, PermissionItems.GroupOf(Find(items, "camera"), modules, notifications: true));  // mirror is not built
        Assert.Equal(PermGroup.Granted, PermissionItems.GroupOf(Find(items, "hook.claude"), modules, notifications: true));
        Assert.Equal(PermGroup.Needed, PermissionItems.GroupOf(Find(items, "hook.codex"), modules, notifications: true));
        Assert.Equal(PermGroup.NotInUse, PermissionItems.GroupOf(Find(items, "hook.codex"), new HashSet<string> { "mirror" }, notifications: true));
    }

    [Theory]
    [InlineData("Allow", "Allow", true)]
    [InlineData("Allow", null, true)]
    [InlineData("Deny", null, false)]
    [InlineData(null, null, null)]
    [InlineData("Allow", "Ask", null)]
    [InlineData("Deny", "Allow", false)]
    [InlineData("Allow", "Deny", false)]
    public void ConsentAllowed_NeedsBothToAllow(string? value, string? nonPackaged, bool? expected)
    {
        Assert.Equal(expected, PermissionItems.ConsentAllowed(value, nonPackaged));
    }

    [Fact]
    public void ConsentFromRegistry_NonPackagedAbsent_UsesTopLevelValue()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        var reg = new FakeRegistry();
        reg.SetString(key, "Value", "Allow");

        Assert.True(PermissionItems.ConsentFromRegistry(reg, key));

        reg.SetString(key + PermissionItems.NonPackagedSubkey, "Value", "Deny");
        Assert.False(PermissionItems.ConsentFromRegistry(reg, key));
    }
}
