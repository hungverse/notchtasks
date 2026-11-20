using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// A publish that changes only a media chip's position keeps its panel (and the working bars in it): the panel is rebuilt only when
/// what it shows changes (its artwork, its icon, its tint, or its sides).
public sealed class MediaChipSignatureTests
{
    private static Chip Media(long nowMs, double position, string? artKey = "spotify.exe|Song|Artist") => new(ChipKind.Media, "media", "media")
    {
        Media = new NowPlaying("spotify.exe", "Spotify", null, "Song", "Artist", true, position, 200, nowMs, true, true, true, true, artKey),
    };

    [Fact]
    public void PositionOnlyChange_KeepsThePanelAndItsBars()
    {
        Sta.Run(() =>
        {
            var (left, right, window) = Wings();
            try
            {
                var now = DateTimeOffset.Now;
                var nowMs = now.ToUnixTimeMilliseconds();
                Fill(left, right, [Media(nowMs, 10)], now);
                var first = Assert.Single(right.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");

                Fill(left, right, [Media(nowMs + 2000, 12)], now.AddSeconds(2));
                var second = Assert.Single(right.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");

                Assert.Same(first, second);
                Assert.Same(((StackPanel)first).Children[0], ((StackPanel)second).Children[0]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// The artwork arriving changes what the lead shows (the glyph becomes the picture): the panel is updated to it, in place.
    [Fact]
    public void ArtworkArriving_UpdatesTheLead()
    {
        Sta.Run(() =>
        {
            var (left, right, window) = Wings();
            try
            {
                var now = DateTimeOffset.Now;
                var nowMs = now.ToUnixTimeMilliseconds();
                Fill(left, right, [Media(nowMs, 10)], now);
                var first = Assert.Single(right.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");

                // The same chip with its artwork cached: the lead on the left wing becomes the picture.
                ChipView.Fill(left, right, [Media(nowMs, 10)], new Dictionary<string, string>(StringComparer.Ordinal),
                    new Dictionary<string, string>(StringComparer.Ordinal), null, now, null, animationsOn: false,
                    art: _ => new DrawingImage());
                var leadWing = Assert.Single(left.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");

                Assert.IsType<Image>(((StackPanel)leadWing).Children[0]);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (StackPanel Left, StackPanel Right, Window Window) Wings()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(left);
        host.Children.Add(right);
        var window = new Window { Content = host, Width = 400, Height = 60 };
        Fixture.ShowOffScreen(window);
        return (left, right, window);
    }

    private static void Fill(StackPanel left, StackPanel right, Chip[] chips, DateTimeOffset now) =>
        ChipView.Fill(left, right, chips, new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal), null, now, null, animationsOn: false, art: _ => null);
}
