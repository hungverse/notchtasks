using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;
using Xunit;

namespace NotchTasks.Core.Tests;

// Uses the English keys (no language is set here), so it shares the "L10n" collection with the tests that change the language.
[Collection("L10n")]
public sealed class PermissionItemsTests
{
    private static PermItem Item(string id, string? module) =>
        new(id, id, "", "", module, PermState.NotAsked);

    private static PermissionFacts Facts(bool? toasts = null, bool login = false, bool? mic = null, bool? camera = null,
        bool? firewall = null, Dictionary<AgentKind, bool?>? hooks = null) =>
        new(toasts, login, mic, camera, firewall, hooks ?? new Dictionary<AgentKind, bool?>());

    private static PermItem Find(IReadOnlyList<PermItem> items, string id) => items.Single(i => i.Id == id);

    [Fact]
    public void Needed_FollowsModule()
    {
        var mirror = Item("camera", "mirror");

        Assert.True(PermissionItems.Needed(mirror, new HashSet<string> { "mirror" }, notifications: false));
        Assert.False(PermissionItems.Needed(mirror, new HashSet<string> { "calls" }, notifications: false));
        Assert.False(PermissionItems.Needed(Item("firewall", null), new HashSet<string> { "mirror" }, notifications: false));
    }

    [Fact]
    public void Needed_NotificationsOnlyWithAgentsOrCi()
    {
        var notifications = Item("notifications", null);

        Assert.True(PermissionItems.Needed(notifications, new HashSet<string> { "agents" }, notifications: true));
        Assert.True(PermissionItems.Needed(notifications, new HashSet<string> { "ci" }, notifications: true));
        Assert.False(PermissionItems.Needed(notifications, new HashSet<string> { "media" }, notifications: true));
        Assert.False(PermissionItems.Needed(notifications, new HashSet<string> { "agents" }, notifications: false));
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
        Assert.Equal(PermState.NotAsked, Find(items, "firewall").State);
        Assert.Equal(PermState.Granted, Find(items, "hook.claude").State);
        Assert.Equal(PermState.NotAsked, Find(items, "hook.codex").State);
        Assert.Equal(PermState.Checking, Find(items, "hook.gemini").State);
        Assert.Null(Find(items, "firewall").Module);
    }

    [Fact]
    public void Group_PutsEachRowWhereItIsNeeded()
    {
        var modules = new HashSet<string> { "agents", "mirror", "privacy" };
        var items = PermissionItems.Build(Facts(
            toasts: true, login: true, mic: null, camera: null, firewall: false,
            hooks: new Dictionary<AgentKind, bool?> { [AgentKind.Claude] = true, [AgentKind.Codex] = false }));

        Assert.Equal(PermGroup.Allowed, PermissionItems.GroupOf(Find(items, "login"), modules, notifications: true));
        Assert.Equal(PermGroup.Needed, PermissionItems.GroupOf(Find(items, "camera"), modules, notifications: true));
        Assert.Equal(PermGroup.Allowed, PermissionItems.GroupOf(Find(items, "hook.claude"), modules, notifications: true));
        Assert.Equal(PermGroup.Needed, PermissionItems.GroupOf(Find(items, "hook.codex"), modules, notifications: true));
        Assert.Equal(PermGroup.NotUsed, PermissionItems.GroupOf(Find(items, "firewall"), modules, notifications: true));
        Assert.Equal(PermGroup.NotUsed, PermissionItems.GroupOf(Find(items, "hook.codex"), new HashSet<string> { "mirror" }, notifications: true));
    }

    [Theory]
    [InlineData("Allow", "Allow", true)]
    [InlineData("Allow", null, null)]
    [InlineData(null, null, null)]
    [InlineData("Deny", "Allow", false)]
    [InlineData("Allow", "Deny", false)]
    public void ConsentAllowed_NeedsBothToAllow(string? value, string? nonPackaged, bool? expected)
    {
        Assert.Equal(expected, PermissionItems.ConsentAllowed(value, nonPackaged));
    }
}
