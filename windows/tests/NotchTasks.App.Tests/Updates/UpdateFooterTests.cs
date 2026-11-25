using System.Windows;
using System.Windows.Controls;
using NotchTasks.ControlPanel;
using NotchTasks.Core;
using NotchTasks.Core.Updates;
using NotchTasks.Services.Updates;
using Xunit;

namespace NotchTasks.AppTests.Updates;

/// The Control Panel's update footer (the Mac's UpdateFooter): each state shows the Mac's text.
public sealed class UpdateFooterTests
{
    private static int Newer => UpdateService.CurrentBuild + 1;

    private static UpdateFooter FooterFor(UpdateService service)
    {
        var footer = new UpdateFooter();
        footer.Bind(service);
        return footer;
    }

    private static bool Visible(UpdateFooter footer, string name) =>
        Fixture.Named<FrameworkElement>(footer, name).Visibility == Visibility.Visible;

    [Fact]
    public void Current_ShowsTheVersionAsACheckNowButton()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(UpdateService.CurrentBuild), root.Folder);

            var footer = FooterFor(service);

            Assert.True(Visible(footer, "VersionButton"));
            Assert.False(Visible(footer, "CheckingText"));
            var button = Fixture.Named<Button>(footer, "VersionButton");
            Assert.Equal(ControlPanelWindow.AppVersionParts().Version, button.Content);
            Assert.Equal(L10n.L("Check now"), button.ToolTip);
        });
    }

    [Fact]
    public void Checking_ShowsCheckingText()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var gate = new TaskCompletionSource<bool>();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Held(gate.Task), root.Folder);
            var footer = FooterFor(service);

            service.Check(manual: true);

            Assert.True(Visible(footer, "CheckingText"));
            Assert.Equal(L10n.L("Checking…"), Fixture.Named<TextBlock>(footer, "CheckingText").Text);
            Assert.False(Visible(footer, "VersionButton"));
            gate.SetResult(true);
            Fixture.PumpUntil(() => !service.Checking);
        });
    }

    [Fact]
    public void UpToDate_ShowsAlreadyUpToDate()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(UpdateService.CurrentBuild), root.Folder);
            var footer = FooterFor(service);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.True(Visible(footer, "UpToDateText"));
            Assert.Equal(L10n.L("Already up to date"), Fixture.Named<TextBlock>(footer, "UpToDateText").Text);
            Assert.False(Visible(footer, "VersionButton"));
        });
    }

    [Fact]
    public void Offered_ShowsTheUpdateButtonAndTheNote()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(Newer, "3.0.1"), root.Folder);
            var footer = FooterFor(service);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.True(Visible(footer, "OfferPanel"));
            Assert.True(Visible(footer, "InstallButton"));
            Assert.Equal(L10n.L("Update {0}", "3.0.1"), Fixture.Named<TextBlock>(footer, "InstallLabel").Text);
            Assert.True(Visible(footer, "NoteText"));
            Assert.Equal("Faster sync", Fixture.Named<TextBlock>(footer, "NoteText").Text);
            Assert.False(Visible(footer, "ProgressPanel"));
            Assert.False(Visible(footer, "ErrorText"));
        });
    }

    [Fact]
    public void Installing_ShowsTheDownloadPercentInPlaceOfTheButton()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var hold = new TaskCompletionSource<bool>();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(Newer, hold: hold.Task), root.Folder);
            var footer = FooterFor(service);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            service.Install();

            Assert.False(Visible(footer, "InstallButton"));
            Assert.True(Visible(footer, "ProgressPanel"));
            Assert.Equal(L10n.L("Downloading… {0}%", 0), Fixture.Named<TextBlock>(footer, "ProgressText").Text);
            hold.SetResult(true);
            Fixture.PumpUntil(() => probe.Quits > 0);
        });
    }

    [Fact]
    public void Failed_ShowsTryAgainAndTheError()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe { Launch = _ => false };
            using var service = probe.Create(FakeUpdateServer.Offering(Newer, "3.0.1"), root.Folder);
            var footer = FooterFor(service);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);
            service.Install();
            Fixture.PumpUntil(() => !service.Installing);

            Assert.True(Visible(footer, "InstallButton"));
            Assert.Equal(L10n.L("Try again"), Fixture.Named<TextBlock>(footer, "InstallLabel").Text);
            Assert.True(Visible(footer, "ErrorText"));
            Assert.Equal(new UpdateFailure(UpdateFailureKind.Launch).Message, Fixture.Named<TextBlock>(footer, "ErrorText").Text);
            Assert.False(Visible(footer, "ProgressPanel"));
        });
    }
}
