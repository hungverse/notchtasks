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
        ["wifi"] = new("\uF8AC"),                       // the full-strength Wi-Fi in the shipped font (U+F8AC; its JSON name is wifi_1, the TTF is ordered the other way)
        ["network"] = new("\uF8AC"),                           // the Network module
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
        // The volume and brightness bar (LevelsModule.SymbolName): the Mac's speaker and sun symbols, filled, by level.
        ["volume.off"] = new("", true),      // speaker_off filled (Mac speaker.slash.fill): muted or at zero
        ["volume.low"] = new("", true),      // speaker_0 filled: below a third
        ["volume.mid"] = new("", true),      // speaker_1 filled: below two thirds
        ["volume.high"] = new("", true),     // speaker_2 filled (Mac speaker.wave.3.fill): the loudest step
        ["brightness.low"] = new("", true),  // brightness_low filled (Mac sun.min.fill): below half
        ["brightness.high"] = new("", true), // brightness_high filled (Mac sun.max.fill): half and up
        ["sun"] = new("", true),                   // weather_sunny filled (Mac sun.max.fill): an all-day event
        // The Mac symbols the Build and CI, device, toast and camera rows draw: the Fluent glyph each one is (Mac name, closest icon)
        ["seal"] = new(""),                                // badge (Mac checkmark.seal)
        ["octagon"] = new(""),                             // stop (Mac octagon)
        ["xmark"] = new(""),                               // dismiss (Mac xmark)
        ["hammer.fill"] = new("", true),                   // wrench filled (Mac hammer.fill): the Build and CI tool
        ["shippingbox.fill"] = new("", true),              // box multiple filled (Mac shippingbox.fill)
        ["checkmark.diamond.fill"] = new("", true),        // shield checkmark filled (Mac checkmark.diamond.fill)
        ["icloud.and.arrow.up.fill"] = new("", true),      // cloud arrow up filled (Mac icloud.and.arrow.up.fill)
        ["server.rack"] = new(""),                         // server (Mac server.rack)
        ["arrow.down.circle.fill"] = new("", true),        // arrow circle down filled (Mac arrow.down.circle.fill)
        ["doc.on.doc.fill"] = new("", true),               // copy filled (Mac doc.on.doc.fill)
        ["film.stack"] = new(""),                          // video clip multiple (Mac film.stack)
        ["archivebox.fill"] = new("", true),               // archive filled (Mac archivebox.fill)
        ["arrow.triangle.branch"] = new(""),               // branch (Mac arrow.triangle.branch)
        ["arrow.left.arrow.right"] = new(""),              // arrow swap (Mac arrow.left.arrow.right)
        ["photo.stack.fill"] = new("", true),              // image multiple filled (Mac photo.stack.fill)
        ["waveform"] = new(""),                            // data bar vertical (Mac waveform)
        ["cube.transparent.fill"] = new("", true),         // cube filled (Mac cube.transparent.fill)
        ["airplane"] = new(""),                            // airplane (Mac airplane)
        ["applewatch"] = new(""),                          // smartwatch (Mac applewatch)
        ["dot.radiowaves.left.and.right"] = new(""),       // radio button (Mac dot.radiowaves.left.and.right)
        ["cable.connector"] = new(""),                     // plug connected (Mac cable.connector)
        ["video.slash"] = new(""),                         // video off (Mac video.slash): the camera mirror's no-picture mark
        ["terminal.fill"] = new(""),                       // code (Mac terminal.fill): a watched process with no icon of its own
        ["star"] = new(""),                                // star (Mac star): the badge symbol a new badge starts with
        ["star.fill"] = new("", true),                     // star filled (Mac star.fill): the sync default of a badge
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

    /// The glyph of a name and the family it is drawn in (Filled for a Mac symbol that is filled), or the fallback in Regular
    /// when the name is not in the map.
    public static (FontFamily Family, string Glyph) Get(string name) =>
        Map.TryGetValue(name, out var icon)
            ? (icon.Filled ? FluentIcons.Filled : FluentIcons.Regular, icon.Code)
            : (FluentIcons.Regular, Fallback);

    /// The Fluent glyph of a badge symbol stored as a Segoe code (the "seg:" symbols), or null when the code is not one the
    /// badge list offers.
    /// Whether a name is in the glyph map (a Fluent glyph of its own).
    public static bool Has(string name) => Map.ContainsKey(name);

    public static string? Legacy(string segoeCode) => LegacyBadgeSymbols.TryGetValue(segoeCode, out var code) ? code : null;

    /// Every glyph name the map knows, for the tests.
    public static IEnumerable<string> Names => Map.Keys;

    /// Whether a name's glyph is in the Filled font, for the tests that check each code against its family.
    public static bool IsFilled(string name) => Map.TryGetValue(name, out var icon) && icon.Filled;
}
