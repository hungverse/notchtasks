using NotchTasks.Core.Asks;
using static NotchTasks.Core.L10n;

namespace NotchTasks.Core.Permissions;

/// The state of one permission row. Checking means the state is not known yet (or cannot be read).
public enum PermState
{
    Granted,
    Denied,
    NotAsked,
    Checking,
}

/// The pane's groups (Preferences.swift PermissionList): what the chosen modules need, what is allowed, and what is not used.
public enum PermGroup
{
    Needed,
    Allowed,
    NotUsed,
}

/// One row of the Permissions pane. Module is the module that needs it (null when none does).
public sealed record PermItem(string Id, string Title, string Detail, string Glyph, string? Module, PermState State);

/// What the probe found. A null value is unknown: the row shows Checking.
public sealed record PermissionFacts(
    bool? ToastsOn,
    bool LoginOn,
    bool? MicAllowed,
    bool? CameraAllowed,
    bool? FirewallRule,
    IReadOnlyDictionary<AgentKind, bool?> Hooks);

public static class PermissionItems
{
    /// The row id of start at login. It is not tied to a module, and it is always in use, as the Mac's login item is.
    public const string LoginId = "login";

    /// The rows in the Mac's order: notifications, start at login, microphone, camera, firewall, then one per agent hook.
    public static IReadOnlyList<PermItem> Build(PermissionFacts facts)
    {
        var items = new List<PermItem>
        {
            new("notifications", L("Notifications"), L("Alerts when a task finishes or fails."), "", null,
                facts.ToastsOn switch { true => PermState.Granted, false => PermState.Denied, null => PermState.Checking }),
            new(LoginId, L("Open at login"), L("Starts when you log in."), "", null,
                facts.LoginOn ? PermState.Granted : PermState.NotAsked),
            new("mic", L("Microphone"), L("Lets the notch show when an app uses the mic, and calls."), "", "privacy",
                Consent(facts.MicAllowed)),
            new("camera", L("Camera mirror"), L("Check the camera before joining a meeting"), "", "mirror",
                Consent(facts.CameraAllowed)),
            new("firewall", L("Firewall rule"), L("Lets your phone reach the notch on this network."), "", null,
                facts.FirewallRule switch { true => PermState.Granted, false => PermState.NotAsked, null => PermState.Checking }),
        };

        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            var name = AgentKinds.Spec(kind).Name;
            var hook = facts.Hooks.TryGetValue(kind, out var on) ? on : null;
            items.Add(new PermItem(
                "hook." + kind.Id(),
                L("{0} permission prompts", name),
                L("Answers {0}'s permission prompts from the notch.", name),
                "",
                "agents",
                hook switch { true => PermState.Granted, false => PermState.NotAsked, null => PermState.Checking }));
        }
        return items;
    }

    /// True when the row's feature is on: its module is among the enabled modules. Notifications are needed only with the
    /// agents or the build modules, and only while notifications are on (OnboardingGuide.needs).
    public static bool Needed(PermItem item, IReadOnlySet<string> modules, bool notifications)
    {
        if (item.Id == "notifications")
        {
            return notifications && (modules.Contains("agents") || modules.Contains("ci"));
        }
        return item.Module is not null && modules.Contains(item.Module);
    }

    /// Which group a row is in: Not used when its feature is off, Allowed once granted, else Needed. Start at login is
    /// always in use. The firewall rule has no module until pairing (Phase 4), so it stays Not used.
    public static PermGroup GroupOf(PermItem item, IReadOnlySet<string> modules, bool notifications)
    {
        var inUse = item.Id == LoginId || Needed(item, modules, notifications);
        if (!inUse)
        {
            return PermGroup.NotUsed;
        }
        return item.State == PermState.Granted ? PermGroup.Allowed : PermGroup.Needed;
    }

    /// The label of a state, as the Mac shows it (Permissions.swift PermState.label; Checking reads "Not checked" here).
    public static string Label(PermState state) => state switch
    {
        PermState.Granted => L("Allowed"),
        PermState.Denied => L("Denied"),
        PermState.NotAsked => L("Not granted"),
        _ => L("Not checked"),
    };

    /// The consent of a desktop app for the mic or the camera: its own value and its NonPackaged value. Allowed only when both
    /// say Allow, denied when either says Deny, and unknown otherwise.
    public static bool? ConsentAllowed(string? value, string? nonPackaged)
    {
        if (value == "Deny" || nonPackaged == "Deny")
        {
            return false;
        }
        return value == "Allow" && nonPackaged == "Allow" ? true : null;
    }

    private static PermState Consent(bool? allowed) => allowed switch
    {
        true => PermState.Granted,
        false => PermState.Denied,
        null => PermState.Checking,
    };
}
