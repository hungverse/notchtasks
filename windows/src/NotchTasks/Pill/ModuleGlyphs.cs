namespace NotchTasks.Pill;

/// The Segoe Fluent Icons glyph of each glyph name a toast or a chip carries (Toast.Glyph, the chip's lead). A name the map
/// does not know draws the fallback, never nothing. The map covers every glyph name the modules and toasts use.
public static class ModuleGlyphs
{
    /// What an unknown name draws (the Control Panel's "more" glyph).
    public const string Fallback = "";

    private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["hand"] = "",      // an agent's approval (AskCard): a pointing hand; E6BA is not in the font (it drew a box)
        ["question"] = "",  // an agent's question (AskCard)
        ["timer"] = "",
        ["download"] = "",
        ["drive"] = "",
        ["phone"] = "",
        ["video"] = "",
        ["mic"] = "",
        ["micoff"] = "",  // microphone with a slash: the mic is muted
        ["phonedown"] = "",  // handset with a down arrow: the call ended
        ["camera"] = "",
        ["wifi"] = "",
        ["network"] = "",
        ["vpn"] = "",
        ["wifioff"] = "",  // wifi arcs with a cross: network disconnected (Segoe Fluent Icons)
        ["vpnoff"] = "",   // open padlock: VPN off
        ["battery"] = "",
        ["bolt"] = "",        // lightning bolt: charging
        ["plug"] = "",        // power symbol, the stand-in for the plug: unplugged
        ["batterylow"] = "",  // an almost empty battery: low battery
        ["warning"] = "",
        ["thermometer"] = "\uE9CA",  // thermometer: the header temperature (Segoe Fluent Icons E9CA)
        ["check"] = "\uE73E",        // check mark: a finished job's status (Segoe Fluent Icons E73E)
        ["sparkles"] = "\uE794",     // sparkle: an agent's icon when it has no app icon (Segoe Fluent Icons E794)
        ["log"] = "\uE8E2",          // text lines: a job's log, opened on click (Segoe Fluent Icons E8E2)
    };

    /// Vector stand-ins for the names the font does not draw here: a gauge and an hourglass, the Mac's gauge and hourglass
    /// symbols, and the CI results seal and octagon (Mac checkmark.seal.fill and xmark.octagon.fill). No Segoe Fluent Icons
    /// glyph was found for them, so they are stroked paths in a 16 by 16 box, drawn in the toast's tint. Null for every other name.
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // a half dial with its baseline, and a needle from the centre
        ["gauge"] = "M2,12 A6,6 0 0 1 14,12 M2,12 H14 M8,12 L11.2,7.2",
        // a top and a bottom bar, joined by the two halves of the glass that meet in the middle
        ["hourglass"] = "M3,1.5 H13 M3,14.5 H13 M4.5,1.5 V3.5 L8,8 L11.5,3.5 V1.5 M4.5,14.5 V12.5 L8,8 L11.5,12.5 V14.5",
        // a scalloped seal: eight outward arcs around a circle, with a check mark inside
        ["seal"] = "M13.40,8.00 A2.4,2.4 0 0 1 11.82,11.82 A2.4,2.4 0 0 1 8.00,13.40 A2.4,2.4 0 0 1 4.18,11.82 A2.4,2.4 0 0 1 2.60,8.00 A2.4,2.4 0 0 1 4.18,4.18 A2.4,2.4 0 0 1 8.00,2.60 A2.4,2.4 0 0 1 11.82,4.18 A2.4,2.4 0 0 1 13.40,8.00 Z M5.2,8.2 L7.1,10.1 L10.9,6.3",
        // a stop-sign octagon with an x inside
        ["octagon"] = "M13.73,10.37 L10.37,13.73 L5.63,13.73 L2.27,10.37 L2.27,5.63 L5.63,2.27 L10.37,2.27 L13.73,5.63 Z M5.8,5.8 L10.2,10.2 M10.2,5.8 L5.8,10.2",
    };

    /// The glyph for a name, or the fallback when the name is not in the map.
    public static string Get(string name) => Map.TryGetValue(name, out var code) ? code : Fallback;

    /// The path data of a name the font does not draw (gauge, hourglass), or null.
    public static string? Vector(string name) => Paths.TryGetValue(name, out var data) ? data : null;
}
