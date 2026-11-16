namespace NotchTasks.Core.Sounds;

/// The built-in sound packs and the sound events, as the Mac has them (Customize.swift SoundPack and SoundEvent) and as
/// Electron has them (settings.js SOUND_PACKS, SOUND_EVENTS). Names and event titles are the Mac's English keys: the
/// panes show them through L10n, and Electron's Vietnamese pack names are legacy keys that L10n maps to these.
/// A sound name of "" is silent.
public static class SoundPacks
{
    /// The events, in the Mac's order, with their English titles.
    public static IReadOnlyList<(string Id, string Title)> Events { get; } =
    [
        ("taskDone", "AI / task done"),
        ("agentAsk", "Agent asks for approval"),
        ("ciPassed", "CI passed"),
        ("ciFailed", "CI failed"),
        ("limitWarn", "AI limit running out"),
        ("meeting", "Upcoming event"),
        ("timerDone", "Timer done"),
        ("charging", "Plugged in"),
        ("unplugged", "Unplugged"),
        ("lowBattery", "Low battery"),
        ("deviceOn", "Device connected"),
        ("deviceOff", "Device disconnected"),
        ("callStart", "Call started"),
        ("callEnd", "Call ended"),
        ("sensorOn", "Mic or camera on"),
        ("authFail", "Failed unlock"),
        ("netDown", "Offline"),
        ("netUp", "Back online, VPN"),
        ("downloadDone", "Download done"),
        ("warning", "Hot computer, disk full"),
        ("open", "Opening the notch"),
        ("close", "Notch collapses"),
    ];

    /// The packs in the Mac's order. The first is the Default pack, which fills any event missing from a saved map.
    public static IReadOnlyList<(string Name, IReadOnlyDictionary<string, string> Map)> All { get; } =
    [
        ("Default", Pack(
            taskDone: "Glass", agentAsk: "Ping", ciPassed: "Hero", ciFailed: "Basso", limitWarn: "Funk", meeting: "Ping",
            timerDone: "Glass", charging: "Pop", lowBattery: "Funk", deviceOn: "Pop", deviceOff: "Bottle", callStart: "Tink",
            callEnd: "Bottle", authFail: "Basso", netDown: "Basso", netUp: "Tink", downloadDone: "Glass", warning: "Funk")),
        ("Minimal", Pack(
            taskDone: "Tink", agentAsk: "Tink", ciPassed: "Tink", ciFailed: "Pop", limitWarn: "Pop", meeting: "Tink",
            timerDone: "Tink", charging: "Tink", lowBattery: "Pop", deviceOn: "Tink", callStart: "Tink", authFail: "Pop",
            netDown: "Pop", downloadDone: "Tink", warning: "Pop")),
        ("Lively", Pack(
            taskDone: "Hero", agentAsk: "Submarine", ciPassed: "Hero", ciFailed: "Sosumi", limitWarn: "Funk", meeting: "Glass",
            timerDone: "Submarine", charging: "Bottle", unplugged: "Blow", lowBattery: "Funk", deviceOn: "Hero", deviceOff: "Blow",
            callStart: "Glass", callEnd: "Blow", sensorOn: "Pop", authFail: "Sosumi", netDown: "Sosumi", netUp: "Hero",
            downloadDone: "Hero", warning: "Funk", open: "Pop", close: "Tink")),
        ("Retro", Pack(
            taskDone: "Morse", agentAsk: "Ping", ciPassed: "Frog", ciFailed: "Basso", limitWarn: "Purr", meeting: "Ping",
            timerDone: "Submarine", charging: "Bottle", unplugged: "Blow", lowBattery: "Sosumi", deviceOn: "Morse", deviceOff: "Purr",
            callStart: "Morse", callEnd: "Purr", authFail: "Basso", netDown: "Basso", netUp: "Morse", downloadDone: "Frog",
            warning: "Sosumi")),
        ("Silent", Pack()),
    ];

    /// The name of the pack that `map` matches exactly, or null for a custom map. An event missing from `map` counts as
    /// the Default pack's sound for it.
    public static string? Current(IReadOnlyDictionary<string, string> map)
    {
        foreach (var (name, packMap) in All)
        {
            if (Events.All(e => SoundOf(map, e.Id) == packMap[e.Id]))
            {
                return name;
            }
        }
        return null;
    }

    private static string SoundOf(IReadOnlyDictionary<string, string> map, string eventId) =>
        map.TryGetValue(eventId, out var sound) ? sound : All[0].Map[eventId];

    /// A pack's map with all 22 events; each parameter is an event's sound ("" when silent).
    private static IReadOnlyDictionary<string, string> Pack(
        string taskDone = "", string agentAsk = "", string ciPassed = "", string ciFailed = "", string limitWarn = "",
        string meeting = "", string timerDone = "", string charging = "", string unplugged = "", string lowBattery = "",
        string deviceOn = "", string deviceOff = "", string callStart = "", string callEnd = "", string sensorOn = "",
        string authFail = "", string netDown = "", string netUp = "", string downloadDone = "", string warning = "",
        string open = "", string close = "")
    {
        var sounds = new[]
        {
            taskDone, agentAsk, ciPassed, ciFailed, limitWarn, meeting, timerDone, charging, unplugged, lowBattery, deviceOn,
            deviceOff, callStart, callEnd, sensorOn, authFail, netDown, netUp, downloadDone, warning, open, close,
        };
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < sounds.Length; i++)
        {
            map[Events[i].Id] = sounds[i];
        }
        return map;
    }
}
