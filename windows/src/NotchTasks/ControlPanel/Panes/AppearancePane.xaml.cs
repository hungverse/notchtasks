using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Appearance pane (Preferences.swift appearance): the notch's look, the toast style and the accent, with a live preview.
public partial class AppearancePane : SettingsPane
{
    /// The Mac's green (Color.green), the tint of a test alert when the accent is "Match app color" (the Mac has no colour then).
    private const string GreenTint = "#30D158";

    private readonly AppState _state;
    private readonly List<ThemeTileView> _tiles = new();

    public AppearancePane(SettingsStore store, AppState state) : base(store)
    {
        _state = state;
        InitializeComponent();

        foreach (var theme in Themes.All)
        {
            var tile = BuildTile(theme);
            _tiles.Add(tile);
            ThemeGrid.Children.Add(tile.Button);
        }
        TestAlertButton.Click += (_, _) => TestAlert();
        ExportButton.Click += async (_, _) => await ExportProfile();
        ImportButton.Click += async (_, _) => await ImportProfile();
        Message = ShowDialogMessage;

        BackgroundBox.ItemsSource = ChoiceItems.Backgrounds;
        SizeBox.ItemsSource = ChoiceItems.Sizes;
        MotionBox.ItemsSource = ChoiceItems.Motions;
        ToastBox.ItemsSource = ChoiceItems.ToastStyles;
        AccentBox.ItemsSource = ChoiceItems.Accents;

        BackgroundBox.SelectionChanged += (_, _) => WriteChoice(BackgroundBox, "background");
        SizeBox.SelectionChanged += (_, _) => WriteChoice(SizeBox, "size");
        MotionBox.SelectionChanged += (_, _) => WriteChoice(MotionBox, "motion");
        ToastBox.SelectionChanged += (_, _) => WriteChoice(ToastBox, "toastStyle");
        AccentBox.SelectionChanged += (_, _) => WriteChoice(AccentBox, "accent");
        TopColor.ValueChanged += (_, _) => Write("gradientA", JsonValue.Create(TopColor.Hex));
        BottomColor.ValueChanged += (_, _) => Write("gradientB", JsonValue.Create(BottomColor.Hex));
        CustomAccent.ValueChanged += (_, _) => Write("customAccent", JsonValue.Create(CustomAccent.Hex));
    }

    protected override void Apply()
    {
        Select(BackgroundBox, ChoiceItems.Backgrounds, Store.Background);
        Select(SizeBox, ChoiceItems.Sizes, Store.Size);
        Select(MotionBox, ChoiceItems.Motions, Store.Motion);
        Select(ToastBox, ChoiceItems.ToastStyles, Store.ToastStyle);
        Select(AccentBox, ChoiceItems.Accents, Store.Accent);
        TopColor.Hex = Store.GradientA;
        BottomColor.Hex = Store.GradientB;
        CustomAccent.Hex = Store.CustomAccent;

        var gradient = Store.Background == "gradient";
        TopRow.Visibility = gradient ? Visibility.Visible : Visibility.Collapsed;
        BottomRow.Visibility = gradient ? Visibility.Visible : Visibility.Collapsed;
        CustomAccentRow.Visibility = Store.Accent == "custom" ? Visibility.Visible : Visibility.Collapsed;

        NotchNote.Text = L10n.L("This screen has no notch: shown as a floating Dynamic Island.");
        TestAlertButton.Content = L10n.L("Test alert");
        BackupHeader.Text = L10n.L("Back up settings");
        BackupLabel.Text = L10n.L("Back up, share");
        ExportButton.Content = L10n.L("Export…");
        ImportButton.Content = L10n.L("Import…");
        BackupFooter.Text = L10n.L("All your customization in one .json file.");
        UpdatePreview();
        RefreshThemes();
    }

    /// Where a message box shows its text. Tests replace it, so no dialog opens.
    internal Action<string, string> Message { get; set; } = (_, _) => { };

    /// Asks for a file to write the profile to and writes it (the Mac's exportProfile, NotchTasks.json by default).
    private async Task ExportProfile()
    {
        var dialog = new SaveFileDialog { FileName = "NotchTasks.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ExportProfileTo(dialog.FileName);
        }
    }

    /// Writes the profile of the stored customization to `path`. The text is made here, on the UI thread, and the file is written
    /// off it; a failed write is shown in a message box with the exception's message.
    internal async Task ExportProfileTo(string path)
    {
        var text = SettingsProfile.Export(Store);
        var problem = await Task.Run(() => WriteProfile(text, path));
        if (problem is not null)
        {
            Message(L10n.L("Couldn't export the profile"), problem);
        }
    }

    /// Writes the profile text to `path`; returns the problem (the exception's message) when the write fails, else null.
    internal static string? WriteProfile(string text, string path)
    {
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// Asks for a profile file and applies it (the Mac's importProfile).
    private async Task ImportProfile()
    {
        var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ImportProfileFrom(dialog.FileName);
        }
    }

    /// Reads a profile file off the UI thread, then applies it on the UI thread.
    internal async Task ImportProfileFrom(string path)
    {
        var read = await Task.Run(() => ReadProfile(path));
        ApplyProfile(read);
    }

    /// Reads and checks a profile file. Runs on a pool thread: it does not touch the store or the controls. A file too large to be
    /// a profile is refused; a disk or IO error is shown with the exception's message.
    internal static ProfileRead ReadProfile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > SettingsProfile.MaxFileBytes)
            {
                return SettingsProfile.NotAProfile();
            }
            return SettingsProfile.Read(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ProfileRead(new Dictionary<string, JsonNode?>(StringComparer.Ordinal), e.Message);
        }
    }

    /// Applies a read profile on the UI thread: its values are saved in one write, so every open pane and the pill take them. A
    /// profile that could not be read is shown in a message box and changes nothing.
    internal void ApplyProfile(ProfileRead read)
    {
        if (read.Error is not null)
        {
            Message(L10n.L("Couldn't import the profile"), read.Error);
            return;
        }
        WriteMany(read.Values);
    }

    /// Shows a message box owned by the Control Panel window.
    private void ShowDialogMessage(string caption, string text) =>
        MessageBox.Show(Window.GetWindow(this), text, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    /// Shows a task-finished toast in the current style, as the Mac's testToast: it plays the task's sound and queues the
    /// toast on the pill, and posts no system notification (the user is already looking at the pane).
    private void TestAlert()
    {
        var tint = Store.Accent == "auto" ? GreenTint : Appearance.ResolveAccent(Store.Accent, Store.CustomAccent, null) ?? GreenTint;
        _state.Announce(new Toast(
            Glyph: "check",
            Tint: tint,
            Title: L10n.L("Task finished"),
            Subtitle: L10n.L("Test alert"),
            Event: "taskDone"), notify: false);
    }

    /// Shows the theme tiles as the store is now: the selected theme gets the accent border and the check, and the titles
    /// follow the language.
    private void RefreshThemes()
    {
        foreach (var tile in _tiles)
        {
            var selected = Themes.IsSelected(tile.Theme, Store);
            tile.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            // A dynamic reference, so a live light/dark or accent change reaches the border (a FindResource would keep the old brush).
            if (selected)
            {
                tile.Frame.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
            }
            else
            {
                tile.Frame.BorderBrush = Brushes.Transparent;
            }
            tile.Title.Text = L10n.L(tile.Theme.TitleKey);
            AutomationProperties.SetName(tile.Button, tile.Title.Text);
        }
    }

    /// Whether the tile of a theme shows its check (the theme is the one the store has). For the tests.
    internal bool ShowsSelected(string themeId) =>
        _tiles.First(tile => tile.Theme.Id == themeId).Check.Visibility == Visibility.Visible;

    /// Gives the controls the stored values now (the tests check the theme tiles after a change made outside the pane).
    internal void ShowStoredValues() => ApplyStored();

    /// A theme's one click: its values are saved in one write, so every open pane and the pill take the look at once.
    private void ApplyTheme(Theme theme) => WriteMany(theme.Values);

    /// One theme tile: a preview of the theme (its background, the closed pill with an accent dot and bar, the Mac's ThemeTile),
    /// its title and the check of the selected theme.
    private ThemeTileView BuildTile(Theme theme)
    {
        var accent = HexColor.ToColor(PreviewAccent(theme));
        var accentBrush = HexColor.Brush(PreviewAccent(theme));

        // The background is the theme's own: a gradient, or an opaque grey for glass and the solid looks (the Mac's
        // 0.35 and 0.12 white), so the tile shows the look in both Windows themes.
        Brush previewBackground = theme.Background == "gradient"
            ? new LinearGradientBrush(HexColor.ToColor(theme.GradientA), HexColor.ToColor(theme.GradientB), new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(theme.Background == "glass" ? Color.FromRgb(0x59, 0x59, 0x59) : Color.FromRgb(0x1F, 0x1F, 0x1F));

        var pill = new Border
        {
            Width = 90,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Black,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(8, 0, 8, 0),
        };
        if (theme.Background == "glow")
        {
            pill.BorderThickness = new Thickness(1);
            pill.BorderBrush = accentBrush;
            pill.Effect = new DropShadowEffect { Color = accent, BlurRadius = 4, ShadowDepth = 0, Opacity = 1 };
        }
        var pillRow = new DockPanel();
        pillRow.Children.Add(new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(3.5),
            Background = accentBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var bar = new Border { Width = 14, Height = 4, CornerRadius = new CornerRadius(2), Background = accentBrush, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(bar, Dock.Right);
        pillRow.Children.Insert(0, bar);
        pill.Child = pillRow;

        var preview = new Grid { Height = 62 };
        preview.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = previewBackground });
        preview.Children.Add(pill);

        var glyph = ModuleGlyphs.Get("check");
        var check = new TextBlock
        {
            FontFamily = glyph.Family,
            Text = glyph.Glyph,
            FontSize = 16,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var labelRow = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        DockPanel.SetDock(check, Dock.Right);
        labelRow.Children.Add(check);
        labelRow.Children.Add(title);

        var frame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(10),
            Child = new StackPanel { Children = { preview, labelRow } },
        };
        frame.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

        var button = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = frame,
            Tag = theme.Id,
            Margin = new Thickness(4),
        };
        AutomationProperties.SetName(button, L10n.L(theme.TitleKey));
        button.Click += (_, _) => ApplyTheme(theme);
        return new ThemeTileView(theme, button, frame, check, title);
    }

    /// The accent a theme's preview shows: its own colour when it has one, the Mac's orange for "auto" (the Mac's ThemeTile).
    private static string PreviewAccent(Theme theme) =>
        theme.Accent == "auto" ? "#FF9F0A" : Appearance.ResolveAccent(theme.Accent, theme.CustomAccent, null) ?? "#FF9F0A";

    /// The controls of one theme tile, kept to show its selection and title.
    private sealed record ThemeTileView(Theme Theme, Button Button, Border Frame, TextBlock Check, TextBlock Title);

    /// Draws the closed pill and the toast sample with the stored look (background, gradient, accent glow, toast style).
    private void UpdatePreview()
    {
        var accentHex = Appearance.ResolveAccent(Store.Accent, Store.CustomAccent, SystemAccent.Hex()) ?? Appearance.FallbackAccent;
        var accent = HexColor.ToColor(accentHex);
        var accentBrush = HexColor.Brush(accentHex);

        Brush background = Store.Background switch
        {
            "glass" => new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1E)),
            "gradient" => new LinearGradientBrush(
                HexColor.ToColor(Store.GradientA), HexColor.ToColor(Store.GradientB), new Point(0, 0), new Point(1, 1)),
            _ => Brushes.Black,
        };
        PreviewPill.Background = background;
        PreviewToast.Background = background;

        var glow = Store.Background == "glow";
        PreviewGlow.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        PreviewGlow.BorderBrush = accentBrush;
        PreviewGlow.Effect = glow ? new DropShadowEffect { Color = accent, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 } : null;
        PreviewGlyph.Foreground = accentBrush;

        // The toast sample takes the toast style's shape: island and banner hang from the pill, the compact one sits inside
        // it, the card floats below it. Every toast is a floating island below the pill's 4 px gap (the Mac's
        // NotchView toast radius is 18), so none of them hugs the top edge.
        switch (Store.ToastStyle)
        {
            case "compact":
                PreviewToast.Width = 200;
                PreviewToast.Margin = new Thickness(0, 44, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(18);
                PreviewToast.Padding = new Thickness(12, 6, 12, 6);
                PreviewTitle.FontSize = 12;
                break;
            case "card":
                PreviewToast.Width = 260;
                PreviewToast.Margin = new Thickness(0, 54, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(14);
                PreviewToast.Padding = new Thickness(14, 10, 14, 10);
                PreviewTitle.FontSize = 13;
                break;
            case "banner":
                PreviewToast.Width = 300;
                PreviewToast.Margin = new Thickness(0, 44, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(18);
                PreviewToast.Padding = new Thickness(14, 12, 14, 12);
                PreviewTitle.FontSize = 16;
                break;
            default:
                PreviewToast.Width = 260;
                PreviewToast.Margin = new Thickness(0, 44, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(18);
                PreviewToast.Padding = new Thickness(14, 10, 14, 10);
                PreviewTitle.FontSize = 13;
                break;
        }
    }
}
