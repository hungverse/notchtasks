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
/// row that does not apply on this computer (an agent that is not installed here): the pane hides it. Glyph is a Fluent System
/// Icons codepoint, in the Filled font when Filled is set (the Mac draws that row's symbol filled).
public sealed record PermItem(
    string Id,
    string Title,
    string Detail,
    string Glyph,
    IReadOnlyList<string> Modules,
    PermState State,
    bool Applies = true,
    bool Filled = false);

/// What the probe found. A null value is unknown: the row shows Checking. Present says which agents are installed here.
public sealed record PermissionFacts(
    bool? ToastsOn,
    bool LoginOn,
    bool? MicAllowed,
    bool? CameraAllowed,
    bool? FirewallRule,
    IReadOnlyDictionary<AgentKind, bool?> Hooks,
    IReadOnlyDictionary<AgentKind, bool> Present,
    PermState Temperatures = PermState.NotAsked);

public static class PermissionItems
{
    /// The row id of start at login. It is not tied to a module, and it is always in use, as the Mac's login item is.
    public const string LoginId = "login";

    /// The row id of the optional temperatures helper. It is never needed: it is in use only once it is on (see GroupOf).
    public const string TemperaturesId = "temperatures";

    private static readonly IReadOnlyList<string> NoModules = Array.Empty<string>();
    private static readonly IReadOnlyList<string> CameraModules = ["mirror"];
    private static readonly IReadOnlyList<string> AgentModules = ["agents"];

    /// The glyphs of the rows: the Mac's symbols as Fluent icons (notification bell, power, microphone and camera filled as the
    /// Mac draws them, thermometer, shield, and the sparkle the agents use).
    private const string BellIcon = "";        // alert
    private const string PowerIcon = "";       // power
    private const string MicIcon = "";         // mic, filled
    private const string CameraIcon = "";      // camera, filled
    private const string TemperatureIcon = ""; // temperature
    private const string ShieldIcon = "";      // shield
    private const string SparkleIcon = "";     // sparkle: the agents

    /// Whether the firewall row is shown. The rule lets the phone reach the notch, so the row comes with pairing (Phase 4).
    /// Until then the row is not built and the rule is not probed; the code for it stays in place.
    public static readonly bool FirewallRowShown = false;

    /// The rows in the Mac's order: notifications, start at login, microphone, camera, firewall (when shown), then one per agent
    /// hook. An agent that is not installed here gets a row with Applies false.
    public static IReadOnlyList<PermItem> Build(PermissionFacts facts)
    {
        var items = new List<PermItem>
        {
            new("notifications", L("Notifications"), L("Alerts when a task finishes or fails."), BellIcon, NoModules,
                facts.ToastsOn switch { true => PermState.Granted, false => PermState.Denied, null => PermState.Checking }),
            new(LoginId, L("Open at login"), L("Starts when you log in."), PowerIcon, NoModules,
                facts.LoginOn ? PermState.Granted : PermState.NotAsked),
            new("mic", L("Microphone"), L("Lets the notch show when an app uses the mic, and calls."), MicIcon, NoModules,
                Consent(facts.MicAllowed), Filled: true),
            new("camera", L("Camera mirror"), L("Check the camera before joining a meeting"), CameraIcon, CameraModules,
                Consent(facts.CameraAllowed), Filled: true),
        };
        items.Add(new PermItem(TemperaturesId, L("CPU temperature"),
            L("Optional. Reading temperatures on Windows needs administrator, so a small helper runs with administrator rights only if you turn this on."),
            TemperatureIcon, NoModules, facts.Temperatures));
        if (FirewallRowShown)
        {
            items.Add(new PermItem("firewall", L("Firewall rule"), L("Lets your phone reach the notch on this network."), ShieldIcon, NoModules,
                facts.FirewallRule switch { true => PermState.Granted, false => PermState.NotAsked, null => PermState.Checking }));
        }

        foreach (var kind in Enum.GetValues<AgentKind>())
        {
            var name = AgentKinds.Spec(kind).Name;
            var hook = facts.Hooks.TryGetValue(kind, out var on) ? on : null;
            var here = facts.Present.TryGetValue(kind, out var present) && present;
            items.Add(new PermItem(
                "hook." + kind.Id(),
                L("{0} permission prompts", name),
                L("Answers {0}'s permission prompts from the notch.", name),
                SparkleIcon,
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
        if (item.Id == TemperaturesId)
        {
            return false;  // optional: never counted as needed, whatever the modules
        }
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
        if (item.Id == TemperaturesId)
        {
            // Optional: in Granted while the helper is on, else in Not in use. Never Needed.
            return item.State == PermState.Granted ? PermGroup.Granted : PermGroup.NotInUse;
        }
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
