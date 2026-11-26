namespace NotchTasks.Core.Settings;

/// The modules of the notch, in the Mac's order (Settings.swift ModuleID): their English title and blurb keys (L10n reads
/// them), their Fluent System Icons glyph, and whether this PC has them. Levels, calendar and fans are not on Windows.
public static class ModuleCatalog
{
    private static readonly IReadOnlyList<string> Ids =
    [
        "agents", "ci", "media", "calendar", "timer", "shelf", "limits", "battery", "devices", "calls", "privacy",
        "levels", "network", "downloads", "health", "mirror",
    ];

    private static readonly HashSet<string> NotOnWindows = new(StringComparer.Ordinal) { "levels", "calendar", "fans" };

    /// The modules this build actually does. Any id not listed here shows "Coming soon" and asks for no permission
    /// (PermissionItems.Needed, NextSteps).
    private static readonly HashSet<string> BuiltIds = new(StringComparer.Ordinal)
    {
        "agents", "ci", "timer", "media", "shelf", "battery", "network", "downloads", "health", "privacy", "calls", "limits",
        "devices", "mirror",
    };

    /// Every module id, in the Mac's order.
    public static IReadOnlyList<string> All => Ids;

    /// Whether this PC can show the module. Unknown ids are not on Windows.
    public static bool OnWindows(string id) => Ids.Contains(id) && !NotOnWindows.Contains(id);

    /// Whether this build has the module's feature. An unbuilt module keeps its settings but does nothing yet.
    public static bool Built(string id) => BuiltIds.Contains(id);

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

    /// The Fluent System Icons glyph of a module: the Mac's SF Symbol, as the closest Fluent icon. The codepoint is in the
    /// Filled font when GlyphFilled says so (the Mac draws that symbol filled), else in the Regular font.
    public static string Glyph(string id) => id switch
    {
        "agents" => "\uEB33",   // sparkle (Mac sparkles): AI agent
        "ci" => "\uEE86",       // wrench, filled (Mac wrench.and.screwdriver.fill): build and CI
        "media" => "\uE854",    // music_note_2 (Mac music.note)
        "calendar" => "\uE24E", // calendar_ltr (Mac calendar)
        "timer" => "\uED88",    // timer (Mac timer)
        "shelf" => "\uF41C",    // folder, filled (Mac tray.full.fill): file shelf
        "limits" => "\uF4C7",   // gauge (Mac gauge.with.dots.needle.67percent): AI limits
        "battery" => "\uF1C5",  // battery_5 (Mac battery.100percent)
        "devices" => "\uE6EE",  // headphones (Mac headphones)
        "calls" => "\uF5EA",    // phone, filled (Mac phone.fill)
        "privacy" => "\uE807",  // mic, filled (Mac mic.fill): microphone
        "levels" => "\uEB4B",   // speaker_2, filled (Mac speaker.wave.2.fill): volume
        "network" => "\uF8B2",  // wifi_4 (Mac wifi)
        "downloads" => "\uF12C", // arrow_circle_down, filled (Mac arrow.down.circle.fill)
        "health" => "\uF78F",   // temperature (Mac thermometer.medium): the computer's heat
        "mirror" => "\uF254",   // camera, filled (Mac video.fill): the camera mirror
        _ => "\uE824",          // more_horizontal
    };

    /// Whether a module's Glyph is in the Filled font: the Mac draws that module's symbol filled.
    public static bool GlyphFilled(string id) => id is "ci" or "shelf" or "calls" or "privacy" or "levels" or "downloads" or "mirror";

    /// The stored list with `id` swapped with the previous (delta -1) or next (delta 1) visible Windows module. Only the
    /// visible modules move among themselves: every other stored id (hidden on Windows, unknown) keeps its position. A
    /// move past either end, or of an id that is not visible, returns the stored list unchanged.
    public static IReadOnlyList<string> Reorder(IReadOnlyList<string> stored, string id, int delta) =>
        Reorder(stored, id, delta, _ => true);

    /// Reorder, where only the visible modules `shown` accepts move. A visible module that `shown` rejects (a row hidden on
    /// this PC, the battery without a battery) keeps its stored place, as a hidden id does, so a move swaps only with the
    /// nearest shown neighbour and the stored list keeps every other id where it was.
    public static IReadOnlyList<string> Reorder(IReadOnlyList<string> stored, string id, int delta, Func<string, bool> shown)
    {
        var visible = Order(stored).Where(shown).ToList();
        var index = visible.IndexOf(id);
        return Move(stored, index, index + delta, shown);
    }

    /// The stored list with the visible module at index `from` moved to index `to` among the visible modules (both as
    /// counted by Order and `shown`): the rows the arrows and a drag both use. Out of range, or from equal to to, returns
    /// the stored list unchanged. Hidden ids keep their stored places, as in Reorder.
    public static IReadOnlyList<string> Move(IReadOnlyList<string> stored, int from, int to, Func<string, bool> shown)
    {
        var visible = Order(stored).Where(shown).ToList();
        if (from < 0 || from >= visible.Count || to < 0 || to >= visible.Count || from == to)
        {
            return stored.ToList();
        }
        var moved = visible[from];
        visible.RemoveAt(from);
        visible.Insert(to, moved);

        // Walk the stored list: a visible module's slot takes the next visible module of the new order; hidden ids stay.
        var result = new List<string>(stored.Count + visible.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var item in stored)
        {
            if (!OnWindows(item) || !shown(item))
            {
                result.Add(item);
            }
            else if (seen.Add(item))
            {
                result.Add(visible[next++]);
            }
        }
        // Visible modules not in the stored list follow, in the new order.
        result.AddRange(visible.Skip(next));
        return result;
    }

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
