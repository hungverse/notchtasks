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
        "check", "sparkles", "log",
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
    public void UnknownName_DrawsTheFallback()
    {
        Assert.Equal(ModuleGlyphs.Fallback, ModuleGlyphs.Get("no-such-glyph"));
        Assert.Equal("", ModuleGlyphs.Fallback);
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
    public void Vpn_IsTheVpnGlyph_NotTheLock()
    {
        // Segoe Fluent Icons: VPN is E705; E72E is Lock.
        Assert.Equal("", ModuleGlyphs.Get("vpn"));
    }

    [Fact]
    public void Hand_IsAFontGlyph_NotTheMissingOne()
    {
        // E6BA is not in Segoe Fluent Icons (it drew as a box); F271 is the pointing hand.
        Assert.Equal("", ModuleGlyphs.Get("hand"));
    }

    [Fact]
    public void Thermometer_IsTheThermometerGlyph()
    {
        // Segoe Fluent Icons: the thermometer is E9CA (checked against the font's glyph shapes; E9CE is a question mark).
        Assert.Equal("", ModuleGlyphs.Get("thermometer"));
    }

    [Fact]
    public void Network_IsTheNetworkGlyph()
    {
        // Segoe Fluent Icons: Network is E968.
        Assert.Equal("", ModuleGlyphs.Get("network"));
    }

    [Fact]
    public void Check_Sparkles_Log_AreTheFontGlyphs()
    {
        // Segoe Fluent Icons: CheckMark is E73E, Sparkle is E794, the text-lines glyph for a log is E8E2.
        Assert.Equal("", ModuleGlyphs.Get("check"));
        Assert.Equal("", ModuleGlyphs.Get("sparkles"));
        Assert.Equal("", ModuleGlyphs.Get("log"));
    }

    [Fact]
    public void Gauge_And_Hourglass_HaveVectorGeometry()
    {
        // The font has no gauge or hourglass glyph that was found in SegoeIcons.ttf (E916 is a stopwatch), so both are drawn.
        Assert.NotNull(ModuleGlyphs.Vector("gauge"));
        Assert.NotNull(ModuleGlyphs.Vector("hourglass"));
        Assert.Null(ModuleGlyphs.Vector("check"));
    }
}
