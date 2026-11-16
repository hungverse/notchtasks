using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sounds;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel.Panes;

/// The Sound pane (Preferences.swift sounds): the on/off switch, the volume, the pack that sets every event's sound, and one
/// sound per event with a button that plays it.
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

    protected override void Apply()
    {
        SoundOn.IsChecked = Store.Sounds;
        if (!_volume.Pending)
        {
            VolumeSlider.Value = Store.Volume;
        }
        EventsCard.IsEnabled = Store.Sounds;

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
            DisplayMemberPath = nameof(Choice.Title),
            Width = 200,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(picker, Dock.Right);
        picker.SelectionChanged += (_, _) => OnEventSelected(row, picker);

        var play = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
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
        return row;
    }

    /// The event's sound was changed: the whole map is saved, with this event's new sound.
    private void OnEventSelected(EventRow row, ComboBox picker)
    {
        if (Applying || picker.SelectedItem is not Choice chosen)
        {
            return;
        }
        var map = Store.SoundMap.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        map[row.EventId] = chosen.Value;
        Write("soundMap", MapJson(map));
    }

    private static JsonObject MapJson(IReadOnlyDictionary<string, string> map)
    {
        var json = new JsonObject();
        foreach (var (id, sound) in map)
        {
            json[id] = JsonValue.Create(sound);
        }
        return json;
    }

    /// The picker of one event, with its own list: a saved sound that is not a Mac name (the user's own file) is added to it.
    private sealed class EventRow
    {
        public EventRow(string eventId)
        {
            EventId = eventId;
            Items = new ObservableCollection<Choice>(ChoiceItems.SoundNames);
        }

        public string EventId { get; }

        public ObservableCollection<Choice> Items { get; }

        public ComboBox? Picker { get; set; }

        /// Selects the sound, adding an item for a file the list does not have.
        public void Show(string sound)
        {
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (!ChoiceItems.SoundNames.Contains(Items[i]) && Items[i].Value != sound)
                {
                    Items.RemoveAt(i);
                }
            }
            var item = Items.FirstOrDefault(c => c.Value == sound);
            if (item is null)
            {
                item = Choice.Literal(sound, Path.GetFileName(sound));
                Items.Add(item);
            }
            if (Picker is not null)
            {
                Picker.SelectedItem = item;
            }
        }
    }
}
