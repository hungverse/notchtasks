using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;

namespace NotchTasks.Core.Onboarding;

/// One row of the Overview's next steps (Onboarding.swift OnboardingGuide.NextStep). The phone step is not here until pairing
/// is built (Phase 4).
public abstract record NextStep
{
    /// A permission row that is needed and not granted yet; Id is the PermItem's id.
    public sealed record Permission(string Id) : NextStep;

    /// An agent that is on this computer and whose hook is not installed yet.
    public sealed record Agent(AgentKind Kind) : NextStep;
}

public static class NextSteps
{
    /// The steps still to do, in order: the needed permissions that are not granted (in the rows' order), then the agents
    /// present here whose hook is not installed (in the AgentKind order). Empty when nothing is left.
    public static IReadOnlyList<NextStep> Compute(IReadOnlyList<PermItem> items, IReadOnlySet<string> modules, bool notifications,
        IReadOnlyList<AgentKind> present, IReadOnlySet<AgentKind> installed)
    {
        var steps = new List<NextStep>();
        foreach (var item in items)
        {
            if (PermissionItems.Needed(item, modules, notifications) && item.State is not (PermState.Granted or PermState.Checking))
            {
                steps.Add(new NextStep.Permission(item.Id));
            }
        }
        foreach (var kind in present)
        {
            if (!installed.Contains(kind))
            {
                steps.Add(new NextStep.Agent(kind));
            }
        }
        return steps;
    }
}
