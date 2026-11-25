using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Sync;
using NotchTasks.Services;
using NotchTasks.Services.Sync;

namespace NotchTasks.ControlPanel.Panes;

/// The Pairing pane (Mac PhonePane, Sync.swift): the Pro line, the steps and the code to pair a phone, the connection switches
/// once a phone is paired, the phones, Unpair all, and Report a problem. It shows the bridge's state and calls the hub; nothing
/// here blocks on the network. While the pane is shown it polls the phones every 4 s; the code is shown only while it is shown
/// (the code is the key), and the poll stops when it is hidden.
public partial class PairingPane : UserControl
{
    private const string PhoneGlyph = "";
    private const string WarningGlyph = "";

    /// The lifetime Pro: an "until" at or past this (ms since 1970) is shown as lifetime (Mac PhonePane.status).
    private const double LifetimeMs = 30_000_000_000_000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);
    private static readonly Brush Orange = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    private static readonly Brush Destructive = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));

    private readonly SyncBridge? _bridge;
    private readonly DispatcherTimer _poll;
    private bool _busy;
    private bool _showCode;
    private string? _pairUrl;

    public PairingPane(SyncBridge? bridge)
    {
        _bridge = bridge;
        InitializeComponent();
        _poll = new DispatcherTimer { Interval = PollInterval };
        _poll.Tick += (_, _) => OnPollTick();

        ProblemGlyph.Text = WarningGlyph;
        UnpairAllButton.Foreground = Destructive;
        ApplyTexts();

        PairButton.Click += (_, _) => _ = ShowCodeAsync();
        AnotherButton.Click += (_, _) => _ = ShowCodeAsync();
        CopyButton.Click += (_, _) => CopyCode();
        HideButton.Click += (_, _) => HideCode();
        ReportButton.Click += (_, _) => ShowReport();
        UnpairAllButton.Click += (_, _) => _ = UnpairAllAsync();
        LanSwitch.Click += (_, _) => _ = SetLanAsync(LanSwitch.IsChecked == true);
        OnlyAwaySwitch.Click += (_, _) => SetOnlyAway(OnlyAwaySwitch.IsChecked == true);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// The Pro line (Mac PhonePane.status): no Pro, a lifetime Pro, or the date the Pro runs until (`until` in ms since 1970).
    internal static string ProStatus(double? until, DateTimeOffset now)
    {
        if (until is not { } ms || ms <= now.ToUnixTimeMilliseconds())
        {
            return L10n.L("No Pro: one phone, basic notifications");
        }
        if (ms >= LifetimeMs)
        {
            return L10n.L("Pro lifetime");
        }
        var date = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().DateTime;
        return L10n.L("Pro until {0}", date.ToString("d", CultureInfo.CurrentCulture));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// Every text of the pane in the current language. Run when the pane is made and when the language changes.
    private void ApplyTexts()
    {
        UnavailableText.Text = L10n.L("Pairing is not available");
        IntroText.Text = L10n.L("Answer agents and see what's running right on your phone. Data is encrypted between your devices.");
        Step1.Text = L10n.L("1. Install NotchTasks on your phone");
        Step2.Text = L10n.L("2. Click Pair device");
        Step3.Text = L10n.L("3. Scan the QR code with the app or Camera");
        CodeInstructions.Text = L10n.L("Open NotchTasks on your phone → Pair with computer, then scan this code.");
        CodeNote.Text = L10n.L("This code only works for your devices.");
        CopyButton.Content = L10n.L("Copy code");
        HideButton.Content = L10n.L("Hide code");
        AnotherButton.Content = L10n.L("Pair another device…");
        ReportButton.Content = L10n.L("Report a problem");
        ConnectingLabel.Text = L10n.L("Connecting…");
        LanSwitch.Content = L10n.L("Connect directly on the same Wi‑Fi");
        OnlyAwaySwitch.Content = L10n.L("Only alert my phone when I'm away");
        SettingsNote.Text = L10n.L("On the same Wi‑Fi your phone gets updates instantly; elsewhere, over the Internet. While you're using this computer, your phone stays quiet.");
        PairedCaps.Text = L10n.L("Paired").ToUpper(CultureInfo.CurrentUICulture);
        UnpairAllButton.Content = L10n.L("Unpair all");
        PairedNote.Text = L10n.L("Every device will disconnect from this computer. Pairing again uses a new code.");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_bridge is null)
        {
            Render();
            return;
        }
        _bridge.Changed += OnBridgeChanged;
        L10n.Changed += OnLanguageChanged;
        _poll.Start();
        Render();
        _ = RefreshPhonesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_bridge is not null)
        {
            _bridge.Changed -= OnBridgeChanged;
            L10n.Changed -= OnLanguageChanged;
        }
        _poll.Stop();
        // The code is the key: it is not left on screen once the pane is hidden.
        HideCode();
    }

    private void OnBridgeChanged() => Render();

    private void OnLanguageChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyTexts();
            Render();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTexts();
                Render();
            }));
        }
    }

    /// The 4 s poll: the phones are read again while the pane is shown.
    private void OnPollTick()
    {
        if (IsLoaded)
        {
            _ = RefreshPhonesAsync();
        }
    }

    /// Reads the phones. A paired group with no phone yet shows its code, as the Mac does: the first pairing needs the QR at once.
    private async Task RefreshPhonesAsync()
    {
        if (_bridge is null || _bridge.Hub.Group is null)
        {
            return;
        }
        try
        {
            await _bridge.Hub.LoadPhonesAsync();
        }
        catch (Exception e)
        {
            LogFailure("reading the phones", e);
        }
        if (IsLoaded && _pairUrl is null && !_busy && _bridge.State.Paired && _bridge.State.Phones.Count == 0)
        {
            await ShowCodeAsync();
        }
    }

    /// Creates the group if there is none and shows its code: the QR, and the pair URL that Copy code puts on the clipboard.
    private async Task ShowCodeAsync()
    {
        if (_bridge is null || _busy)
        {
            return;
        }
        _busy = true;
        Render();
        try
        {
            var (_, url) = await _bridge.Hub.PairAsync();
            if (IsLoaded)
            {
                _pairUrl = url;
                QrCode.Source = QrImage.Create(url);
                _showCode = true;
            }
        }
        catch (Exception e)
        {
            LogFailure("pairing", e);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// Puts the pair URL on the clipboard, and nothing else (the code is the key).
    private void CopyCode()
    {
        if (_pairUrl is null)
        {
            return;
        }
        try
        {
            Clipboard.SetText(_pairUrl);
        }
        catch (ExternalException)
        {
            // The clipboard is busy with another program: the user can press Copy code again.
        }
    }

    private void HideCode()
    {
        _showCode = false;
        _pairUrl = null;
        QrCode.Source = null;
        Render();
    }

    private async Task UnpairAllAsync()
    {
        if (_bridge is null || _busy)
        {
            return;
        }
        _busy = true;
        Render();
        try
        {
            await _bridge.Hub.UnpairAllAsync();
            _showCode = false;
            _pairUrl = null;
            QrCode.Source = null;
        }
        catch (Exception e)
        {
            LogFailure("unpairing", e);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private async Task SetLanAsync(bool on)
    {
        if (_bridge is null)
        {
            return;
        }
        try
        {
            await _bridge.SetLan(on);
        }
        catch (Exception e)
        {
            LogFailure("the Wi-Fi switch", e);
        }
        Render();
    }

    private void SetOnlyAway(bool on)
    {
        if (_bridge is null)
        {
            return;
        }
        try
        {
            _bridge.SetOnlyAway(on);
        }
        catch (Exception e)
        {
            LogFailure("the away switch", e);
        }
        Render();
    }

    private void ShowReport()
    {
        if (_bridge is null)
        {
            return;
        }
        var dialog = new ReportDialog(_bridge) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    /// Draws the pane from the bridge's state and the pane's own parts (busy, the code). Only what applies is shown.
    private void Render()
    {
        if (_bridge is null)
        {
            UnavailableCard.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Collapsed;
            return;
        }
        UnavailableCard.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;

        var state = _bridge.State;
        var noPhones = state.Phones.Count == 0;
        // A code that is on screen means the group exists, even before the bridge has caught up with the hub's state.
        var hasGroup = state.Paired || _pairUrl is not null;

        ProText.Text = ProStatus(state.ProUntil, DateTimeOffset.UtcNow);
        var note = state.ProNote is { } info ? SnapshotBuilder.ProNoteText(info.Why) : null;
        ProNoteText.Text = note ?? string.Empty;
        ProNoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;

        // The steps and the code: with no phone, the steps and the code of the group (or the button that creates it).
        StepsPanel.Visibility = noPhones ? Visibility.Visible : Visibility.Collapsed;
        var showCode = _pairUrl is not null && (noPhones || _showCode);
        CodePanel.Visibility = showCode ? Visibility.Visible : Visibility.Collapsed;
        CodeInstructions.Visibility = noPhones ? Visibility.Collapsed : Visibility.Visible;
        HideButton.Visibility = noPhones ? Visibility.Collapsed : Visibility.Visible;

        PairButton.Content = _busy ? L10n.L("Creating code…") : L10n.L("Pair device…");
        PairButton.IsEnabled = !_busy;
        PairButton.Visibility = hasGroup ? Visibility.Collapsed : Visibility.Visible;
        AnotherButton.IsEnabled = !_busy;
        AnotherButton.Visibility = hasGroup && !noPhones && !_showCode ? Visibility.Visible : Visibility.Collapsed;

        var problem = state.Problem;
        ProblemText.Text = problem ?? string.Empty;
        ProblemRow.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;

        // The connection, the phones and Unpair all: once a phone is paired.
        var connected = state.Paired && !noPhones;
        ConnectionCard.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        SettingsNote.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PairedCaps.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PairedCard.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        PairedNote.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        if (connected)
        {
            RenderConnected(state);
        }
    }

    /// The switches, the connection row, the phones (sorted by name, Pro on the holders) and Unpair all.
    private void RenderConnected(SyncState state)
    {
        LanSwitch.IsChecked = state.Lan;
        OnlyAwaySwitch.IsChecked = state.OnlyAway;
        ConnectionValue.Text = state.LanPhones > 0
            ? L10n.L("{0} devices over Wi‑Fi", state.LanPhones)
            : L10n.L("Over the Internet");

        PhoneRows.Children.Clear();
        foreach (var (uid, name) in state.Phones.OrderBy(entry => entry.Value, StringComparer.Ordinal))
        {
            PhoneRows.Children.Add(PhoneRow(name, state.ProHolders.Contains(uid)));
        }
        UnpairAllButton.IsEnabled = !_busy;
    }

    /// One phone: its glyph and name, and "Pro" in orange when it holds the group's Pro.
    private static DockPanel PhoneRow(string name, bool holdsPro)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        if (holdsPro)
        {
            var pro = new TextBlock
            {
                Text = L10n.L("Pro"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Orange,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            DockPanel.SetDock(pro, Dock.Right);
            row.Children.Add(pro);
        }
        row.Children.Add(new TextBlock
        {
            Text = PhoneGlyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// A failed call is logged by its step and its exception type only: the pairing code and the keys never go to the log.
    private static void LogFailure(string step, Exception e) =>
        ErrorLog.Write(new InvalidOperationException($"NotchTasks Pairing pane: {step} failed ({e.GetType().Name})"));
}
