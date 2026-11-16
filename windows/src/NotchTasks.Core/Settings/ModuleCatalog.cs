namespace NotchTasks.Core.Settings;

/// The modules of the notch, in the Mac's order (Settings.swift ModuleID): their English title and blurb keys (L10n reads
/// them), their Segoe Fluent Icons glyph, and whether this PC has them. Levels, calendar and fans are not on Windows.
public static class ModuleCatalog
{
    private static readonly IReadOnlyList<string> Ids =
    [
        "agents", "ci", "media", "calendar", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy",
        "levels", "network", "downloads", "health", "mirror",
    ];

    private static readonly HashSet<string> NotOnWindows = new(StringComparer.Ordinal) { "levels", "calendar", "fans" };

    /// Every module id, in the Mac's order.
    public static IReadOnlyList<string> All => Ids;

    /// Whether this PC can show the module. Unknown ids are not on Windows.
    public static bool OnWindows(string id) => Ids.Contains(id) && !NotOnWindows.Contains(id);

    /// The English title key of a module (Settings.swift ModuleID.title).
    public static string Title(string id) => id switch
    {
        "agents" => "AI agent",
        "ci" => "Build & CI",
        "media" => "Now Playing",
        "limits" => "AI limits",
        "battery" => "Battery",
        "calendar" => "Calendar",
        "shelf" => "File shelf",
        "timer" => "Timer",
        "devices" => "Devices",
        "calls" => "Calls",
        "privacy" => "Mic & camera",
        "levels" => "Volume & brightness",
        "network" => "Network",
        "downloads" => "Downloads",
        "health" => "Computer health",
        "mirror" => "Camera mirror",
        _ => id,
    };

    /// The English blurb key of a module (Settings.swift ModuleID.blurb).
    public static string Blurb(string id) => id switch
    {
        "agents" => "Agents and AI apps at work",
        "ci" => "Build, test, install, render, CI",
        "media" => "What's playing, with controls",
        "limits" => "Agent usage, alerts before running out",
        "battery" => "Plugged in, unplugged, low battery",
        "calendar" => "Upcoming events, with meeting links",
        "shelf" => "Drop files on the notch to keep them",
        "timer" => "Quick timers",
        "devices" => "Headphones, keyboards, mice and their batteries",
        "calls" => "Call time, mute button (FaceTime, Zoom…)",
        "privacy" => "Alerts when an app uses the mic or camera",
        "levels" => "Volume and brightness as you change them",
        "network" => "Offline, back online, VPN",
        "downloads" => "Download finished, show in folder",
        "health" => "Computer hot, disk almost full",
        "mirror" => "Check the camera before joining a meeting",
        _ => string.Empty,
    };

    /// The Segoe Fluent Icons glyph of a module (the Mac's SF Symbol, mapped to the closest Fluent icon).
    public static string Glyph(string id) => id switch
    {
        "agents" => "\uE8BD",   // sparkle-like: AI agent
        "ci" => "\uE90F",       // wrench: build and CI
        "media" => "\uE8D6",    // music note
        "calendar" => "\uE787", // calendar
        "timer" => "\uE916",    // timer
        "shelf" => "\uE8B7",    // folder: file shelf
        "limits" => "\uE9D9",   // gauge: AI limits
        "battery" => "\uE83F",  // battery
        "devices" => "\uE7F6",  // headphones
        "calls" => "\uE717",    // phone
        "privacy" => "\uE720",  // microphone
        "levels" => "\uE767",   // volume
        "network" => "\uE701",  // wifi
        "downloads" => "\uE896", // download
        "health" => "\uE9CA",   // health / thermometer-like
        "mirror" => "\uE722",   // camera
        _ => "\uE712",          // more
    };

    /// The modules to show, in the order the user keeps: the ids of `modules` that are on Windows and not repeated, then
    /// the Windows modules not in it, in the Mac's order.
    public static IReadOnlyList<string> Order(IReadOnlyList<string> modules)
    {
        var order = new List<string>(Ids.Count);
        foreach (var id in modules)
        {
            if (OnWindows(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }
        foreach (var id in Ids)
        {
            if (OnWindows(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }
        return order;
    }
}
