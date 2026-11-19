using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Settings;
using Xunit;

namespace NotchTasks.AppTests;

/// The watch list of the Watching pane and its item editor: rows, the On switch, presets, running apps, delete and the
/// editor's validation. Every test works in a temp settings folder with a fake process table; nothing reads the real one.
public sealed class WatchingPaneTests
{
    /// A process table with the given processes and no command lines.
    private sealed class FakeProcesses : IProcessReader
    {
        private readonly ProcSnapshot _snapshot;

        public FakeProcesses(params ProcInfo[] procs) => _snapshot = new ProcSnapshot(procs);

        public ProcSnapshot Snapshot() => _snapshot;

        public string? CommandLine(ProcInfo p) => null;

        public string? ImagePath(ProcInfo p) => null;
    }

    /// Writes the settings file with this items array, then loads the store over it.
    private static SettingsStore StoreWith(string itemsJson)
    {
        File.WriteAllText(AppPaths.SettingsFile, "{\"items\":" + itemsJson + "}");
        return SettingsStore.Load(AppPaths.SettingsFile);
    }

    /// The pane with a home and CLI folders under the temp root, so no real PATH, APPDATA or profile is read.
    private static WatchingPane NewPane(SettingsStore store, IProcessReader processes, TempRoot root) =>
        new(store, processes, Path.Combine(root.Folder, "home"), path: "", appData: Path.Combine(root.Folder, "appdata"));

    /// Hosts the pane in an off-screen window, so it loads and builds its rows.
    private static Window Show(UIElement pane)
    {
        var window = new Window { Content = pane };
        Fixture.ShowOffScreen(window);
        return window;
    }

    /// The rows of the watch list (each row is a Grid; separators are skipped).
    private static List<Grid> Rows(FrameworkElement pane) =>
        Fixture.Named<StackPanel>(pane, "WatchRows").Children.OfType<Grid>().ToList();

    private static string NameOf(Grid row) =>
        row.Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().First().Text;

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// The text of a menu entry or header (they are TextBlocks, so an "_" in a name is not an access key).
    private static string HeaderText(MenuItem item) => ((TextBlock)item.Header).Text;

    /// The menu the Add button builds now.
    private static ContextMenu AddMenu(WatchingPane pane)
    {
        Fixture.Call(pane, "RebuildAddMenu");
        return Fixture.Named<Button>(pane, "AddButton").ContextMenu;
    }

    /// Explorer and its children: n programs started from the desktop, named app000.exe, app001.exe, ...
    private static FakeProcesses DesktopWith(int children)
    {
        var procs = new List<ProcInfo> { new(10, 1, "explorer.exe", 1, 0) };
        for (var i = 0; i < children; i++)
        {
            procs.Add(new ProcInfo(100 + i, 10, $"app{i:000}.exe", 1, 0));
        }
        return new FakeProcesses(procs.ToArray());
    }

    [Fact]
    public void List_ShowsItems_ToggleWritesSettings()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith("[{\"id\":\"a1\",\"name\":\"Alpha\",\"kind\":\"process\",\"process\":\"alpha\",\"enabled\":true},"
                + "{\"id\":\"b1\",\"name\":\"Beta\",\"kind\":\"app\",\"bundleID\":\"beta.exe\",\"enabled\":true}]");
            var pane = NewPane(store, new FakeProcesses(), root);
            var window = Show(pane);
            try
            {
                var rows = Rows(pane);
                Assert.Equal(2, rows.Count);
                Assert.Equal("Alpha", NameOf(rows[0]));
                Assert.Equal("Beta", NameOf(rows[1]));

                // Switching the first row off writes the list through SetItems.
                var on = rows[0].Children.OfType<CheckBox>().Single();
                on.IsChecked = false;
                Click(on);

                Assert.False(store.Items[0].Enabled);
                Assert.True(store.Items[1].Enabled);
                Assert.Equal("a1", store.Items[0].Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AddPreset_AddsOnce_MarksAdded()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith("[]");
            var pane = NewPane(store, new FakeProcesses(), root);
            var window = Show(pane);
            try
            {
                var claude = WatchItems.Presets.Single(p => p.Name == "Claude");
                Fixture.Call(pane, "AddPreset", claude);
                Fixture.Call(pane, "AddPreset", claude);

                Assert.Single(store.Items);
                Assert.Equal("Claude", store.Items[0].Name);

                // The menu marks the added preset and does not offer it again.
                var entry = AddMenu(pane).Items.OfType<MenuItem>().Single(m => m.Header is TextBlock && HeaderText(m).Contains("Claude"));
                Assert.EndsWith(L10n.L(" (added)"), HeaderText(entry));
                Assert.False(entry.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Delete_RemovesOnlyThatItem_KeepsUnknownFields()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith("[{\"id\":\"a1\",\"name\":\"Alpha\",\"kind\":\"process\",\"process\":\"alpha\",\"future\":\"keep\"},"
                + "{\"id\":\"b1\",\"name\":\"Beta\",\"kind\":\"app\",\"bundleID\":\"beta.exe\",\"futureNum\":7}]");
            var pane = NewPane(store, new FakeProcesses(), root);
            var window = Show(pane);
            try
            {
                var remove = Rows(pane)[0].Children.OfType<Button>().Single(b => Equals(b.ToolTip, L10n.L("Remove")));
                Click(remove);

                Assert.Single(store.Items);
                Assert.Equal("b1", store.Items[0].Id);
                Assert.Equal(7, store.Items[0].Extra["futureNum"]!.GetValue<int>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Editor_InvalidThreshold_NotSaved()
    {
        Sta.Run(() =>
        {
            var editor = new WatchItemEditor(new WatchItem("a1", "Alpha", WatchKind.Process, Process: "alpha"), isNew: false);
            var threshold = Fixture.Named<TextBox>(editor, "ThresholdBox");

            foreach (var bad in new[] { "0", "0.4", "101", "many" })
            {
                threshold.Text = bad;
                Fixture.Call(editor, "Save");
                Assert.Null(editor.Result);
            }

            threshold.Text = "5";
            Fixture.Call(editor, "Save");
            Assert.NotNull(editor.Result);
            Assert.Equal(5, editor.Result!.Threshold);
        });
    }

    [Fact]
    public void RunningApps_AreExplorerChildren_WithoutNotchTasks()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var procs = new FakeProcesses(
                new ProcInfo(10, 1, "explorer.exe", 1, 0),
                new ProcInfo(11, 10, "Code.exe", 1, 0),
                new ProcInfo(12, 10, "NotchTasks.exe", 1, 0),
                new ProcInfo(13, 10, "chrome.exe", 1, 0),
                new ProcInfo(14, 1, "svchost.exe", 1, 0));
            var pane = NewPane(StoreWith("[]"), procs, root);
            var window = Show(pane);
            try
            {
                var apps = AddMenu(pane).Items.OfType<MenuItem>().Where(m => m.Tag as string == "running").Select(HeaderText).ToList();
                Assert.Equal(new[] { "Chrome", "Code" }, apps);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void OpenApps_AreCappedAt80()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var pane = NewPane(StoreWith("[]"), DesktopWith(100), root);
            var window = Show(pane);
            try
            {
                var apps = AddMenu(pane).Items.OfType<MenuItem>().Where(m => m.Tag as string == "running").ToList();
                Assert.Equal(80, apps.Count);
                Assert.Equal("App000", HeaderText(apps[0]));
                Assert.Equal("App079", HeaderText(apps[79]));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InstalledPresets_AreSortedFirst()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            // A gemini CLI in the user's bin folder makes Gemini CLI installed; Codex is not.
            var bin = Path.Combine(root.Folder, "home", ".local", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "gemini.exe"), string.Empty);
            var pane = NewPane(StoreWith("[]"), new FakeProcesses(), root);
            var window = Show(pane);
            try
            {
                // The first group is the terminal agents: Claude (always installed), then Codex (not) and Gemini CLI (installed).
                // The entries after the group's title, up to its separator.
                var first = AddMenu(pane).Items.Cast<object>().Skip(1).TakeWhile(x => x is MenuItem)
                    .Cast<MenuItem>().Select(HeaderText).ToList();
                var installed = first.Select(h => h.StartsWith("✓ ", StringComparison.Ordinal)).ToList();
                var firstNotInstalled = installed.IndexOf(false);
                Assert.True(firstNotInstalled > 0);
                Assert.DoesNotContain(true, installed.Skip(firstNotInstalled));
                Assert.Contains(first, h => h.StartsWith("✓ Gemini CLI", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Toggle_KeepsUnknownFields()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var store = StoreWith("[{\"id\":\"a1\",\"name\":\"Alpha\",\"kind\":\"process\",\"process\":\"alpha\",\"future\":\"keep\"}]");
            var pane = NewPane(store, new FakeProcesses(), root);
            var window = Show(pane);
            try
            {
                var on = Rows(pane)[0].Children.OfType<CheckBox>().Single();
                on.IsChecked = false;
                Click(on);

                Assert.False(store.Items[0].Enabled);
                Assert.Equal("keep", store.Items[0].Extra["future"]!.GetValue<string>());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Editor_EmptyName_NotSaved()
    {
        Sta.Run(() =>
        {
            var editor = new WatchItemEditor(new WatchItem("a1", "Alpha", WatchKind.Process, Process: "alpha"), isNew: false);
            Fixture.Named<TextBox>(editor, "NameBox").Text = "   ";
            Fixture.Call(editor, "Save");
            Assert.Null(editor.Result);
        });
    }

    [Fact]
    public void Editor_AppKind_SavesExe()
    {
        Sta.Run(() =>
        {
            var editor = new WatchItemEditor(new WatchItem("b1", "Foo", WatchKind.App), isNew: false);
            Fixture.Named<ComboBox>(editor, "KindBox").SelectedIndex = 1;
            const string exe = @"C:\Apps\foo\foo.exe";
            Fixture.Named<TextBox>(editor, "ProgramBox").Text = exe;
            Fixture.Call(editor, "Save");

            Assert.NotNull(editor.Result);
            Assert.Equal(WatchKind.App, editor.Result!.Kind);
            Assert.Equal(exe, editor.Result.Exe);
        });
    }
}
