using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Pill;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Notifications;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class AppearancePaneTests
{
    /// The theme tiles of the grid, in the order they are shown.
    private static List<Button> Tiles(AppearancePane pane) =>
        Fixture.Named<Panel>(pane, "ThemeGrid").Children.OfType<Button>().ToList();

    /// Counts the system notifications the app posts.
    private sealed class RecordingNotifier : INotifier
    {
        public List<Toast> Posted { get; } = new();

        public void Post(Toast t) => Posted.Add(t);
    }

    /// An app state whose system notifier is `notifier`, over the temp settings of the test.
    private static AppState StateWith(SettingsStore settings, INotifier notifier) =>
        new(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Notifier = notifier });

    [Fact]
    public void TestAlert_QueuesATaskFinishedToastInTheAccent_WithNoSystemNotification()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);
            settings.Set("accent", JsonValue.Create("orange"));
            var pane = Fixture.Create<AppearancePane>(settings, state);

            Fixture.Named<Button>(pane, "TestAlertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var toast = state.Toasts.Current;
            Assert.NotNull(toast);
            Assert.Equal("Task finished", toast.Title);
            Assert.Equal("Test alert", toast.Subtitle);
            Assert.Equal("taskDone", toast.Event);
            Assert.Equal("#FF9F0A", toast.Tint);
            Assert.Empty(notifier.Posted);
        });
    }

    [Fact]
    public void TestAlert_MatchAppColorIsTheMacsGreen()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            using var state = StateWith(settings, new RecordingNotifier());
            var pane = Fixture.Create<AppearancePane>(settings, state);

            Fixture.Named<Button>(pane, "TestAlertButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("#30D158", state.Toasts.Current?.Tint);
        });
    }

    [Fact]
    public void ThemeGrid_ShowsTheSixThemesInTheMacsOrder()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            Assert.Equal(Themes.All.Select(t => t.Id), Tiles(pane).Select(b => (string)b.Tag));
        });
    }

    /// The text blocks under a node of the logical tree (a tile's title is its only one).
    private static IEnumerable<TextBlock> TextBlocksOf(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is TextBlock text)
            {
                yield return text;
            }
            foreach (var inner in TextBlocksOf(child))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public void ThemeTiles_TitleIsThePrimaryTextBrush()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var primary = pane.FindResource("TextFillColorPrimaryBrush");

            // Each tile's title sits on its card, so it is the theme's primary text (13 DIP SemiBold), in every theme's look.
            foreach (var tile in Tiles(pane))
            {
                var title = Assert.Single(TextBlocksOf(tile));
                Assert.Same(primary, title.Foreground);
                Assert.Equal(13, title.FontSize);
                Assert.Equal(FontWeights.SemiBold, title.FontWeight);
            }
        });
    }

    [Fact]
    public void ClickingATheme_AppliesItToTheStoreAndSelectsItsTile()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var glass = Tiles(pane).First(b => (string)b.Tag == "glass");

            glass.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // The pane is not shown here, so it takes the store's values as a shown pane does on each change.
            pane.ShowStoredValues();

            Assert.Equal("glass", state.Settings.Background);
            Assert.Equal("banner", state.Settings.ToastStyle);
            Assert.Equal("smooth", state.Settings.Motion);
            Assert.Equal("white", state.Settings.Accent);
            Assert.True(pane.ShowsSelected("glass"));
            Assert.False(pane.ShowsSelected("classic"));
        });
    }

    [Fact]
    public void AnOutsideChange_MovesTheSelectionToTheMatchingTheme()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            state.Settings.SetMany(Themes.All.First(t => t.Id == "ocean").Values);
            pane.ShowStoredValues();

            Assert.True(pane.ShowsSelected("ocean"));
            Assert.False(pane.ShowsSelected("sunset"));
        });
    }

    /// A profile as the Mac writes it, with a key Windows lacks (haptics) and a module Windows does not have (fans).
    private const string MacProfile = """
    {
      "accent" : "custom",
      "background" : "glass",
      "customAccent" : "#00F5D4",
      "disabled" : [ "timer", "fans" ],
      "doneAfter" : 45,
      "expandMode" : "hover",
      "gradientA" : "#1E1B4B",
      "gradientB" : "#000000",
      "haptics" : false,
      "hoverDelay" : 0.15,
      "limitWarn" : 80,
      "menuBarIcon" : true,
      "moduleColors" : {},
      "modules" : [ "agents", "fans", "timer" ],
      "motion" : "snappy",
      "placements" : {},
      "size" : "compact",
      "soundMap" : {},
      "sounds" : true,
      "systemNotifications" : true,
      "toastStyle" : "card",
      "volume" : 0.5
    }
    """;

    [Fact]
    public void ExportProfile_WritesTheMacsProfileFile()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var path = Path.Combine(root.Folder, "NotchTasks.json");

            Assert.Null(AppearancePane.WriteProfile(SettingsProfile.Export(state.Settings), path));

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(22, json.Count);
            Assert.True((bool?)json["haptics"]);
        });
    }

    [Fact]
    public void ExportProfile_AFailedWriteReportsIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var bad = Path.Combine(root.Folder, "no-such-folder", "NotchTasks.json");

            Assert.NotNull(AppearancePane.WriteProfile("{}", bad));
        });
    }

    [Fact]
    public void ImportProfile_AppliesTheMacProfile_ToTheStore()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var path = Path.Combine(root.Folder, "mac.json");
            File.WriteAllText(path, MacProfile);

            pane.ApplyProfile(AppearancePane.ReadProfile(path));

            Assert.Equal("glass", state.Settings.Background);
            Assert.Equal("#00F5D4", state.Settings.CustomAccent);
            Assert.Equal("snappy", state.Settings.Motion);
            Assert.Equal("card", state.Settings.ToastStyle);
            Assert.Equal(45, state.Settings.DoneAfter, 1);
            Assert.Equal(0.5, state.Settings.Volume, 2);
            Assert.Equal(new[] { "timer" }, state.Settings.Disabled);
        });
    }

    [Fact]
    public void ImportProfile_ANonProfile_ShowsTheMessageAndChangesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;
            var path = Path.Combine(root.Folder, "notes.json");
            File.WriteAllText(path, "{\"hello\":1}");
            var before = state.Settings.Background;

            pane.ApplyProfile(AppearancePane.ReadProfile(path));

            Assert.Equal("Couldn't import the profile|This file isn't a NotchTasks profile.", shown);
            Assert.Equal(before, state.Settings.Background);
        });
    }

    [Fact]
    public void ImportProfile_AMissingFile_ShowsTheProblem()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            string? shown = null;
            pane.Message = (caption, text) => shown = caption + "|" + text;

            var read = AppearancePane.ReadProfile(Path.Combine(root.Folder, "gone.json"));
            pane.ApplyProfile(read);

            Assert.NotNull(read.Error);
            Assert.Equal("Couldn't import the profile|" + read.Error, shown);
        });
    }

    [Fact]
    public void SelectedThemeBorder_FollowsALiveThemeChange()
    {
        // The selected tile's border is the accent of the current theme: a change of the accent brush reaches it at once, without a
        // rebuild of the pane.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            var window = new Window { Content = pane, ShowInTaskbar = false };
            Fixture.ShowOffScreen(window);
            try
            {
                var accent = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                pane.Resources["AccentFillColorDefaultBrush"] = accent;
                Fixture.Flush();

                var tiles = (System.Collections.IEnumerable)Fixture.Field<object>(pane, "_tiles");
                var selected = tiles.Cast<object>()
                    .Select(tile => (Border)tile.GetType().GetProperty("Frame")!.GetValue(tile)!)
                    .Where(frame => frame.BorderBrush == accent)
                    .ToList();

                Assert.Single(selected);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// A crisp ring: no effect, the accent at full alpha, and drawn above the fill (later in the same parent).
    private static void AssertCrispRing(Border ring, Border fill, Color accent)
    {
        Assert.Null(ring.Effect);
        var brush = Assert.IsType<SolidColorBrush>(ring.BorderBrush);
        Assert.Equal(255, brush.Color.A);
        Assert.Equal(accent, brush.Color);
        var siblings = ((Panel)ring.Parent).Children.Cast<UIElement>().ToList();
        Assert.True(siblings.IndexOf(ring) > siblings.IndexOf(fill));
    }

    [Fact]
    public void PreviewRing_IsAboveThePill_WithNoEffect_AndFullAccent_ForTheGlowBackground()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("background", JsonValue.Create("glow"));
            state.Settings.Set("accent", JsonValue.Create("orange"));
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            pane.ShowStoredValues();

            var ring = Fixture.Named<Border>(pane, "PreviewRing");
            Assert.Equal(Visibility.Visible, ring.Visibility);
            AssertCrispRing(ring, Fixture.Named<Border>(pane, "PreviewPill"), Color.FromRgb(0xFF, 0x9F, 0x0A));
        });
    }

    [Fact]
    public void ThemeTileRing_IsAboveThePillWithNoEffect_ForTheGlowTheme()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            var glow = Themes.All.First(t => t.Background == "glow");
            var ring = pane.RingOf(glow.Id);

            Assert.NotNull(ring);
            Assert.Null(ring!.Effect);
            var brush = Assert.IsType<SolidColorBrush>(ring.BorderBrush);
            Assert.Equal(255, brush.Color.A);
            var siblings = ((Panel)ring.Parent).Children.Cast<UIElement>().ToList();
            var fill = siblings.OfType<Border>().First(b => b.Width == 90 && b.Background is not null);
            Assert.True(siblings.IndexOf(ring) > siblings.IndexOf(fill));
            Assert.Null(pane.RingOf(Themes.All.First(t => t.Background != "glow").Id));
        });
    }

    [Fact]
    public void PillGlowRing_IsCrisp_AboveTheFill_AndOutsideTheClip()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("background", JsonValue.Create("glow"));
            var pill = new PillWindow(state);
            try
            {
                Fixture.Call(pill, "ApplyAppearance");
                // The ring shows only while the pill is open or has a collapsed item; this checks what it is when shown.
                var ring = Fixture.Named<System.Windows.Shapes.Path>(pill, "GlowRing");
                Assert.Null(ring.Effect);
                Assert.True(ring.SnapsToDevicePixels);
                Assert.True(ring.StrokeThickness >= 1.5);
                var brush = Assert.IsType<SolidColorBrush>(ring.Stroke);
                Assert.Equal(255, brush.Color.A);

                var siblings = ((Panel)ring.Parent).Children.Cast<UIElement>().ToList();
                var content = Fixture.Named<Grid>(pill, "PillContent");
                Assert.True(siblings.IndexOf(ring) > siblings.IndexOf(content));
                Assert.NotSame(content, ring.Parent);  // not inside PillContent's clip, so its whole stroke shows
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void TestAlertButton_HasTheWhiteBellGlyph_AndItsLabel()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);
            pane.ShowStoredValues();

            var button = Fixture.Named<Button>(pane, "TestAlertButton");
            var glyph = ((StackPanel)button.Content).Children.OfType<FluentGlyph>().Single();
            Assert.Equal("", glyph.Code);
            Assert.Equal(Colors.White, ((SolidColorBrush)glyph.Foreground).Color);
            Assert.Equal("Test alert", Fixture.Named<TextBlock>(pane, "TestAlertLabel").Text);
        });
    }

    [Fact]
    public void ThemeTiles_HaveTwelveDipBetweenThemAndAHairlineUnlessSelected()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pane = Fixture.Create<AppearancePane>(state.Settings, state);

            // Each tile has 6 DIP on each side, so two neighbours have 12 DIP between them.
            Assert.All(Tiles(pane), b => Assert.Equal(12, b.Margin.Left + b.Margin.Right));

            state.Settings.SetMany(Themes.All.First(t => t.Id == "ocean").Values);
            pane.ShowStoredValues();

            var ocean = (Border)Tiles(pane).First(b => (string)b.Tag == "ocean").Content;
            var sunset = (Border)Tiles(pane).First(b => (string)b.Tag == "sunset").Content;
            Assert.Equal(2, ocean.BorderThickness.Left);
            Assert.Equal(1, sunset.BorderThickness.Left);
            Assert.Equal(12, ocean.Padding.Left);
        });
    }
}
