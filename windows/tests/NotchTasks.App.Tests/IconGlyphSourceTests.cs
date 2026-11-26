using System.IO;
using System.Text.RegularExpressions;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Permissions;
using NotchTasks.Core.Settings;
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
        Assert.Contains("x:Key=\"FluentRegular\" Member=\"NotchTasks.FluentIcons.Regular\"", app);
        Assert.Contains("x:Key=\"FluentFilled\" Member=\"NotchTasks.FluentIcons.Filled\"", app);
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
