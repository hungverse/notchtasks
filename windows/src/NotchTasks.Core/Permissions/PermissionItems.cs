using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
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

/// The pane's groups (Preferences.swift PermissionList): what the chosen modules need, what is granted, and what is not in use.
public enum PermGroup
{
    Needed,
    Granted,
    NotInUse,
}

/// One row of the Permissions pane. Modules are the modules that need it (none when no module does). Applies is false for a
/// row that does not apply on this computer (an agent that is not installed here): the pane hides it.
public sealed record PermItem(
    string Id,
    string Title,
    string Detail,
    string Glyph,
    IReadOnlyList<string> Modules,
    PermState State,
    bool Applies = true);

/// What the probe found. A null value is unknown: the row shows Checking. Present says which agents are installed here.
public sealed record PermissionFacts(
    bool? ToastsOn,
    bool LoginOn,
    bool? MicAllowed,
    bool? CameraAllowed,
    bool? FirewallRule,
    IReadOnlyDictionary<AgentKind, bool?> Hooks,
    IReadOnlyDictionary<AgentKind, bool> Present);

public static class PermissionItems
{
    /// The row id of start at login. It is not tied to a module, and it is always in use, as the Mac's login item is.
    public const string LoginId = "login";

    private static readonly IReadOnlyList<string> NoModules = Array.Empty<string>();
    private static readonly IReadOnlyList<string> PrivacyOrCalls = ["privacy", "calls"];
    private static readonly IReadOnlyList<string> CameraModules = ["mirror"];
    private static readonly IReadOnlyList<string> AgentModules = ["agents"];

    /// The rows in the Mac's order: notifications, start at login, microphone, camera, firewall, then one per agent hook.
    /// An agent that is not installed here gets a row with Applies false.
    public static IReadOnlyList<PermItem> Build(PermissionFacts facts)
    {
        var items = new List<PermItem>
        {
            new("notifications", L("Notifications"), L("Alerts when a task finishes or fails."), "", NoModules,
                facts.ToastsOn switch { true => PermState.Granted, false => PermState.Denied, null => PermState.Checking }),
            new(LoginId, L("Open at login"), L("Starts when you log in."), "", NoModules,
                facts.LoginOn ? PermState.Granted : PermState.NotAsked),
            new("mic", L("Microphone"), L("Lets the notch show when an app uses the mic, and calls."), "", PrivacyOrCalls,
                Consent(facts.MicAllowed)),
            new("camera", L("Camera mirror"), L("Check the camera before joining a meeting"), "", CameraModules,
                Consent(facts.CameraAllowed)),
            new("firewall", L("Firewall rule"), L("Lets your phone reach the notch on this network."), "", NoModules,
                facts.FirewallRule switch { true => PermState.Granted, false => PermState.NotAsked, null => PermState.Checking }),
        };

        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            var name = AgentKinds.Spec(kind).Name;
            var hook = facts.Hooks.TryGetValue(kind, out var on) ? on : null;
            var here = facts.Present.TryGetValue(kind, out var present) && present;
            items.Add(new PermItem(
                "hook." + kind.Id(),
                L("{0} permission prompts", name),
                L("Answers {0}'s permission prompts from the notch.", name),
                "",
                AgentModules,
                hook switch { true => PermState.Granted, false => PermState.NotAsked, null => PermState.Checking },
                Applies: here));
        }
        return items;
    }

    /// True when the row is needed: it applies here, and one of its modules is on and built. Notifications are needed only with
    /// the agents or the build modules, and only while notifications are on (OnboardingGuide.needs). Needed says nothing about
    /// the state: a granted row can still be needed (GroupOf sorts it).
    public static bool Needed(PermItem item, IReadOnlySet<string> modules, bool notifications)
    {
        if (!item.Applies)
        {
            return false;
        }
        bool On(string module) => modules.Contains(module) && ModuleCatalog.Built(module);
        if (item.Id == "notifications")
        {
            return notifications && (On("agents") || On("ci"));
        }
        return item.Modules.Any(On);
    }

    /// Which group a row is in: Not in use when it is not needed, Granted once granted, else Needed. Start at login is always
    /// in use. The firewall rule has no module until pairing (Phase 4), so it stays Not in use.
    public static PermGroup GroupOf(PermItem item, IReadOnlySet<string> modules, bool notifications)
    {
        var inUse = item.Id == LoginId || Needed(item, modules, notifications);
        if (!inUse)
        {
            return PermGroup.NotInUse;
        }
        return item.State == PermState.Granted ? PermGroup.Granted : PermGroup.Needed;
    }

    /// The label of a state (Permissions.swift PermState.label; Checking reads "Not checked" here).
    public static string Label(PermState state) => state switch
    {
        PermState.Granted => L("Allowed"),
        PermState.Denied => L("Denied"),
        PermState.NotAsked => L("Not granted"),
        _ => L("Not checked"),
    };

    /// The subkey a desktop app keeps its consent in, under the capability's own key.
    public const string NonPackagedSubkey = @"\NonPackaged";

    /// The consent of a desktop app for the mic or the camera. When the NonPackaged value is there, both values must say Allow;
    /// when it is absent, the top-level value decides. Denied when either says Deny, and unknown otherwise.
    public static bool? ConsentAllowed(string? value, string? nonPackaged)
    {
        if (value == "Deny" || nonPackaged == "Deny")
        {
            return false;
        }
        if (nonPackaged is null)
        {
            return value == "Allow" ? true : null;
        }
        return value == "Allow" && nonPackaged == "Allow" ? true : null;
    }

    /// ConsentAllowed read from the registry: the capability key's Value and its NonPackaged Value.
    public static bool? ConsentFromRegistry(IRegistry registry, string capabilityKey) =>
        ConsentAllowed(registry.GetString(capabilityKey, "Value"), registry.GetString(capabilityKey + NonPackagedSubkey, "Value"));

    private static PermState Consent(bool? allowed) => allowed switch
    {
        true => PermState.Granted,
        false => PermState.Denied,
        null => PermState.Checking,
    };
}
