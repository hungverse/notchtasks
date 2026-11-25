using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Updates;
using NotchTasks.Services.Updates;

namespace NotchTasks.ControlPanel;

/// The app's version and update line at the foot of the Control Panel's sidebar (the Mac's UpdateFooter). It shows what
/// UpdateService.FooterState says: the version (a button that checks now), "Checking…", "Already up to date", or the offer with its
/// install button, progress, note and error. It re-reads the service on each change and on each language change.
public partial class UpdateFooter : UserControl
{
    private UpdateService? _service;

    public UpdateFooter()
    {
        InitializeComponent();
        // The language follows only while the footer is on screen: L10n.Init can run on any thread, and a footer that is not
        // shown has nothing to re-read.
        Loaded += (_, _) => L10n.Changed += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            L10n.Changed -= OnLanguageChanged;
            if (_service is not null)
            {
                _service.Changed -= Render;
            }
        };
    }

    /// Shows the service's state and follows it until the footer is unloaded. Called once, when the Control Panel is made.
    public void Bind(UpdateService service)
    {
        if (_service is not null)
        {
            _service.Changed -= Render;
        }
        _service = service;
        service.Changed += Render;
        Render();
    }

    /// The language changed, on whatever thread changed it: the texts are read again on the UI thread.
    private void OnLanguageChanged()
    {
        if (CheckAccess())
        {
            Render();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(Render));
        }
    }

    /// The rows the footer state shows: one of the checking, version and up-to-date rows, or the offer's rows.
    private void Render()
    {
        if (_service is not { } service)
        {
            return;
        }
        var state = service.FooterState;
        var offer = service.Offered;
        Show(CheckingText, state == FooterState.Checking);
        if (state == FooterState.Checking)
        {
            CheckingText.Text = L10n.L("Checking…");
        }

        Show(VersionButton, state == FooterState.Current);
        VersionButton.Content = ControlPanelWindow.AppVersionParts().Version;
        VersionButton.ToolTip = L10n.L("Check now");

        Show(UpToDateText, state == FooterState.UpToDate);
        UpToDateText.Text = L10n.L("Already up to date");

        var offering = state is FooterState.Offered or FooterState.Failed;
        Show(OfferPanel, offering);
        if (!offering)
        {
            return;
        }
        RenderInstall(service, offer);
    }

    private void RenderInstall(UpdateService service, UpdateManifest? offer)
    {
        Show(InstallButton, !service.Installing);
        InstallLabel.Text = service.Error is null
            ? L10n.L("Update {0}", offer?.Version ?? string.Empty)
            : L10n.L("Try again");

        Show(ProgressPanel, service.Installing);
        if (service.Progress is { } fraction)
        {
            ProgressText.Text = L10n.L("Downloading… {0}%", (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero));
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = fraction;
        }
        else
        {
            ProgressText.Text = L10n.L("Verifying…");
            ProgressBar.IsIndeterminate = true;
        }

        var note = offer?.NoteFor(L10n.Current);
        Show(NoteText, note is not null);
        NoteText.Text = note ?? string.Empty;
        NoteText.ToolTip = note;

        Show(ErrorText, service.Error is not null);
        ErrorText.Text = service.Error ?? string.Empty;
        ErrorText.ToolTip = service.Error;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e) => _service?.Check(manual: true);

    private void OnInstallClick(object sender, RoutedEventArgs e) => _service?.Install();

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
