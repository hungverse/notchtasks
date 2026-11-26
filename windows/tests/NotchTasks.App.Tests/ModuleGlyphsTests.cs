using System.Text.Json;
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
        "check", "sparkles", "log", "media", "previous", "next", "play", "pause", "gauge", "hourglass",
    };

    /// The icon each name draws: the Fluent System Icons key whose codepoint the glyph is (20 px, Regular unless Filled is named).
    private static readonly IReadOnlyDictionary<string, string> KeyOf = new Dictionary<string, string>
    {
        ["hand"] = "ic_fluent_hand_right_20_regular",
        ["question"] = "ic_fluent_chat_help_20_regular",
        ["timer"] = "ic_fluent_timer_20_regular",
        ["download"] = "ic_fluent_arrow_download_20_regular",
        ["drive"] = "ic_fluent_storage_20_regular",
        ["phone"] = "ic_fluent_phone_20_regular",
        ["video"] = "ic_fluent_video_20_regular",
        ["mic"] = "ic_fluent_mic_20_regular",
        ["micoff"] = "ic_fluent_mic_off_20_regular",
        ["phonedown"] = "ic_fluent_call_end_20_regular",
        ["camera"] = "ic_fluent_camera_20_regular",
        ["wifi"] = "ic_fluent_wifi_4_20_regular",
        ["wifioff"] = "ic_fluent_wifi_off_20_regular",
        ["network"] = "ic_fluent_globe_20_regular",
        ["vpn"] = "ic_fluent_shield_lock_20_regular",
        ["vpnoff"] = "ic_fluent_lock_open_20_regular",
        ["battery"] = "ic_fluent_battery_5_20_regular",
        ["batterylow"] = "ic_fluent_battery_1_20_regular",
        ["bolt"] = "ic_fluent_flash_20_regular",
        ["plug"] = "ic_fluent_power_20_regular",
        ["warning"] = "ic_fluent_warning_20_regular",
        ["thermometer"] = "ic_fluent_temperature_20_regular",
        ["check"] = "ic_fluent_checkmark_20_regular",
        ["sparkles"] = "ic_fluent_sparkle_20_regular",
        ["log"] = "ic_fluent_text_bullet_list_ltr_20_regular",
        ["media"] = "ic_fluent_music_note_2_20_regular",
        ["previous"] = "ic_fluent_previous_20_regular",
        ["next"] = "ic_fluent_next_20_regular",
        ["play"] = "ic_fluent_play_20_filled",
        ["pause"] = "ic_fluent_pause_20_filled",
        ["gauge"] = "ic_fluent_gauge_20_regular",
        ["hourglass"] = "ic_fluent_hourglass_20_regular",
    };

    /// Pairs of names that share a code point on purpose. Empty: every name has its own glyph.
    private static readonly (string A, string B)[] IntendedSharedGlyphs = Array.Empty<(string, string)>();

    [Fact]
    public void EveryToastAndChipGlyphName_HasAGlyph()
    {
        foreach (var name in Names)
        {
            var glyph = ModuleGlyphs.Get(name);
            Assert.False(string.IsNullOrEmpty(glyph), name);
            Assert.NotEqual(ModuleGlyphs.Fallback, glyph);
        }
    }

    [Fact]
    public void EveryGlyphName_ResolvesToItsKeysCodepointInTheBundledJson()
    {
        var regular = BundledJson("FluentSystemIcons-Regular.json");
        var filled = BundledJson("FluentSystemIcons-Filled.json");
        foreach (var name in Names)
        {
            var key = KeyOf[name];
            var table = key.EndsWith("_filled", StringComparison.Ordinal) ? filled : regular;
            Assert.True(table.TryGetValue(key, out var codepoint), $"{name}: {key} is not in the bundled json");
            Assert.Equal(char.ConvertFromUtf32(codepoint), ModuleGlyphs.Get(name));
        }
    }

    [Fact]
    public void EveryMappedName_IsInTheBundledJson()
    {
        var regular = BundledJson("FluentSystemIcons-Regular.json");
        var filled = BundledJson("FluentSystemIcons-Filled.json");
        var values = regular.Values.Concat(filled.Values).ToHashSet();
        foreach (var name in ModuleGlyphs.Names)
        {
            var glyph = ModuleGlyphs.Get(name);
            Assert.True(values.Contains(char.ConvertToUtf32(glyph, 0)), $"{name} draws a code the fonts do not have");
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

    [Fact]
    public void UnknownName_DrawsTheFallback()
    {
        Assert.Equal(ModuleGlyphs.Fallback, ModuleGlyphs.Get("no-such-glyph"));
        Assert.Equal(ModuleGlyphs.Fallback, char.ConvertFromUtf32(BundledJson("FluentSystemIcons-Regular.json")["ic_fluent_more_horizontal_20_regular"]));
    }

    [Fact]
    public void NoTwoNamesShareACodePoint_UnlessIntended()
    {
        foreach (var (a, b) in Names.SelectMany((a, i) => Names.Skip(i + 1).Select(b => (a, b))))
        {
            var shared = ModuleGlyphs.Get(a) == ModuleGlyphs.Get(b);
            var intended = IntendedSharedGlyphs.Any(p => (p.A == a && p.B == b) || (p.A == b && p.B == a));
            Assert.False(shared && !intended, $"{a} and {b} share a glyph");
        }
    }

    [Fact]
    public void PlayAndPause_AreTheFilledFont_EveryOtherGlyphIsRegular()
    {
        Assert.Same(FluentIcons.Filled, ModuleGlyphs.FamilyOf("play"));
        Assert.Same(FluentIcons.Filled, ModuleGlyphs.FamilyOf("pause"));
        Assert.Same(FluentIcons.Regular, ModuleGlyphs.FamilyOf("vpn"));
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
        var regular = BundledJson("FluentSystemIcons-Regular.json");
        Assert.Equal(char.ConvertFromUtf32(regular["ic_fluent_star_20_regular"]), ModuleGlyphs.Legacy("E734"));
        Assert.Equal(char.ConvertFromUtf32(regular["ic_fluent_heart_20_regular"]), ModuleGlyphs.Legacy("E735"));
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

    private static Dictionary<string, int> BundledJson(string file)
    {
        using var stream = typeof(ModuleGlyphs).Assembly.GetManifestResourceStream(file)
            ?? throw new InvalidOperationException($"{file} is not embedded in the app");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
    }
}
