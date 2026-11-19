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

    /// Vector stand-ins for the two names the font does not draw here: a gauge and an hourglass, the Mac's gauge and hourglass
    /// symbols. No Segoe Fluent Icons glyph was found for either (E916 is a stopwatch), so they are stroked paths in a 16 by 16
    /// box. Null for every other name.
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // a half dial with its baseline, and a needle from the centre
        ["gauge"] = "M2,12 A6,6 0 0 1 14,12 M2,12 H14 M8,12 L11.2,7.2",
        // a top and a bottom bar, joined by the two halves of the glass that meet in the middle
        ["hourglass"] = "M3,1.5 H13 M3,14.5 H13 M4.5,1.5 V3.5 L8,8 L11.5,3.5 V1.5 M4.5,14.5 V12.5 L8,8 L11.5,12.5 V14.5",
    };

    /// The glyph for a name, or the fallback when the name is not in the map.
    public static string Get(string name) => Map.TryGetValue(name, out var code) ? code : Fallback;

    /// The path data of a name the font does not draw (gauge, hourglass), or null.
    public static string? Vector(string name) => Paths.TryGetValue(name, out var data) ? data : null;
}
