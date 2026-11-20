using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The Now Playing card and the closed pill's media chip: the buttons follow what the session can do, a missing title says
/// "Playing audio", the seek bar needs a duration and a position, and the chip's lead is the artwork, else the app icon.
[Collection("L10n")]
public sealed class NowPlayingCardTests
{
    private static MediaSessionFact Session(string? title = "Song", string? artist = "Artist", MediaStatus status = MediaStatus.Playing,
        bool canNext = true, bool canToggle = true, double? position = 30, double? duration = 200) => new(
        "spotify.exe", title, artist, status, position, duration, 0, true, canNext, true, canToggle, "key", IsCurrent: true);

    private static AppState NewState(FakeMediaReader reader)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Media = reader });
    }

    /// A state whose reader lists the given session from its first read, and the card after the first read has shown it.
    private static (AppState State, NowPlayingCard Card) CardFor(MediaSessionFact session)
    {
        var reader = new FakeMediaReader { Sessions = { session } };
        var state = NewState(reader);
        Fixture.PumpUntil(() => state.Media.Current is not null);
        var card = new NowPlayingCard();
        Assert.True(card.Update(state, null));
        return (state, card);
    }

    [Fact]
    public void Controls_AreTheMacsFilledGlyphs()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (state, card) = CardFor(Session(status: MediaStatus.Playing));

            // Playing shows the filled pause; previous and next are the filled double triangles, drawn as paths.
            Assert.Equal("", Assert.IsType<string>(card.PlayPauseButton.Content));
            Assert.IsType<System.Windows.Shapes.Path>(card.PreviousButton.Content);
            Assert.IsType<System.Windows.Shapes.Path>(card.NextButton.Content);
            Assert.Equal(30, card.PlayPauseButton.Width);
            state.Dispose();
        });
    }

    [Fact]
    public void Card_DisabledButtons_FollowCan()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (state, card) = CardFor(Session(canNext: false, canToggle: true));

            Assert.False(card.NextButton.IsEnabled);
            Assert.Equal(0.35, card.NextButton.Opacity, 2);
            Assert.True(card.PreviousButton.IsEnabled);
            Assert.Equal(1.0, card.PreviousButton.Opacity, 2);
            Assert.True(card.PlayPauseButton.IsEnabled);
            state.Dispose();
        });
    }

    [Fact]
    public void Card_NoTitle_SaysPlayingAudio()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (state, card) = CardFor(Session(title: " "));

            Assert.Equal("Playing audio", card.TitleText.Text);
            state.Dispose();
        });
    }

    [Fact]
    public void SeekBar_HiddenWithoutDuration()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var (withoutDuration, cardWithout) = CardFor(Session(duration: null));
            Assert.Equal(Visibility.Collapsed, cardWithout.Seek.Visibility);
            withoutDuration.Dispose();

            var (withDuration, cardWith) = CardFor(Session(duration: 200));
            Assert.Equal(Visibility.Visible, cardWith.Seek.Visibility);
            withDuration.Dispose();
        });
    }

    [Fact]
    public void SeekBar_AnimatesOnlyWhileOnScreen()
    {
        Sta.Run(() =>
        {
            var playing = new NowPlaying("spotify.exe", "Spotify", "spotify.exe", "Song", "Artist", true, 30,
                200, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true, true, true, true, null);
            var bar = new SeekBar(_ => { });
            var window = new Window { Content = bar, Width = 200, Height = 40 };

            // not on screen (the pill is closed): the clock does not run
            bar.Update(playing, Colors.Cyan);
            Assert.False(bar.IsAnimating);

            // shown: the fill animates from the position to the end
            Fixture.ShowOffScreen(window);
            bar.Update(playing, Colors.Cyan);
            Assert.True(bar.IsAnimating);

            // hidden again: the animation stops, and an update does not start it
            window.Hide();
            Assert.False(bar.IsAnimating);
            bar.Update(playing, Colors.Cyan);
            Assert.False(bar.IsAnimating);
            window.Close();
        });
    }

    [Fact]
    public void Chip_LeadFallsBackToAppIcon()
    {
        Sta.Run(() =>
        {
            var playing = new NowPlaying("spotify.exe", "Spotify", "spotify.exe", "Song", "Artist", true, 30, 200, 0,
                true, true, true, true, null);
            var chip = new Chip(ChipKind.Media, "media", "media") { Media = playing };
            var colors = new Dictionary<string, string>();
            ImageSource? none(string _) => null;

            // no artwork and no app icon yet: the music-note glyph
            var glyph = Assert.IsType<TextBlock>(ChipView.Lead(chip, 18, null, colors, art: none));
            Assert.Equal(ModuleGlyphs.Get("media"), glyph.Text);

            // the app's icon, once it is known, stands in for the artwork
            var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            icon.Freeze();
            ItemIcon.CacheExeIcon("spotify.exe", icon);
            Assert.IsType<Image>(ChipView.Lead(chip, 18, null, colors, art: none));

            // the artwork is the lead when there is one
            var art = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            art.Freeze();
            var image = Assert.IsType<Image>(ChipView.Lead(chip with { Media = playing with { ArtworkKey = "key" } }, 18, null,
                colors, art: _ => art));
            Assert.Same(art, image.Source);
        });
    }
}
