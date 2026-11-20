using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The file shelf's card, the pill's "Nothing running" rule and the quick bar's link. The files are made in a temp folder; the
/// state (state.json) is in the same temp root.
[Collection("L10n")]
public sealed class ShelfCardTests
{
    private static readonly Color Accent = Color.FromRgb(0x32, 0xD2, 0xFF);

    /// The English texts, from the Mac's tables (the English text is the key, so these are the strings the card shows).
    private static void InitEnglish() => L10n.Init(MacTables(), "en");

    private static string MacTables()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "mac", "Sources")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("mac/Sources"), "mac", "Sources");
    }

    /// A state with only the shelf module on, and the given files on the shelf (made in the temp root when they exist).
    private static AppState ShelfState(TempRoot root, params string[] paths)
    {
        var state = Fixture.NewState();
        state.Settings.Set("modules", JsonNode.Parse("[\"shelf\"]"));
        state.Settings.Set("disabled", JsonNode.Parse("[]"));
        state.Shelf.Add(paths);
        state.Publish();
        return state;
    }

    private static string FileIn(TempRoot root, string name)
    {
        var path = Path.Combine(root.Folder, name);
        File.WriteAllText(path, "shelf test");
        return path;
    }

    [Fact]
    public void Card_HiddenWhenEmptyAndNotTargeted()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            using var state = ShelfState(root);
            var card = new ShelfCard();

            Assert.False(card.Update(state, Accent));
            Assert.Empty(card.Tiles);
        });
    }

    [Fact]
    public void Card_EmptyTargeted_SaysDropHere()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            using var state = ShelfState(root);
            state.ShelfTargeted = true;
            var card = new ShelfCard();

            Assert.True(card.Update(state, Accent));

            Assert.Equal("Drop here", card.EmptyPrompt);
            Assert.True(card.EmptyVisible);
        });
    }

    [Fact]
    public void Card_TargetedThenLeft_HiddenAgain()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            using var state = ShelfState(root);
            state.ShelfTargeted = true;
            var card = new ShelfCard();
            Assert.True(card.Update(state, Accent));

            state.ShelfTargeted = false;

            Assert.False(card.Update(state, Accent));
        });
    }

    [Fact]
    public void Missing_StaysDimmed()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var present = FileIn(root, "present.txt");
            var missing = Path.Combine(root.Folder, "gone.txt");
            using var state = ShelfState(root, present, missing);
            var card = new ShelfCard();

            Assert.True(card.Update(state, Accent));
            // The existence check runs on the background and the tiles show it when it lands.
            Fixture.PumpUntil(() => card.Tiles.Count == 2 && card.Tiles[1].Thumb.Opacity < 1);

            Assert.Equal(2, card.Tiles.Count);
            Assert.Equal(1.0, card.Tiles[0].Thumb.Opacity);
            Assert.Equal(0.35, card.Tiles[1].Thumb.Opacity);
            Assert.Equal("File not found anymore: " + missing, card.Tiles[1].ToolTip);
            Assert.Equal(present, card.Tiles[0].ToolTip);
        });
    }

    [Fact]
    public void DragOut_SkipsMissing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var present = FileIn(root, "present.txt");
            var other = FileIn(root, "other.txt");
            var missing = Path.Combine(root.Folder, "gone.txt");
            var items = new[] { present, missing, other };
            var exists = new Func<string, bool>(p => File.Exists(p));

            // a missing tile dragged alone carries nothing
            Assert.Empty(ShelfCard.DragFiles(missing, items, new HashSet<string>(), exists));
            // a selection carries its existing files only
            var selected = new HashSet<string>(new[] { present, missing, other }, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(new[] { present, other }, ShelfCard.DragFiles(present, items, selected, exists));
            // an unselected tile carries just that file
            Assert.Equal(new[] { other }, ShelfCard.DragFiles(other, items, new HashSet<string>(), exists));
        });
    }

    [Fact]
    public void Selection_HeaderAndHint()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var a = FileIn(root, "a.txt");
            var b = FileIn(root, "b.txt");
            using var state = ShelfState(root, a, b);
            var card = new ShelfCard();
            card.Update(state, Accent);

            Assert.Equal("File shelf", card.HeaderTitle);
            Assert.Equal("2", card.HeaderCount);
            Assert.Equal("Click to select · double-click to open · drag out to drop into another app", card.Hint);

            card.Toggle(a);
            card.Update(state, Accent);

            Assert.Equal("1/2 selected", card.HeaderTitle);
            Assert.Equal(string.Empty, card.HeaderCount);
            Assert.Equal("Drag one selected file to bring all 1 files", card.Hint);
            Assert.True(card.Tiles[0].IsSelected);
            Assert.False(card.Tiles[1].IsSelected);
        });
    }

    [Fact]
    public void Remove_SelectedOnly()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var a = FileIn(root, "a.txt");
            var b = FileIn(root, "b.txt");
            var c = FileIn(root, "c.txt");
            using var state = ShelfState(root, a, b, c);
            var card = new ShelfCard();
            card.Update(state, Accent);
            card.Toggle(b);

            card.RemoveTargets();

            Assert.Equal(new[] { a, c }, state.Shelf.Items);
            Assert.Equal(new[] { a, c }, card.Tiles.Select(t => t.FilePath).ToArray());
            Assert.Empty(card.Selected);
        });
    }

    [Fact]
    public void RemoveAll_WithoutSelection()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            var a = FileIn(root, "a.txt");
            var b = FileIn(root, "b.txt");
            using var state = ShelfState(root, a, b);
            var card = new ShelfCard();
            card.Update(state, Accent);

            card.RemoveTargets();

            Assert.Empty(state.Shelf.Items);
            Assert.False(card.Update(state, Accent));
        });
    }

    [Fact]
    public void NothingRunning_HiddenWithShelf()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            using var state = ShelfState(root, FileIn(root, "a.txt"));
            var pill = new PillWindow(state);
            try
            {
                state.Publish();

                Assert.Single(Fixture.Named<StackPanel>(pill, "SectionList").Children);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(pill, "EmptyNote").Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void QuickBarLink_OnlyWhenEmpty()
    {
        Sta.Run(() =>
        {
            InitEnglish();
            using var root = new TempRoot();
            using var state = ShelfState(root);
            var bar = new QuickBar();
            var link = Fixture.Named<UIElement>(bar, "ShelfLink");

            bar.Update(state);
            Assert.Equal(Visibility.Visible, link.Visibility);

            state.Shelf.Add(new[] { FileIn(root, "a.txt") });
            bar.Update(state);
            Assert.Equal(Visibility.Collapsed, link.Visibility);

            state.Shelf.Clear();
            state.ShelfTargeted = true;
            bar.Update(state);
            Assert.Equal(Visibility.Collapsed, link.Visibility);
        });
    }
}
