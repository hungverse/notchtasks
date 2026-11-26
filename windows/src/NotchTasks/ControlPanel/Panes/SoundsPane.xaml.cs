using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
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
        ExportButton.Click += (_, _) => ExportPack();
        ImportButton.Click += (_, _) => ImportPack();

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
    internal Action<string, string> Message { get; set; } = ShowMessage;

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
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(picker, Dock.Right);
        picker.SelectionChanged += (_, _) => OnEventSelected(row, picker);

        var play = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
            Width = 32,
            Height = 28,
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
        var dialog = new OpenFileDialog { Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma" };
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
    private void ExportPack()
    {
        var dialog = new SaveFileDialog
        {
            FileName = L10n.L("NotchTasks Sounds.notchsounds"),
            Filter = "NotchTasks sounds|*.notchsounds",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            ExportPackTo(dialog.FileName);
        }
    }

    /// Writes the pack of the stored sounds and volume to `path`. A failed write is shown in a message box.
    internal void ExportPackTo(string path)
    {
        try
        {
            File.WriteAllText(path, PackFiles.Export(Store.SoundMap, Store.Volume), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message(L10n.L("Couldn't export the sound pack"), e.Message);
        }
    }

    /// Picks a pack file and merges its sounds into the map (Preferences.swift importSounds).
    private void ImportPack()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "NotchTasks sounds|*.notchsounds;*.json",
            Title = L10n.L("Choose a sound pack file (.notchsounds)"),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            ImportPackFrom(dialog.FileName);
        }
    }

    /// Uses the sounds and volume of a pack file: the sounds are merged into the map, the volume is set. A file that is not
    /// a pack, or has nothing usable, is shown in a message box and changes nothing.
    internal void ImportPackFrom(string path)
    {
        string? error = null;
        try
        {
            if (new FileInfo(path).Length >= SoundPackFile.MaxPackFile)
            {
                error = L10n.L("This file isn't a NotchTasks sound pack.");
            }
            else
            {
                var result = PackFiles.Import(File.ReadAllText(path), SoundFiles.Names);
                error = result.Error;
                if (error is null)
                {
                    var map = Store.SoundMap.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    foreach (var (eventId, sound) in result.Map)
                    {
                        map[eventId] = sound;
                    }
                    Write("soundMap", MapJson(map));
                    if (result.Volume is { } volume)
                    {
                        Write("volume", JsonValue.Create(Math.Round(volume, 2)));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = L10n.L("This file isn't a NotchTasks sound pack.");
        }

        if (error is not null)
        {
            Message(L10n.L("Couldn't import the sound pack"), error);
        }
    }

    private static void ShowMessage(string caption, string text) =>
        MessageBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static JsonObject MapJson(IReadOnlyDictionary<string, string> map)
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
