using System.IO;
using System.Text.RegularExpressions;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.ControlPanel;
using Xunit;

namespace NotchTasks.AppTests;

/// Every icon codepoint the app writes must be a glyph of the bundled Fluent fonts. A wrong code draws an empty box, so each
/// place that holds codes is checked: the source literals, the Core catalogs (module and permission glyphs) and the pane icons.
public sealed class IconGlyphSourceTests
{
    private static readonly Regex Escape = new(@"\\u([0-9A-Fa-f]{4})|&#x([0-9A-Fa-f]{4});", RegexOptions.Compiled);

    [Fact]
    public void EveryIconCodepointLiteralInTheSource_IsInABundledFont()
    {
        // Icon codes live in the Unicode private use area (U+E000 to U+F8FF); the scan takes only those, so ordinary text
        // escapes and entities never count. Each code, as an escape, an entity or a literal character, must be a glyph of the
        // Regular or the Filled font.
        var bundled = Union();
        var found = 0;
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Escape.Matches(text))
            {
                var hex = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var code = Convert.ToInt32(hex, 16);
                if (!IsPrivateUse(code))
                {
                    continue;
                }
                found++;
                Assert.True(bundled.Contains(code), $"{Path.GetFileName(file)}: U+{code:X4} is not in the bundled Fluent fonts");
            }
            foreach (var c in text.Where(c => IsPrivateUse(c)))
            {
                found++;
                Assert.True(bundled.Contains(c), $"{Path.GetFileName(file)}: U+{(int)c:X4} is not in the bundled Fluent fonts");
            }
        }
        Assert.True(found > 100, $"only {found} icon literals were found in the source");
    }

    [Fact]
    public void FontUri_IsWrittenInOnePlace_AndAppXamlDefinesTheTwoFamilies()
    {
        // The pack URI of the fonts is in FluentIcons.cs only; the XAML names the families through App.xaml's resources.
        var app = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "App.xaml"));
        Assert.Contains("x:Key=\"FluentRegular\" Member=\"local:FluentIcons.Regular\"", app);
        Assert.Contains("x:Key=\"FluentFilled\" Member=\"local:FluentIcons.Filled\"", app);
        Assert.Contains("xmlns:local=\"clr-namespace:NotchTasks\"", app);
        var uriFragments = Directory.EnumerateFiles(IconFonts.SourceRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Resources/Fonts/", StringComparison.Ordinal));
        Assert.Empty(uriFragments);
    }

    [Fact]
    public void EveryModuleGlyph_IsInTheFontItsFilledFlagNames()
    {
        foreach (var id in ModuleCatalog.All)
        {
            var table = ModuleCatalog.GlyphFilled(id) ? IconFonts.Filled : IconFonts.Regular;
            Assert.Contains(char.ConvertToUtf32(ModuleCatalog.Glyph(id), 0), table.Values);
        }
    }

    [Fact]
    public void EveryPermissionRowGlyph_IsInTheFontItsFilledFlagNames()
    {
        var facts = new PermissionFacts(null, false, null, null, null,
            new Dictionary<AgentKind, bool?>(), new Dictionary<AgentKind, bool>());
        foreach (var item in PermissionItems.Build(facts))
        {
            var table = item.Filled ? IconFonts.Filled : IconFonts.Regular;
            Assert.Contains(char.ConvertToUtf32(item.Glyph, 0), table.Values);
        }
    }

    [Fact]
    public void EveryPaneIcon_IsInTheRegularFont()
    {
        foreach (var pane in Enum.GetValues<PaneId>())
        {
            Assert.Contains(char.ConvertToUtf32(pane.Glyph(), 0), IconFonts.Regular.Values);
        }
    }

    [Fact]
    public void EveryPillGlyphName_IsInTheFontItsFilledFlagNames()
    {
        // ModuleGlyphs is the map of the pill's chips, toasts and Control Panel's glyph names: each name must draw a glyph of its family.
        foreach (var name in ModuleGlyphs.Names)
        {
            var table = ModuleGlyphs.IsFilled(name) ? IconFonts.Filled : IconFonts.Regular;
            var (_, glyph) = ModuleGlyphs.Get(name);
            Assert.Contains(char.ConvertToUtf32(glyph, 0), table.Values);
        }
    }

    [Fact]
    public void NetworkModule_DrawsTheFluentWifiGlyph_NotADot()
    {
        // the full-strength Wi-Fi (U+F8AC): the Network module's icon in the Control Panel and in the pill's Network chip.
        Assert.Equal("", ModuleCatalog.Glyph("network"));
        Assert.Equal("", ModuleGlyphs.Get("network").Glyph);
        Assert.Contains(0xF8AC, IconFonts.Regular.Values);  // the full-strength Wi-Fi (the JSON names it wifi_1, see ModuleGlyphs)
    }

    [Fact]
    public void NoFluentIconIsDrawnWithEmptyText()
    {
        // A TextBlock in the Fluent family with an empty Text draws nothing: the Sound pane's play button was like this.
        var empty = new Regex(@"Text\s*=\s*""""[\s\S]{0,80}?FontFamily\s*=\s*FluentIcons\.", RegexOptions.None, TimeSpan.FromSeconds(5));
        foreach (var file in SourceFiles())
        {
            Assert.False(empty.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} draws a Fluent icon with empty text");
        }
    }

    [Fact]
    public void NoPillOrControlPanelIcon_IsDrawnFromAPath()
    {
        // Every icon is a Fluent glyph (FluentGlyph). A path parsed from text is an icon drawn by hand, which the brief retired;
        // progress rings, spinners and charge art build their geometry from figures, not from text, and are not icons.
        foreach (var root in new[] { "Pill", "ControlPanel" }.Select(r => Path.Combine(IconFonts.SourceRoot, "NotchTasks", r)))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("Geometry.Parse(", File.ReadAllText(file));
            }
        }
    }

    [Fact]
    public void MacSymbolsTheApp_Uses_ResolveToAFluentGlyph()
    {
        // every Mac symbol the rows, badges and CI rules name is in the Fluent map (a name the map lacks draws the star or nothing)
        var names = new[]
        {
            "hammer.fill", "shippingbox.fill", "checkmark.diamond.fill", "icloud.and.arrow.up.fill", "server.rack",
            "arrow.down.circle.fill", "doc.on.doc.fill", "film.stack", "archivebox.fill", "arrow.triangle.branch",
            "arrow.left.arrow.right", "photo.stack.fill", "waveform", "cube.transparent.fill", "airplane", "xmark", "seal",
            "octagon", "applewatch", "dot.radiowaves.left.and.right", "cable.connector", "video.slash", "terminal.fill",
            "star", "star.fill", "sparkles",
        }.Concat(new[] { "GitLab Runner", "Tải file", "ffmpeg", "Git", "anything else" }.Select(DevRules.DefaultSymbol));
        foreach (var name in names)
        {
            Assert.True(ModuleGlyphs.Has(name), name);
            Assert.NotEqual(ModuleGlyphs.Fallback, ModuleGlyphs.Get(name).Glyph);
        }
    }

    [Fact]
    public void NoPillOrControlPanelFile_SetsAFluentFont_OutsideTheGlyphHelper()
    {
        // Every Fluent icon in the pill and the Control Panel is a FluentGlyph (centred on its outline). A TextBlock, Button or
        // ContentControl that names a Fluent family draws on the font's baseline, so none may: the Fluent families are named in
        // the helper (FluentGlyph, FluentIcons) and the Core glyph map (ModuleGlyphs), and nowhere else. No text is allow-listed.
        var fluentFont = new Regex(
            @"FluentRegular|FluentFilled|FluentIcons\.(Regular|Filled)|ModuleGlyphs\.Get\([^)]*\)\.Family|GlyphFamily",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "ModuleGlyphs.cs" };
        var roots = new[] { "Pill", "ControlPanel" }.Select(r => Path.Combine(IconFonts.SourceRoot, "NotchTasks", r));
        var offenders = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !allowed.Contains(Path.GetFileName(f)))
            .Where(f => fluentFont.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void CoulouredChipGlyphs_AreWhite_NotTheOnAccentBrush()
    {
        // The on-accent brush is black in dark mode: a glyph on a coloured chip is white (the module, Overview and permission chips).
        foreach (var file in new[] { "ModulesPane.xaml.cs", "OverviewPane.xaml.cs", "PermissionsPane.xaml.cs" })
        {
            var text = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "ControlPanel", "Panes", file));
            Assert.DoesNotContain("TextOnAccentFillColorPrimaryBrush", text);
        }
    }

    [Fact]
    public void ThemeGlyphs_FollowTheThemeLive_NotAOneTimeFindResource()
    {
        // A FindResource takes the brush once; a SetResourceReference follows a light and dark switch. Glyphs use the second.
        foreach (var file in new[]
                 {
                     Path.Combine("ControlPanel", "BadgeEditorBody.cs"),
                     Path.Combine("ControlPanel", "Panes", "SoundsPane.xaml.cs"),
                     Path.Combine("ControlPanel", "Panes", "WatchingPane.xaml.cs"),
                     Path.Combine("ControlPanel", "Panes", "ModulesPane.xaml.cs"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", file));
            Assert.DoesNotContain("FindResource(\"TextFillColorPrimaryBrush\")", text);
            Assert.DoesNotContain("FindResource(\"TextFillColorSecondaryBrush\")", text);
        }
    }

    [Fact]
    public void PillIcons_AreWhiteUnlessTheyCarryAStatusColour()
    {
        // The quick-bar timer glyph was grey (#8E8E93): a grey icon is not a status, so it is white.
        var quickBar = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "Pill", "QuickBar.xaml"));
        Assert.Contains("x:Name=\"TimerGlyph\" Size=\"12\" Foreground=\"White\"", quickBar);
        Assert.DoesNotContain("8E8E93", quickBar);
    }

    [Fact]
    public void EveryNavigationItem_IsNamedByItsPaneTitle()
    {
        // The Control Panel's nav rows are announced by their pane title, not by the item class name.
        var window = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "ControlPanel", "ControlPanelWindow.xaml"));
        Assert.Contains("<Setter Property=\"AutomationProperties.Name\" Value=\"{Binding Title}\" />", window);
    }

    [Fact]
    public void ProblemText_InPairingAndReport_UsesTheThemesCautionBrush_NotAFixedOrange()
    {
        // #FF9F0A is about 2:1 on a light card. The Windows caution brush follows the theme: the same amber in dark mode, a dark
        // amber in light mode. The Pro label on a phone row is the same brush, set through the theme (SetResourceReference).
        var panes = Path.Combine(IconFonts.SourceRoot, "NotchTasks", "ControlPanel", "Panes");
        var pairingXaml = File.ReadAllText(Path.Combine(panes, "PairingPane.xaml"));
        var pairingCode = File.ReadAllText(Path.Combine(panes, "PairingPane.xaml.cs"));
        var report = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "ControlPanel", "ReportDialog.xaml"));

        foreach (var text in new[] { pairingXaml, pairingCode, report })
        {
            Assert.DoesNotContain("#FF9F0A", text);
            Assert.DoesNotContain("0xFF, 0x9F, 0x0A", text);
        }
        Assert.Contains("x:Name=\"ProblemGlyph\" Size=\"14\" Foreground=\"{DynamicResource SystemFillColorCautionBrush}\"", pairingXaml);
        Assert.Contains("x:Name=\"ProblemText\" Foreground=\"{DynamicResource SystemFillColorCautionBrush}\"", pairingXaml);
        Assert.Contains("x:Name=\"ProblemText\" Foreground=\"{DynamicResource SystemFillColorCautionBrush}\"", report);
        Assert.Contains("SetResourceReference(TextBlock.ForegroundProperty, \"SystemFillColorCautionBrush\")", pairingCode);
    }

    [Fact]
    public void NavigationRows_AreThe36DipNavigationViewSize_WithA16DipGlyph()
    {
        var window = File.ReadAllText(Path.Combine(IconFonts.SourceRoot, "NotchTasks", "ControlPanel", "ControlPanelWindow.xaml"));
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"36\" />", window);
        Assert.Contains("Size=\"16\"", window);
    }

    private static HashSet<int> Union()
    {
        var all = new HashSet<int>(IconFonts.Regular.Values);
        all.UnionWith(IconFonts.Filled.Values);
        return all;
    }

    private static bool IsPrivateUse(int code) => code is >= 0xE000 and <= 0xF8FF;

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(IconFonts.SourceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}
