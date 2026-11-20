using System.Windows.Controls;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The device toast art: with reduced motion the final values show at once, nothing animates, and no ripple is drawn.
public sealed class DeviceArtTests
{
    private static ToastView Show(bool connected)
    {
        var view = new ToastView();
        var tint = connected ? "#0A84FF" : "#8E8E93";
        view.Show(new Toast("headphones", tint, "Keyboard connected", "Ready", Event: null,
            Art: new DeviceArt("keyboard", connected), Module: "devices"), "island", null, "reduced");
        return view;
    }

    [Fact]
    public void ToastArt_DeviceArt_Reduced_NoAnimation()
    {
        Sta.Run(() =>
        {
            var view = Show(connected: true);
            var art = Assert.IsType<DeviceArtView>(Fixture.Named<ContentControl>(view, "TopGlyph").Content);

            Assert.Equal(1.0, art.Progress);
            Assert.Equal(0.0, art.Float);
            Assert.False(art.HasAnimatedProperties);
            Assert.Single(art.Children);  // the glyph only: no ripple behind it
            Assert.Equal(1.0, art.Children[0].Opacity);
        });
    }

    [Fact]
    public void ToastArt_DeviceArt_Disconnected_FinalValuesWhenReduced()
    {
        Sta.Run(() =>
        {
            var view = Show(connected: false);
            var art = Assert.IsType<DeviceArtView>(Fixture.Named<ContentControl>(view, "TopGlyph").Content);

            Assert.False(art.HasAnimatedProperties);
            Assert.Equal(0.45, art.Children[0].Opacity, 2);
        });
    }
}
