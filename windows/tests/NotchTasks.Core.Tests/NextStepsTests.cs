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

    // The Mac's calendar row maps to the Windows mic row (privacy or calls); the camera row keeps the mirror module.
    private static PermItem Mic(PermState state = PermState.NotAsked) => Item("mic", state, "privacy", "calls");

    private static PermItem Camera(PermState state = PermState.NotAsked) => Item("camera", state, "mirror");

    [Fact]
    public void NextSteps_PermissionsFollowChoices()
    {
        var calls = new HashSet<string> { "calls" };

        Assert.True(PermissionItems.Needed(Mic(), calls, notifications: false));
        Assert.False(PermissionItems.Needed(Camera(), calls, notifications: false));

        var steps = NextSteps.Compute(new[] { Mic(), Camera() }, calls, notifications: false,
            present: [], installed: new HashSet<AgentKind>());
        Assert.Equal(new NextStep[] { new NextStep.Permission("mic") }, steps);
    }

    [Fact]
    public void NextSteps_Order()
    {
        var steps = NextSteps.Compute(new[] { Mic(), Camera() }, new HashSet<string> { "calls" }, notifications: false,
            present: [AgentKind.Claude], installed: new HashSet<AgentKind>());

        Assert.Equal(new NextStep[] { new NextStep.Permission("mic"), new NextStep.Agent(AgentKind.Claude) }, steps);
    }

    [Fact]
    public void NextSteps_EmptyWhenDone()
    {
        Assert.Empty(NextSteps.Compute(new[] { Mic() }, new HashSet<string>(), notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(new[] { Mic(PermState.Granted) }, new HashSet<string> { "calls" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(new[] { Mic(PermState.Checking) }, new HashSet<string> { "calls" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
        Assert.Empty(NextSteps.Compute(Array.Empty<PermItem>(), new HashSet<string> { "calls" }, notifications: false,
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
    public void NextSteps_HiddenRowIsNotAStep()
    {
        // A row that does not apply on this computer (an agent that is not installed) is never a step.
        var absent = Item("hook.gemini", PermState.NotAsked, "agents") with { Applies = false };

        Assert.Empty(NextSteps.Compute(new[] { absent }, new HashSet<string> { "agents" }, notifications: false,
            present: [], installed: new HashSet<AgentKind>()));
    }
}
