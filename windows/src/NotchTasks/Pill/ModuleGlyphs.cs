using System.Windows.Media;

namespace NotchTasks.Pill;

/// The Fluent System Icons glyph of each glyph name a toast or a chip carries (Toast.Glyph, the chip's lead). A name the map
/// does not know draws the fallback, never nothing. Each entry names its icon in a comment and says whether it is the Filled
/// twin (the Mac draws that symbol filled); the tests check every code against the bundled JSON of its family.
public static class ModuleGlyphs
{
    /// One map entry: the codepoint, and whether it is in the Filled font (true) or the Regular font (false).
    private readonly record struct Icon(string Code, bool Filled = false);

    /// What an unknown name draws (the Control Panel's "more" glyph, more_horizontal).
    public const string Fallback = "";

    private static readonly IReadOnlyDictionary<string, Icon> Map = new Dictionary<string, Icon>(StringComparer.Ordinal)
    {
        ["hand"] = new("", true),                        // hand_right filled: an agent's approval (Mac hand.raised.fill)
        ["question"] = new("", true),                    // chat_help filled: an agent's question (Mac questionmark.bubble.fill)
        ["timer"] = new(""),                             // timer
        ["download"] = new("", true),                    // arrow_circle_down filled (Mac arrow.down.circle.fill)
        ["drive"] = new(""),                             // storage (no Mac symbol)
        ["phone"] = new("", true),                       // phone filled (Mac phone.fill)
        ["video"] = new("", true),                       // video filled (Mac video.fill)
        ["mic"] = new("", true),                         // mic filled (Mac mic.fill)
        ["micoff"] = new("", true),                      // mic_off filled: the mic is muted (Mac mic.slash.fill)
        ["phonedown"] = new("", true),                   // call_end filled: the call ended (Mac phone.down.fill)
        ["camera"] = new("", true),                      // camera filled: the camera mirror (Mac video.fill)
        ["wifi"] = new(""),                              // wifi_4 (Mac wifi)
        ["network"] = new(""),                           // globe (Mac network)
        ["vpn"] = new("", true),                         // shield_lock filled (Mac lock.shield.fill)
        ["wifioff"] = new(""),                           // wifi_off: network disconnected (Mac wifi.slash)
        ["vpnoff"] = new("", true),                      // lock_open filled: VPN off (Mac lock.open.fill)
        ["battery"] = new(""),                           // battery_5 (Mac battery.100percent)
        ["bolt"] = new("", true),                        // flash filled: charging (Mac bolt.fill)
        ["plug"] = new(""),                              // plug_connected: unplugged (Mac powerplug.portrait)
        ["batterylow"] = new(""),                        // battery_1: low battery
        ["battery.25percent"] = new(""),                 // battery_1: a device's low battery (Mac battery.25percent)
        ["warning"] = new("", true),                     // warning filled (Mac exclamationmark.triangle.fill)
        ["thermometer"] = new(""),                       // temperature (Mac thermometer.medium)
        ["check"] = new("", true),                       // checkmark_circle filled: a finished job (Mac checkmark.circle.fill)
        ["sparkles"] = new(""),                          // sparkle (Mac sparkles): an agent's icon
        ["log"] = new(""),                               // text_bullet_list_ltr: a job's log (Mac text.alignleft)
        ["media"] = new(""),                             // music_note_2 (Mac music.note)
        ["previous"] = new(""),                          // previous
        ["next"] = new(""),                              // next
        ["play"] = new("", true),                        // play filled (Mac play.fill)
        ["pause"] = new("", true),                       // pause filled (Mac pause.fill)
        // Devices (the Mac's device symbols). The AirPods use the headphones glyph, as the font has no AirPods.
        ["headphones"] = new(""),                        // headphones
        ["airpods"] = new(""),
        ["airpodspro"] = new(""),
        ["airpodsmax"] = new(""),
        ["keyboard"] = new(""),                          // keyboard
        ["computermouse"] = new(""),                     // cursor: the mouse (Fluent has no mouse)
        ["rectangle.and.hand.point.up.left"] = new(""),  // hand_left: touch pointer
        ["hifispeaker"] = new(""),                       // speaker_2
        ["gamecontroller"] = new(""),                    // games
        ["iphone"] = new(""),                            // phone (Mac iphone)
        ["externaldrive"] = new(""),                     // hard_drive (Mac externaldrive)
        ["gauge"] = new(""),                             // gauge (Mac gauge.with.dots.needle.67percent): a limit below 100 %
        ["hourglass"] = new(""),                         // hourglass (Mac hourglass): a limit at 100 %
        ["calendar"] = new(""),                    // calendar_ltr (Mac calendar): the calendar chip and module
        ["sun"] = new("", true),                   // weather_sunny filled (Mac sun.max.fill): an all-day event
    };

    /// The codes the badge symbols were stored as before the Fluent icons (a "seg:" + Segoe code in settings). They still
    /// draw, as the Fluent icon each one was: a stored badge keeps its look. Only the codes BadgeEdits offers are here.
    private static readonly IReadOnlyDictionary<string, string> LegacyBadgeSymbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["E734"] = "",  // star
        ["E735"] = "",  // heart
        ["E006"] = "",  // flag
        ["E80F"] = "",  // home
        ["E713"] = "",  // settings
        ["E721"] = "",  // search
        ["E710"] = "",  // add
        ["E74D"] = "",  // delete
        ["E70F"] = "",  // edit
        ["E72C"] = "",  // arrow_sync
        ["E72E"] = "",  // lock_closed
        ["E77B"] = "",  // person
        ["E722"] = "",  // camera
        ["E767"] = "",  // speaker_2
        ["E7E8"] = "",  // power
        ["E774"] = "",  // globe
        ["E718"] = "",  // pin
        ["E7C1"] = "",  // tag
        ["E895"] = "",  // emoji
    };

    /// Vector stand-ins for the Mac symbols the font does not draw here: stroked paths in a 16 by 16 box, in the toast's tint.
    /// Null for every other name.
    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
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
        // the Devices module's symbols the font lacks: a watch (Mac applewatch), a radio dot with its waves, and a USB plug (Mac cable.connector)
        ["applewatch"] = "M5,4.5 H11 V11.5 H5 Z M6.5,4.5 L7,2 H9 L9.5,4.5 M6.5,11.5 L7,14 H9 L9.5,11.5",
        ["dot.radiowaves.left.and.right"] = "M6.9,8 A1.1,1.1 0 1 0 9.1,8 A1.1,1.1 0 1 0 6.9,8 Z M4.2,5.2 A4,4 0 0 0 4.2,10.8 M11.8,5.2 A4,4 0 0 1 11.8,10.8 M2.2,2.8 A7,7 0 0 0 2.2,13.2 M13.8,2.8 A7,7 0 0 1 13.8,13.2",
        ["cable.connector"] = "M6,1.5 V5 M10,1.5 V5 M4.5,5 H11.5 V8 A3.5,3.5 0 0 1 4.5,8 Z M8,11.5 V14.5",
        // the camera mirror's "no picture" mark (Mac video.slash.fill): a camera body with its lens, crossed by a slash
        ["video.slash"] = "M1.8,4 H9.8 V12 H1.8 Z M9.8,7 L14.2,4.5 V11.5 L9.8,9 M1.5,1.5 L14.5,14.5",
    };

    /// The glyph of a name and the family it is drawn in (Filled for a Mac symbol that is filled), or the fallback in Regular
    /// when the name is not in the map.
    public static (FontFamily Family, string Glyph) Get(string name) =>
        Map.TryGetValue(name, out var icon)
            ? (icon.Filled ? FluentIcons.Filled : FluentIcons.Regular, icon.Code)
            : (FluentIcons.Regular, Fallback);

    /// The Fluent glyph of a badge symbol stored as a Segoe code (the "seg:" symbols), or null when the code is not one the
    /// badge list offers.
    public static string? Legacy(string segoeCode) => LegacyBadgeSymbols.TryGetValue(segoeCode, out var code) ? code : null;

    /// The path data of a name the font does not draw (the Mac symbols above), or null.
    public static string? Vector(string name) => Paths.TryGetValue(name, out var data) ? data : null;

    /// Every glyph name the map knows, for the tests.
    public static IEnumerable<string> Names => Map.Keys;

    /// Whether a name's glyph is in the Filled font, for the tests that check each code against its family.
    public static bool IsFilled(string name) => Map.TryGetValue(name, out var icon) && icon.Filled;
}
