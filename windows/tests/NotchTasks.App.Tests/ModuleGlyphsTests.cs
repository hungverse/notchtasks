using System.IO;
using System.Windows.Media;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class ModuleGlyphsTests
{
    /// Every glyph name a toast, a chip or a card uses (Tasks 4 to 10, AskCard, ChipView, the timer and call cards).
    private static readonly string[] Names =
    {
        "hand", "question", "timer", "download", "drive", "phone", "phonedown", "video", "mic", "micoff", "camera",
        "wifi", "wifioff", "network", "vpn", "vpnoff", "battery", "batterylow", "bolt", "plug", "warning", "thermometer",
        "check", "sparkles", "log", "media", "previous", "next", "play", "pause", "gauge", "hourglass", "calendar", "sun",
    };

    /// The icon each name draws: the key of the bundled font it is (ic_fluent_<name>_20_regular, or _filled where the Mac draws
    /// the symbol filled).
    private static readonly IReadOnlyDictionary<string, string> KeyOf = new Dictionary<string, string>
    {
        ["hand"] = "ic_fluent_hand_right_20_filled",
        ["question"] = "ic_fluent_chat_help_20_filled",
        ["timer"] = "ic_fluent_timer_20_regular",
        ["download"] = "ic_fluent_arrow_circle_down_20_filled",
        ["drive"] = "ic_fluent_storage_20_regular",
        ["phone"] = "ic_fluent_phone_20_filled",
        ["video"] = "ic_fluent_video_20_filled",
        ["mic"] = "ic_fluent_mic_20_filled",
        ["micoff"] = "ic_fluent_mic_off_20_filled",
        ["phonedown"] = "ic_fluent_call_end_20_filled",
        ["camera"] = "ic_fluent_camera_20_filled",
        ["wifi"] = "ic_fluent_wifi_4_20_regular",
        ["wifioff"] = "ic_fluent_wifi_off_20_regular",
        ["network"] = "ic_fluent_wifi_4_20_regular",  // the Network module is the Fluent wifi icon (the same glyph as the wifi chip)
        ["vpn"] = "ic_fluent_shield_lock_20_filled",
        ["vpnoff"] = "ic_fluent_lock_open_20_filled",
        ["battery"] = "ic_fluent_battery_5_20_regular",
        ["batterylow"] = "ic_fluent_battery_1_20_regular",
        ["bolt"] = "ic_fluent_flash_20_filled",
        ["plug"] = "ic_fluent_plug_connected_20_regular",
        ["warning"] = "ic_fluent_warning_20_filled",
        ["thermometer"] = "ic_fluent_temperature_20_regular",
        ["check"] = "ic_fluent_checkmark_circle_20_filled",
        ["sparkles"] = "ic_fluent_sparkle_20_regular",
        ["log"] = "ic_fluent_text_bullet_list_ltr_20_regular",
        ["media"] = "ic_fluent_music_note_2_20_regular",
        ["previous"] = "ic_fluent_previous_20_regular",
        ["next"] = "ic_fluent_next_20_regular",
        ["play"] = "ic_fluent_play_20_filled",
        ["pause"] = "ic_fluent_pause_20_filled",
        ["gauge"] = "ic_fluent_gauge_20_regular",
        ["hourglass"] = "ic_fluent_hourglass_20_regular",
        ["calendar"] = "ic_fluent_calendar_ltr_20_regular",
        ["sun"] = "ic_fluent_weather_sunny_20_filled",
    };

    /// Pairs of names that share a code point on purpose: the Network module and the wifi chip draw the same wifi icon.
    private static readonly (string A, string B)[] IntendedSharedGlyphs = { ("wifi", "network") };

    [Fact]
    public void EveryToastAndChipGlyphName_HasAGlyph()
    {
        foreach (var name in Names)
        {
            var glyph = ModuleGlyphs.Get(name).Glyph;
            Assert.False(string.IsNullOrEmpty(glyph), name);
            Assert.NotEqual(ModuleGlyphs.Fallback, glyph);
        }
    }

    [Fact]
    public void EveryGlyphName_ResolvesToItsKeysCodepointInItsFamily()
    {
        foreach (var name in Names)
        {
            var key = KeyOf[name];
            var filled = key.EndsWith("_filled", StringComparison.Ordinal);
            var (family, glyph) = ModuleGlyphs.Get(name);
            Assert.Same(filled ? FluentIcons.Filled : FluentIcons.Regular, family);
            var table = filled ? IconFonts.Filled : IconFonts.Regular;
            Assert.True(table.TryGetValue(key, out var codepoint), $"{name}: {key} is not in its font");
            Assert.Equal(IconFonts.GlyphOf(codepoint), glyph);
        }
    }

    [Fact]
    public void EveryMappedName_IsInTheFontItsFilledFlagNames()
    {
        foreach (var name in ModuleGlyphs.Names)
        {
            var (family, glyph) = ModuleGlyphs.Get(name);
            var codepoint = char.ConvertToUtf32(glyph, 0);
            var table = Equals(family, FluentIcons.Filled) ? IconFonts.Filled : IconFonts.Regular;
            Assert.Contains(codepoint, table.Values);
        }
    }

    [Fact]
    public void UnknownName_DrawsTheFallbackInRegular()
    {
        var (family, glyph) = ModuleGlyphs.Get("no-such-glyph");
        Assert.Equal(ModuleGlyphs.Fallback, glyph);
        Assert.Same(FluentIcons.Regular, family);
        Assert.Equal(ModuleGlyphs.Fallback, IconFonts.GlyphOf(IconFonts.Regular["ic_fluent_more_horizontal_20_regular"]));
    }

    [Fact]
    public void NoTwoNamesShareACodePoint_UnlessIntended()
    {
        foreach (var (a, b) in Names.SelectMany((a, i) => Names.Skip(i + 1).Select(b => (a, b))))
        {
            var shared = ModuleGlyphs.Get(a).Glyph == ModuleGlyphs.Get(b).Glyph;
            var intended = IntendedSharedGlyphs.Any(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));
            Assert.False(shared && !intended, $"{a} and {b} share a glyph");
        }
    }

    [Fact]
    public void FilledFlag_FollowsTheMacsFilledSymbols()
    {
        // The Mac's .fill symbols are filled; the outline ones are not.
        Assert.Same(FluentIcons.Filled, ModuleGlyphs.Get("play").Family);
        Assert.Same(FluentIcons.Filled, ModuleGlyphs.Get("phone").Family);
        Assert.Same(FluentIcons.Filled, ModuleGlyphs.Get("check").Family);
        Assert.Same(FluentIcons.Regular, ModuleGlyphs.Get("wifi").Family);
        Assert.Same(FluentIcons.Regular, ModuleGlyphs.Get("iphone").Family);
    }

    [Fact]
    public void Gauge_And_Hourglass_AreFontGlyphs_NotVectors()
    {
        Assert.Null(ModuleGlyphs.Vector("gauge"));
        Assert.Null(ModuleGlyphs.Vector("hourglass"));
        Assert.Null(ModuleGlyphs.Vector("check"));
        Assert.NotNull(ModuleGlyphs.Vector("hammer.fill"));
    }

    [Fact]
    public void BadgeSymbolsStoredAsSegoeCodes_DrawTheirFluentIcon()
    {
        Assert.Equal(IconFonts.GlyphOf(IconFonts.Regular["ic_fluent_star_20_regular"]), ModuleGlyphs.Legacy("E734"));
        Assert.Equal(IconFonts.GlyphOf(IconFonts.Regular["ic_fluent_heart_20_regular"]), ModuleGlyphs.Legacy("E735"));
        Assert.Null(ModuleGlyphs.Legacy("E6BA"));
    }

    [Fact]
    public void EveryBadgeSymbolTheListOffers_HasAFluentGlyph()
    {
        foreach (var code in BadgeEdits.SymbolCodes)
        {
            Assert.NotNull(ModuleGlyphs.Legacy(code));
        }
    }

    [Fact]
    public void BundledFonts_AreTheResourcesTheFluentPackUrisName()
    {
        // The fonts load through pack://application:,,,/NotchTasks;component/Resources/Fonts/. The test host cannot load them
        // (its entry assembly is not the app), so this checks the app's compiled resource table names both font files.
        using var stream = typeof(FluentIcons).Assembly.GetManifestResourceStream("NotchTasks.g.resources")
            ?? throw new InvalidOperationException("NotchTasks.g.resources is not in the app");
        using var buffer = new System.IO.MemoryStream();
        stream.CopyTo(buffer);
        var table = System.Text.Encoding.ASCII.GetString(buffer.ToArray());
        Assert.Contains("resources/fonts/fluentsystemicons-regular.ttf", table);
        Assert.Contains("resources/fonts/fluentsystemicons-filled.ttf", table);
    }
}
