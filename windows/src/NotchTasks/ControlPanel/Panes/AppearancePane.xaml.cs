using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Appearance pane (Preferences.swift appearance): the notch's look, the toast style and the accent, with a live preview.
public partial class AppearancePane : SettingsPane
{
    public AppearancePane(SettingsStore store) : base(store)
    {
        InitializeComponent();

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
        UpdatePreview();
    }

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
        // it, the card floats below it.
        switch (Store.ToastStyle)
        {
            case "compact":
                PreviewToast.Width = 200;
                PreviewToast.Margin = new Thickness(0, 36, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(0, 0, 10, 10);
                PreviewToast.Padding = new Thickness(12, 6, 12, 6);
                PreviewTitle.FontSize = 12;
                break;
            case "card":
                PreviewToast.Width = 260;
                PreviewToast.Margin = new Thickness(0, 46, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(14);
                PreviewToast.Padding = new Thickness(14, 10, 14, 10);
                PreviewTitle.FontSize = 13;
                break;
            case "banner":
                PreviewToast.Width = 300;
                PreviewToast.Margin = new Thickness(0, 36, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(0, 0, 12, 12);
                PreviewToast.Padding = new Thickness(14, 12, 14, 12);
                PreviewTitle.FontSize = 16;
                break;
            default:
                PreviewToast.Width = 260;
                PreviewToast.Margin = new Thickness(0, 36, 0, 0);
                PreviewToast.CornerRadius = new CornerRadius(0, 0, 16, 16);
                PreviewToast.Padding = new Thickness(14, 10, 14, 10);
                PreviewTitle.FontSize = 13;
                break;
        }
    }
}
