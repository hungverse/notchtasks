using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
using NotchTasks.Pill;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Sound pane (Preferences.swift sounds): the on/off switch, the volume, the pack that sets every event's sound, the
/// backup row that exports and imports a pack, and one sound per event with a button that plays it. An event's picker ends
/// with "Choose your file…", which keeps a file of the user's own and uses it for the event.
public partial class SoundsPane : SettingsPane
{
    private readonly DelayedWrite _volume;
    private readonly List<EventRow> _rows = new();
    private readonly ObservableCollection<Choice> _packs;

    public SoundsPane(SettingsStore store) : base(store)
    {
        InitializeComponent();
        _volume = Delayed();

        _packs = new ObservableCollection<Choice>(ChoiceItems.SoundPacks);
        PackBox.ItemsSource = _packs;
        PackBox.SelectionChanged += (_, _) => OnPackSelected();
        ExportButton.Click += async (_, _) => await ExportPack();
        ImportButton.Click += async (_, _) => await ImportPack();
        Message = ShowDialogMessage;

        foreach (var choice in ChoiceItems.SoundEvents)
        {
            _rows.Add(AddRow(choice));
        }

        SoundOn.Click += (_, _) => Write("sounds", JsonValue.Create(SoundOn.IsChecked == true));
        VolumeSlider.ValueChanged += (_, _) =>
        {
            if (Applying)
            {
                return;
            }
            _volume.Schedule(() => Write("volume", JsonValue.Create(Math.Round(VolumeSlider.Value, 2))));
        };
    }

    /// Where a message box shows its text. Tests replace it, so no dialog opens.
    internal Action<string, string> Message { get; set; } = (_, _) => { };

    /// Gives the controls the stored values now (the pane does this when it is shown and when the store changes).
    internal void ShowStoredValues() => ApplyStored();

    /// The folder of the custom sounds, which a pack's files are kept in (SoundPackFile.folder on the Mac).
    private SoundPackFile PackFiles => new(Path.Combine(AppPaths.Root, "Sounds"));

    protected override void Apply()
    {
        SoundOn.IsChecked = Store.Sounds;
        if (!_volume.Pending)
        {
            VolumeSlider.Value = Store.Volume;
        }
        EventsCard.IsEnabled = Store.Sounds;

        BackupLabel.Text = L10n.L("Back up, share");
        ExportButton.Content = L10n.L("Export pack…");
        ImportButton.Content = L10n.L("Import pack…");
        PackFooter.Text = L10n.L("Sounds are kept separately, changing the look won't lose them. Export a pack to take it to another computer.");

        var map = Store.SoundMap;
        foreach (var row in _rows)
        {
            row.Show(map.TryGetValue(row.EventId, out var sound) ? sound : string.Empty);
        }

        var pack = SoundPacks.Current(map);
        var custom = pack is null;
        if (custom && !_packs.Contains(ChoiceItems.CustomPack))
        {
            _packs.Add(ChoiceItems.CustomPack);
        }
        else if (!custom)
        {
            _packs.Remove(ChoiceItems.CustomPack);
        }
        PackBox.SelectedItem = custom ? ChoiceItems.CustomPack : _packs.First(p => p.Value == pack);
    }

    /// Applies a pack: its whole map is saved. The Custom entry is only a label, so choosing it does nothing.
    private void OnPackSelected()
    {
        if (Applying || PackBox.SelectedItem is not Choice { Value.Length: > 0 } choice)
        {
            return;
        }
        var pack = NotchTasks.Core.Sounds.SoundPacks.All.First(p => p.Name == choice.Value);
        Write("soundMap", MapJson(pack.Map));
    }

    /// One event's row: the title at the left, then the sound's picker and the play button at the right.
    private EventRow AddRow(Choice eventChoice)
    {
        var row = new EventRow(eventChoice.Value);
        var label = new TextBlock { Style = (Style)FindResource("RowLabel") };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Choice.Title)) { Source = eventChoice, Mode = BindingMode.OneWay });

        var picker = new ComboBox
        {
            ItemsSource = row.Items,
            ItemTemplate = (DataTemplate)FindResource("SoundItem"),
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(picker, Dock.Right);
        picker.SelectionChanged += (_, _) => OnEventSelected(row, picker);

        var play = new Button
        {
            // the Fluent play glyph, white and centred in the button (FluentGlyph centres it on its own box)
            Content = new FluentGlyph { Code = ModuleGlyphs.Get("play").Glyph, Filled = ModuleGlyphs.IsFilled("play"), Size = 12 }.ThemedBy("TextFillColorPrimaryBrush"),
            Width = 32,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The tooltip follows the language at once, as the other texts do (LocalizedStrings raises Item[] on each change).
        play.SetBinding(FrameworkElement.ToolTipProperty, new Binding("[Play]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay });
        DockPanel.SetDock(play, Dock.Right);
        play.Click += (_, _) => Sounds.PlayName(picker.SelectedItem is Choice chosen ? chosen.Value : string.Empty);

        var panel = new DockPanel
        {
            Style = (Style)FindResource("SettingRow"),
        };
        panel.Children.Add(play);
        panel.Children.Add(picker);
        panel.Children.Add(label);
        EventRows.Children.Add(panel);

        row.Picker = picker;
        row.Play = play;
        return row;
    }

    /// The event's picker changed. A rule cannot be chosen; "Choose your file…" opens the dialog; any other sound is saved.
    private void OnEventSelected(EventRow row, ComboBox picker)
    {
        if (Applying || picker.SelectedItem is not Choice chosen)
        {
            return;
        }
        if (chosen.IsSeparator)
        {
            ApplyStored();
            return;
        }
        if (chosen.Value == ChoiceItems.ChooseFileValue)
        {
            ChooseFile(row);
            return;
        }
        SaveSound(row.EventId, chosen.Value);
    }

    /// Asks for an audio file and keeps a copy of it for the event (Preferences.swift pickFile). A cancelled dialog, or a file
    /// that cannot be kept (too large, or not an audio type), leaves the picker at the event's sound.
    private void ChooseFile(EventRow row)
    {
        var dialog = new OpenFileDialog { Filter = $"{L10n.L("Audio files")}|*.wav;*.mp3;*.m4a;*.aac;*.wma" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            ApplyStored();
            return;
        }

        var path = PackFiles.Keep(dialog.FileName);
        if (path is null)
        {
            Message("NotchTasks", L10n.L("Couldn't use this sound"));
            ApplyStored();
            return;
        }
        SaveSound(row.EventId, path);
        Sounds.PlayName(path);
    }

    /// Saves one event's sound in the map, which is saved whole.
    private void SaveSound(string eventId, string sound)
    {
        var map = Store.SoundMap.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        map[eventId] = sound;
        Write("soundMap", MapJson(map));
    }

    /// Writes the pack to a file the user picks (Preferences.swift exportSounds).
    private async Task ExportPack()
    {
        var dialog = new SaveFileDialog
        {
            FileName = L10n.L("NotchTasks Sounds.notchsounds"),
            Filter = $"{L10n.L("NotchTasks sounds")}|*.notchsounds",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ExportPackTo(dialog.FileName);
        }
    }

    /// Writes the pack of the stored sounds and volume to `path`. The file work runs off the UI thread; a failed write is shown
    /// in a message box with the exception's message.
    internal async Task ExportPackTo(string path)
    {
        var files = PackFiles;
        var map = Store.SoundMap;  // read here, on the UI thread: the store is not read from the pool
        var volume = Store.Volume;
        var problem = await Task.Run(() => WritePack(files, map, volume, path));
        if (problem is not null)
        {
            Message(L10n.L("Couldn't export the sound pack"), problem);
        }
    }

    /// The pack's text written to `path`; returns the problem (the exception's message) when the write fails, else null.
    internal static string? WritePack(SoundPackFile files, IReadOnlyDictionary<string, string> map, double volume, string path)
    {
        try
        {
            File.WriteAllText(path, files.Export(map, volume), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// Picks a pack file and merges its sounds into the map (Preferences.swift importSounds).
    private async Task ImportPack()
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"{L10n.L("NotchTasks sounds")}|*.notchsounds;*.json",
            Title = L10n.L("Choose a sound pack file (.notchsounds)"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await ImportPackFrom(dialog.FileName);
        }
    }

    /// Reads a pack file off the UI thread, then applies it on the UI thread (ApplyImport).
    internal async Task ImportPackFrom(string path)
    {
        var files = PackFiles;
        var outcome = await Task.Run(() => ReadPack(files, path));
        ApplyImport(outcome);
    }

    /// The outcome of reading a pack: the sounds and volume when it can be used, else the problem to show.
    internal sealed record PackRead(SoundPackImport? Pack, string? Problem);

    /// Reads and parses a pack file. Runs on a pool thread: it does not touch the store or the controls. A disk or IO error
    /// is a problem of its own, with the exception's message.
    internal static PackRead ReadPack(SoundPackFile files, string path)
    {
        try
        {
            if (new FileInfo(path).Length >= SoundPackFile.MaxPackFile)
            {
                return new PackRead(null, L10n.L("This file isn't a NotchTasks sound pack."));
            }
            var result = files.Import(File.ReadAllText(path), SoundFiles.Names);
            return result.Error is null ? new PackRead(result, null) : new PackRead(null, result.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PackRead(null, e.Message);
        }
    }

    /// Applies a read pack on the UI thread: its sounds are merged into the map and its volume set. A problem is shown in a
    /// message box and changes nothing in the store.
    internal void ApplyImport(PackRead outcome)
    {
        if (outcome.Problem is not null || outcome.Pack is not { } pack)
        {
            Message(L10n.L("Couldn't import the sound pack"), outcome.Problem ?? string.Empty);
            return;
        }

        var map = Store.SoundMap.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (eventId, sound) in pack.Map)
        {
            map[eventId] = sound;
        }
        Write("soundMap", MapJson(map));
        if (pack.Volume is { } volume)
        {
            Write("volume", JsonValue.Create(Math.Round(volume, 2)));
        }
    }

    /// Shows a message box owned by the Control Panel window.
    private void ShowDialogMessage(string caption, string text) =>
        MessageBox.Show(Window.GetWindow(this), text, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    internal static JsonObject MapJson(IReadOnlyDictionary<string, string> map)
    {
        var json = new JsonObject();
        foreach (var (id, sound) in map)
        {
            json[id] = JsonValue.Create(sound);
        }
        return json;
    }

    /// The picker of one event, with its own list: a saved sound that is not a Mac name (the user's own file) is added to it,
    /// before the rule. Its play button is off for "" (no sound).
    private sealed class EventRow
    {
        public EventRow(string eventId)
        {
            EventId = eventId;
            Items = new ObservableCollection<Choice>(ChoiceItems.SoundPickerItems);
        }

        public string EventId { get; }

        public ObservableCollection<Choice> Items { get; }

        public ComboBox? Picker { get; set; }

        public Button? Play { get; set; }

        /// Selects the sound, adding an item for a file the list does not have.
        public void Show(string sound)
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (!ChoiceItems.SoundPickerItems.Contains(Items[i]) && Items[i].Value != sound)
                {
                    Items.RemoveAt(i);
                }
            }
            var item = Items.FirstOrDefault(c => c.Value == sound && !c.IsSeparator && c.Value != ChoiceItems.ChooseFileValue);
            if (item is null)
            {
                item = Choice.Literal(sound, Path.GetFileName(sound));
                var rule = Items.IndexOf(ChoiceItems.SoundSeparator);
                if (rule < 0)
                {
                    Items.Add(item);
                }
                else
                {
                    Items.Insert(rule, item);
                }
            }
            if (Picker is not null)
            {
                Picker.SelectedItem = item;
            }
            if (Play is not null)
            {
                Play.IsEnabled = sound.Length > 0;
            }
        }
    }
}
