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
        // the close mark of an alert card (Mac xmark)
        ["xmark"] = "M3.5,3.5 L12.5,12.5 M12.5,3.5 L3.5,12.5",
        // the Build & CI job symbols (Mac SF Symbol names, the key is the name itself): drawn outlines in the same 16 by 16 box
        ["hammer.fill"] = "M2.5,3 H10 V6 H2.5 Z M6.2,6 V14.5",
        ["shippingbox.fill"] = "M2,5 L8,2 L14,5 V11 L8,14 L2,11 Z M2,5 L8,8 L14,5 M8,8 V14",
        ["checkmark.diamond.fill"] = "M8,1.5 L14.5,8 L8,14.5 L1.5,8 Z M5,8 L7.2,10.2 L11,6.2",
        ["icloud.and.arrow.up.fill"] = "M4,12.5 A3,3 0 0 1 4.3,6.6 A4.2,4.2 0 0 1 12,6.8 A2.7,2.7 0 0 1 12,12.5 Z M8,14.5 V8 M5.8,10.2 L8,8 L10.2,10.2",
        ["server.rack"] = "M2,2.5 H14 V6.5 H2 Z M2,9.5 H14 V13.5 H2 Z M4,4.5 H5.5 M4,11.5 H5.5 M9,4.5 H12 M9,11.5 H12",
        ["arrow.down.circle.fill"] = "M8,1.5 A6.5,6.5 0 1 0 8,14.5 A6.5,6.5 0 1 0 8,1.5 Z M8,4.5 V10.5 M5.5,8 L8,10.5 L10.5,8",
        ["doc.on.doc.fill"] = "M5.5,1.5 H12.5 V11.5 H5.5 Z M3.5,4.5 V14.5 H10.5",
        ["film.stack"] = "M2.5,5.5 H13.5 V14 H2.5 Z M4.5,3 H11.5 M6.5,8 L10,10.25 L6.5,12.5 Z",
        ["archivebox.fill"] = "M1.5,2.5 H14.5 V5.5 H1.5 Z M2.5,5.5 V14 H13.5 V5.5 M6,8.5 H10",
        ["arrow.triangle.branch"] = "M4,1.5 V14.5 M12,3.5 V6 C12,9 4,7.5 4,10.5",
        ["arrow.left.arrow.right"] = "M1.5,8 H14.5 M4.5,5 L1.5,8 L4.5,11 M11.5,5 L14.5,8 L11.5,11",
        ["photo.stack.fill"] = "M4.5,2.5 H13.5 V11.5 H4.5 Z M2.5,4.5 V13.5 H11.5 M7,9.5 L9.5,6.5 L13.5,11",
        ["waveform"] = "M2,7.5 V8.5 M4,6 V10 M6,3.5 V12.5 M8,6.5 V9.5 M10,2.5 V13.5 M12,5 V11 M14,7 V9",
        ["cube.transparent.fill"] = "M8,1.5 L14,4.5 V11.5 L8,14.5 L2,11.5 V4.5 Z M2,4.5 L8,7.5 L14,4.5 M8,7.5 V14.5",
        ["airplane"] = "M8,1.5 L9.2,6 L14.5,9 V10.5 L9.2,9.3 L9,12.5 L10.5,13.8 V14.8 L8,14.1 L5.5,14.8 V13.8 L7,12.5 L6.8,9.3 L1.5,10.5 V9 L6.8,6 Z",
    };

    /// The glyph for a name, or the fallback when the name is not in the map.
    public static string Get(string name) => Map.TryGetValue(name, out var code) ? code : Fallback;

    /// The path data of a name the font does not draw (gauge, hourglass), or null.
    public static string? Vector(string name) => Paths.TryGetValue(name, out var data) ? data : null;
}
