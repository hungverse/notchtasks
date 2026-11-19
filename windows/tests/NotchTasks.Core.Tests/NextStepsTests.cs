using NotchTasks.Core.Asks;
using NotchTasks.Core.Onboarding;
using NotchTasks.Core.Permissions;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from OnboardingGuide.selfTest (Onboarding.swift). Uses the English keys, so it shares the "L10n" collection.
[Collection("L10n")]
public sealed class NextStepsTests
{
    private static PermItem Item(string id, PermState state, params string[] modules) =>
        new(id, id, "", "", modules, state);

    // Phase 2 builds only the agents: the agent hook row is the one permission row whose module is on and built.
    private static PermItem Hook(PermState state = PermState.NotAsked) => Item("hook.claude", state, "agents");

    // Mic and camera rows need privacy, calls and mirror, which are not built yet: they are never a step in Phase 2.
    // The real mic row has no modules (ruling P3-R4): privacy and calls never make it a step.
    private static PermItem Mic(PermState state = PermState.NotAsked) => Item("mic", state);

    [Fact]
    public void NextSteps_PermissionsFollowChoices()
    {
        var agents = new HashSet<string> { "agents" };

        Assert.True(PermissionItems.Needed(Hook(), agents, notifications: false));
        Assert.False(PermissionItems.Needed(Mic(), agents, notifications: false));

        var steps = NextSteps.Compute(new[] { Hook(), Mic() }, agents, notifications: false,
            present: [], installed: new HashSet<AgentKind>());
        Assert.Equal(new NextStep[] { new NextStep.Permission("hook.claude") }, steps);
    }

    [Fact]
    public void NextSteps_Order()
    {
        var steps = NextSteps.Compute(new[] { Hook(), Mic() }, new HashSet<string> { "agents" }, notifications: false,
            present: [AgentKind.Codex], installed: new HashSet<AgentKind>());

        Assert.Equal(new NextStep[] { new NextStep.Permission("hook.claude"), new NextStep.Agent(AgentKind.Codex) }, steps);
    }

    [Fact]
    public void NextSteps_EmptyWhenDone()
    {
        Assert.Empty(NextSteps.Compute(new[] { Hook() }, new HashSet<string>(), notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(new[] { Hook(PermState.Granted) }, new HashSet<string> { "agents" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(new[] { Hook(PermState.Checking) }, new HashSet<string> { "agents" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(Array.Empty<PermItem>(), new HashSet<string> { "agents" }, notifications: false,
            present: [AgentKind.Claude], installed: new HashSet<AgentKind> { AgentKind.Claude }));
    }

    [Fact]
    public void NextSteps_AgentsOnlyWhenPresentAndNotInstalled()
    {
        var steps = NextSteps.Compute(Array.Empty<PermItem>(), new HashSet<string>(), notifications: false,
            present: [AgentKind.Claude, AgentKind.Codex], installed: new HashSet<AgentKind> { AgentKind.Claude });

        Assert.Equal(new NextStep[] { new NextStep.Agent(AgentKind.Codex) }, steps);
    }

    [Fact]
    public void NextSteps_NotificationsNeededWithAgents()
    {
        var notifications = Item("notifications", PermState.NotAsked);

        var steps = NextSteps.Compute(new[] { notifications }, new HashSet<string> { "agents" }, notifications: true,
            present: [], installed: new HashSet<AgentKind>());
        Assert.Equal(new NextStep[] { new NextStep.Permission("notifications") }, steps);

        Assert.Empty(NextSteps.Compute(new[] { notifications }, new HashSet<string> { "agents" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
    }

    [Fact]
    public void NextSteps_IgnoresUnbuiltModules()
    {
        // Only the modules that are on and built ask for permissions. Privacy is built now, but the mic row has no modules (P3-R4),
        // so it is never a step; ci is not built yet.
        Assert.Empty(NextSteps.Compute(new[] { Mic() }, new HashSet<string> { "privacy" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(new[] { Item("notifications", PermState.NotAsked) }, new HashSet<string> { "ci" },
            notifications: true, present: [], installed: new HashSet<AgentKind>()));
    }

    [Fact]
    public void NextSteps_HiddenRowIsNotAStep()
    {
        // A row that does not apply on this computer (an agent that is not installed) is never a step.
        var absent = Item("hook.gemini", PermState.NotAsked, "agents") with { Applies = false };

        Assert.Empty(NextSteps.Compute(new[] { absent }, new HashSet<string> { "agents" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
    }
}
