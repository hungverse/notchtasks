using System.Windows.Controls;
using System.Windows.Shapes;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// Every glyph name a Core module puts on a toast draws something: a font glyph, or the drawn path of a name the font lacks.
public sealed class ToastGlyphTests
{
    /// The glyph names the Core modules give their toasts (Toast.Glyph): the agents' check, the limits' gauge and hourglass, the
    /// downloads, the disk, the network and VPN, the sensors, the battery, and the timer.
    private static readonly string[] CoreToastGlyphs =
    {
        "check", "download", "drive", "wifi", "network", "wifioff", "vpn", "vpnoff", "micoff", "phone", "phonedown", "mic",
        "camera", "bolt", "plug", "batterylow", "timer", "gauge", "hourglass",
    };

    [Fact]
    public void EveryCoreToastGlyph_ResolvesToAFontGlyphOrAVector_NeverTheFallback()
    {
        foreach (var name in CoreToastGlyphs)
        {
            var isVector = ModuleGlyphs.Vector(name) is not null;
            var isFont = ModuleGlyphs.Get(name) != ModuleGlyphs.Fallback;
            Assert.True(isVector || isFont, name);
        }
    }

    [Fact]
    public void LimitToast_DrawsItsGaugeAsAPath()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            view.Show(new Toast("gauge", "#FF9F0A", "Claude 95%"), "compact", null, "bouncy");
            Assert.IsType<Path>(Fixture.Named<ContentControl>(view, "CompactGlyph").Content);
        });
    }

    [Fact]
    public void FontToast_KeepsItsTextGlyph()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            view.Show(new Toast("check", "#30D158", "Done"), "island", null, "bouncy");
            var glyph = Assert.IsType<TextBlock>(Fixture.Named<ContentControl>(view, "TopGlyph").Content);
            Assert.Equal(ModuleGlyphs.Get("check"), glyph.Text);
        });
    }
}
