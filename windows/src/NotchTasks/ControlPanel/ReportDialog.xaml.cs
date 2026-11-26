using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Sync;
using NotchTasks.Services;
using NotchTasks.Services.Sync;

namespace NotchTasks.ControlPanel;

/// Report a problem (Mac MacReportSheet). The user says what happened and how to reach them; Send writes the report through the sync
/// hub (under the group, or under a fresh account when unpaired). Once sent, the window shows the report code (its last six characters)
/// and a Copy button; a failure says to try again when online. With sync off (no bridge, so no hub) the window still opens, says why
/// it cannot send, and keeps Send off. The window is modal, owned by the Control Panel.
public partial class ReportDialog : Window
{
    /// The issue types, in the Mac's order. The last one is the default. No "Fans & temperature": Windows has no fans pane to report on.
    private static readonly string[] TopicKeys = ["Pairing", "Permissions", "Update", "Other"];

    private readonly SyncBridge? _bridge;
    private string? _code;
    private bool _busy;

    public ReportDialog(SyncBridge? bridge)
    {
        _bridge = bridge;
        InitializeComponent();

        TopicLabel.Text = L10n.L("Issue type");
        WhatLabel.Text = L10n.L("What happened?");
        ContactLabel.Text = L10n.L("Email or other contact (so we can reply)");
        InfoExpander.Header = L10n.L("Information included");
        InfoText.Text = L10n.L("Your description and contact; anonymous device ID, group ID, pairing status, number of phones, app version, OS, device model, language, Pro status and transaction if available. Commands, source code, file paths, task titles and project names are not attached automatically.");
        CopyButton.Content = L10n.L("Copy");
        CloseButton.Content = L10n.L("Close");
        SendButton.Content = L10n.L("Send");

        TopicPicker.ItemsSource = TopicKeys.Select(key => L10n.L(key)).ToList();
        TopicPicker.SelectedIndex = TopicKeys.Length - 1;

        WhatBox.TextChanged += (_, _) => Render();
        SendButton.Click += (_, _) => _ = SendAsync();
        CloseButton.Click += (_, _) => Close();
        CopyButton.Click += (_, _) => CopyCode();
        if (_bridge is null)
        {
            ProblemText.Text = L10n.L("Reports go through sync, which is off. Turn sync on in Pairing to send one.");
            ProblemText.Visibility = Visibility.Visible;
        }
        Render();
    }

    /// Sends the report. Nothing is sent without text, without sync, and not twice. A failure is shown; the user may try again.
    private async Task SendAsync()
    {
        var bridge = _bridge;
        if (bridge is null || _busy || _code is not null || WhatBox.Text.Length == 0)
        {
            return;
        }
        _busy = true;
        ProblemText.Visibility = Visibility.Collapsed;
        Render();
        try
        {
            var topic = TopicKeys[Math.Max(0, TopicPicker.SelectedIndex)];
            _code = await bridge.Hub.SendReportAsync(L10n.L(topic), WhatBox.Text, ContactBox.Text, ReportInfoOf());
        }
        catch (Exception e)
        {
            ErrorLog.Write(new InvalidOperationException($"NotchTasks report: sending failed ({e.GetType().Name})"));
            ProblemText.Text = L10n.L("Could not send the report. Try again when online.");
            ProblemText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// The last six characters of the report's id: the code the user can quote.
    private static string Suffix(string code) => code.Length > 6 ? code[^6..] : code;

    private void CopyCode()
    {
        if (_code is null)
        {
            return;
        }
        try
        {
            Clipboard.SetText(Suffix(_code));
        }
        catch (ExternalException)
        {
            // The clipboard is busy with another program: the code stays on screen for the user to read.
        }
    }

    /// Send is enabled while there is text, sync is on and nothing is sent; the result row shows once the report is sent.
    private void Render()
    {
        SendButton.IsEnabled = _bridge is not null && !_busy && _code is null && WhatBox.Text.Length > 0;
        ResultRow.Visibility = _code is null ? Visibility.Collapsed : Visibility.Visible;
        if (_code is not null)
        {
            ResultText.Text = L10n.L("Sent. Report code: {0}", Suffix(_code));
        }
    }

    /// The version, build, Windows version and language this PC sends with the report.
    private static ReportInfo ReportInfoOf()
    {
        var (version, build) = ControlPanelWindow.AppVersionParts();
        return new ReportInfo(version, build, Environment.OSVersion.VersionString, L10n.Current);
    }
}
