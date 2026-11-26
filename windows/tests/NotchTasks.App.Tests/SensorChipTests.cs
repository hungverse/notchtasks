using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The call and sensor chips: the call's trail is its time so far, red while the mic is muted; the sensor's lead has a glyph
/// for the mic and one for the camera when both are on.
public sealed class SensorChipTests
{
    private static readonly IReadOnlyDictionary<string, string> NoColours = new Dictionary<string, string>();
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    [Fact]
    public void CallTrail_IsRedWhileMuted_AndGreenOtherwise()
    {
        Sta.Run(() =>
        {
            var now = new DateTimeOffset(2026, 11, 19, 10, 5, 0, TimeSpan.Zero);
            var muted = new Chip(ChipKind.Call, "calls", "call")
            {
                Call = new CallInfo("zoom.exe", "Zoom", now.AddSeconds(-65), false, true),
            };
            var live = new Chip(ChipKind.Call, "calls", "call")
            {
                Call = new CallInfo("zoom.exe", "Zoom", now.AddSeconds(-65), false, false),
            };

            var mutedTrail = Assert.IsType<TextBlock>(ChipView.Trail(muted, 11, null, NoColours, now));
            Assert.Equal("1:05", mutedTrail.Text);
            Assert.Equal(Red, Assert.IsType<SolidColorBrush>(mutedTrail.Foreground).Color);

            var liveTrail = Assert.IsType<TextBlock>(ChipView.Trail(live, 11, null, NoColours, now));
            Assert.Equal("1:05", liveTrail.Text);
            Assert.Equal(Green, Assert.IsType<SolidColorBrush>(liveTrail.Foreground).Color);
        });
    }

    [Fact]
    public void CallLead_IsMutedMic_WhenMuted()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var chip = new Chip(ChipKind.Call, "calls", "call")
            {
                Call = new CallInfo("zoom.exe", "Zoom", now, false, true),
            };

            var lead = Assert.IsType<FluentGlyph>(ChipView.Lead(chip, 18, null, NoColours));
            Assert.Equal(ModuleGlyphs.Get("micoff").Glyph, lead.Code);
        });
    }

    [Fact]
    public void SensorLead_ShowsMicAndCamera_WhenBothAreOn()
    {
        Sta.Run(() =>
        {
            var both = new Chip(ChipKind.Sensor, "privacy", "sensor")
            {
                Sensors = new SensorState(new[] { "spotify.exe" }, true, false),
            };
            var micOnly = new Chip(ChipKind.Sensor, "privacy", "sensor")
            {
                Sensors = new SensorState(new[] { "spotify.exe" }, false, false),
            };

            var row = Assert.IsType<StackPanel>(ChipView.Lead(both, 18, null, NoColours));
            Assert.Equal(2, row.Children.Count);
            var single = Assert.IsType<StackPanel>(ChipView.Lead(micOnly, 18, null, NoColours));
            var glyph = Assert.IsType<FluentGlyph>(Assert.Single(single.Children));
            Assert.Equal(ModuleGlyphs.Get("mic").Glyph, glyph.Code);
            Assert.Null(ChipView.Trail(both, 11, null, NoColours, DateTimeOffset.Now));  // a sensor has no trail
        });
    }

    [Fact]
    public void Glyphs_ForTheCallEnd_AndTheMute_AreNotTheFallback()
    {
        Assert.NotEqual(ModuleGlyphs.Fallback, ModuleGlyphs.Get("phonedown").Glyph);
        Assert.Equal(IconFonts.GlyphOf(IconFonts.Filled["ic_fluent_call_end_20_filled"]), ModuleGlyphs.Get("phonedown").Glyph);
        Assert.Equal(IconFonts.GlyphOf(IconFonts.Filled["ic_fluent_mic_off_20_filled"]), ModuleGlyphs.Get("micoff").Glyph);
    }
}
