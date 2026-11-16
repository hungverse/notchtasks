using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using WinForms = System.Windows.Forms;

namespace NotchTasks.ControlPanel;

/// A colour: eight swatches, the hex text (#RRGGBB) and a button for Windows' colour dialog. Hex is the colour; ValueChanged
/// is raised when the user picks one (not when Hex is set from outside, so a stored value never writes back).
public partial class ColorField : UserControl
{
    /// The swatches: the palette accents (Appearance), then black and the Classic theme's indigo.
    private static readonly IReadOnlyList<string> SwatchColors =
    [
        Appearance.ResolveAccent("blue", string.Empty, null)!,
        Appearance.ResolveAccent("green", string.Empty, null)!,
        Appearance.ResolveAccent("orange", string.Empty, null)!,
        Appearance.ResolveAccent("pink", string.Empty, null)!,
        Appearance.ResolveAccent("purple", string.Empty, null)!,
        Appearance.ResolveAccent("white", string.Empty, null)!,
        "#000000",
        "#1E1B4B",
    ];

    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorField), new PropertyMetadata("#000000", OnHexChanged));

    public ColorField()
    {
        InitializeComponent();
        foreach (var hex in SwatchColors)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("ColorSwatch"),
                Background = HexColor.Brush(hex),
                Tag = hex,
                ToolTip = hex,
            };
            swatch.Click += OnSwatchClick;
            Swatches.Children.Add(swatch);
        }
    }

    /// The colour as "#RRGGBB".
    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    /// Raised when the user picks a colour: a swatch, a typed hex, or the colour dialog.
    public event EventHandler? ValueChanged;

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorField)d).HexBox.Text = (string)e.NewValue;

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            Pick(hex);
        }
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.ColorDialog { FullOpen = true, AnyColor = true };
        var current = HexColor.ToColor(Hex);
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }
        var picked = dialog.Color;
        Pick($"#{picked.R:X2}{picked.G:X2}{picked.B:X2}");
    }

    /// Takes the text when it is a colour, and shows the colour again when it is not.
    private void CommitText()
    {
        if (HexColor.TryNormalize(HexBox.Text, out var hex))
        {
            Pick(hex);
        }
        else
        {
            HexBox.Text = Hex;
        }
    }

    private void Pick(string hex)
    {
        HexBox.Text = hex;
        if (hex == Hex)
        {
            return;
        }
        Hex = hex;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
