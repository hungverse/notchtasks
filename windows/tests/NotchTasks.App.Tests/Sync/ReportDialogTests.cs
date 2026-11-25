using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.Core.Sync;
using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// Report a problem (Mac MacReportSheet): Send waits for a text, the code shows its last six characters, a failure says to try
/// again when online. The hub runs over fakes; nothing reaches the network.
[Collection("L10n")]
public sealed class ReportDialogTests
{
    [Fact]
    public void Report_SendDisabledUntilText_ShowsCodeSuffix6()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp();
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var dialog = new ReportDialog(bridge);
            Fixture.ShowOffScreen(dialog);
            try
            {
                var topics = Fixture.Named<ComboBox>(dialog, "TopicPicker");
                Assert.Equal(new object[] { "Pairing", "Permissions", "Fans & temperature", "Update", "Other" }, topics.Items.Cast<object>().ToArray());
                Assert.Equal("Other", topics.SelectedItem);

                var send = Fixture.Named<Button>(dialog, "SendButton");
                Assert.False(send.IsEnabled);

                Fixture.Named<TextBox>(dialog, "WhatBox").Text = "The phone does not pair";
                Assert.True(send.IsEnabled);

                send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Fixture.PumpUntil(() => Fixture.Named<TextBlock>(dialog, "ResultText").Text.Length > 0);

                Assert.Equal("Sent. Report code: 123456", Fixture.Named<TextBlock>(dialog, "ResultText").Text);
                Assert.False(send.IsEnabled);
                Assert.Equal(1, http.Count("/reports"));
            }
            finally
            {
                dialog.Close();
                PairingVisuals.Stop(hub);
            }
        });
    }

    [Fact]
    public void Report_Failure_SaysToTryAgainWhenOnline()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            PairingVisuals.InitEnglish();
            using var state = Fixture.NewState();
            var http = new PairingHttp { FailReports = true };
            var hub = PairingRig.MakeHub(http, paired: true);
            var bridge = new SyncBridge(state, hub);
            var dialog = new ReportDialog(bridge);
            Fixture.ShowOffScreen(dialog);
            try
            {
                Fixture.Named<TextBox>(dialog, "WhatBox").Text = "It is broken";
                var problem = Fixture.Named<TextBlock>(dialog, "ProblemText");
                Fixture.Named<Button>(dialog, "SendButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Fixture.PumpUntil(() => problem.Visibility == Visibility.Visible);

                Assert.Equal("Could not send the report. Try again when online.", problem.Text);
                Assert.Equal(Visibility.Collapsed, Fixture.Named<StackPanel>(dialog, "ResultRow").Visibility);
                Assert.True(Fixture.Named<Button>(dialog, "SendButton").IsEnabled);
            }
            finally
            {
                dialog.Close();
                PairingVisuals.Stop(hub);
            }
        });
    }
}
