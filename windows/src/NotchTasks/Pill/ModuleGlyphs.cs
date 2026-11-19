namespace NotchTasks.Pill;

/// The Segoe Fluent Icons glyph of each glyph name a toast or a chip carries (Toast.Glyph, the chip's lead). A name the map
/// does not know draws the fallback, never nothing. The map is completed and tested in Task 11.
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
    };

    /// The glyph for a name, or the fallback when the name is not in the map.
    public static string Get(string name) => Map.TryGetValue(name, out var code) ? code : Fallback;
}
