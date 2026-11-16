using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using NotchTasks.Core;

namespace NotchTasks.ControlPanel;

/// The texts of the open language as one bindable source: Item[key] is L10n.L(key). It raises Item[] on every
/// L10n.Changed, so each {l:L ...} binding reads its text again.
public sealed class LocalizedStrings : INotifyPropertyChanged
{
    public static LocalizedStrings Instance { get; } = new();

    private LocalizedStrings() => L10n.Changed += OnChanged;

    public string this[string key] => L10n.L(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// {l:L Key} in XAML: the text of Key in the current language, updated when the language changes. Key is the English
/// text. In an attribute, write & as &amp;.
public sealed class LExtension : MarkupExtension
{
    public LExtension(string key) => Key = key;

    public string Key { get; }

    /// Returns the binding's BindingExpression, which is what a property setter accepts (a Binding object is not a value).
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
