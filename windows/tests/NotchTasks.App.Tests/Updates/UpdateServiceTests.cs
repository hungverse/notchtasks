using System.IO;
using System.Net;
using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Updates;
using NotchTasks.Services.Updates;
using Xunit;

namespace NotchTasks.AppTests.Updates;

/// The update service (the Mac's Updater) over a fake feed: the checks, the offer and its notice, the install and its failures.
public sealed class UpdateServiceTests
{
    private static int Newer => UpdateService.CurrentBuild + 1;

    [Fact]
    public void ManualCheck_WithNoNewerBuild_ReportsUpToDate()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(UpdateService.CurrentBuild), root.Folder);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal(FooterState.UpToDate, service.FooterState);
            Assert.Null(service.Offered);
            Assert.Empty(probe.Announced);
        });
    }

    [Fact]
    public void NewerBuild_IsOffered_AndAnnouncedOncePerBuild()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(Newer, "3.0.1"), root.Folder);

            service.Check(manual: false);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal("3.0.1", service.Offered?.Version);
            Assert.Equal(FooterState.Offered, service.FooterState);
            var toast = Assert.Single(probe.Announced);
            Assert.Equal(L10n.L("New version {0}", "3.0.1"), toast.Title);
            Assert.Null(toast.Event);
            Assert.Equal(L10n.L("Control Panel"), toast.Action);
            Assert.Equal("overview", toast.Pane);
            Assert.Equal("#0A84FF", toast.Tint);
            Assert.Equal(Newer, StateJson.Get(AppPaths.StateFile, UpdateService.AnnouncedKey)!.GetValue<int>());

            service.Check(manual: false);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Single(probe.Announced);
        });
    }

    [Fact]
    public void AutomaticCheckFailure_StaysSilent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Status(HttpStatusCode.InternalServerError), root.Folder);

            service.Check(manual: false);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Null(service.Error);
            Assert.Equal(FooterState.Current, service.FooterState);
        });
    }

    [Fact]
    public void ManualCheckFailure_ShowsTheDownloadError()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Status(HttpStatusCode.InternalServerError), root.Folder);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal(new UpdateFailure(UpdateFailureKind.Download).Message, service.Error);
            Assert.False(service.CheckedManually);
        });
    }

    [Fact]
    public void ManualCheck_UnexpectedException_ShowsTheDownloadError()
    {
        // An exception the client does not wrap (here a handler's own) must not escape the check or the footer.
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(new FakeUpdateServer(_ => throw new InvalidOperationException("boom")), root.Folder);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal(new UpdateFailure(UpdateFailureKind.Download).Message, service.Error);
        });
    }

    [Fact]
    public void CheckWhileChecking_DoesNotRequestAgain()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var gate = new TaskCompletionSource<bool>();
            var probe = new UpdateProbe();
            var server = FakeUpdateServer.Held(gate.Task);
            using var service = probe.Create(server, root.Folder);

            service.Check(manual: true);
            service.Check(manual: true);  // the first is still in flight
            gate.SetResult(true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Single(server.Requests);
        });
    }

    [Fact]
    public void CheckIfDue_TwiceWithinAMinute_ChecksOnce()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            var server = FakeUpdateServer.Offering(UpdateService.CurrentBuild);
            using var service = probe.Create(server, root.Folder);

            service.CheckIfDue();
            Fixture.PumpUntil(() => !service.Checking);
            service.CheckIfDue();
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Single(server.Requests);
        });
    }

    [Fact]
    public void FeedOverride_IsIgnored_WhenNotAllowed()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            StateJson.Set(AppPaths.StateFile, UpdateService.FeedKey, JsonValue.Create("http://example.com/x"));
            var probe = new UpdateProbe();
            var server = FakeUpdateServer.Offering(UpdateService.CurrentBuild);
            using var service = probe.Create(server, root.Folder);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal(UpdateClient.DefaultFeed, Assert.Single(server.Requests));
        });
    }

    [Fact]
    public void FeedOverride_IsUsed_WhenAllowed()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            const string local = "http://127.0.0.1:9/version-win.json";
            StateJson.Set(AppPaths.StateFile, UpdateService.FeedKey, JsonValue.Create(local));
            var probe = new UpdateProbe();
            var server = FakeUpdateServer.Offering(UpdateService.CurrentBuild);
            using var service = probe.Create(server, root.Folder);

            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            Assert.Equal(local, Assert.Single(server.Requests));
        });
    }

    [Fact]
    public void Install_StartsTheInstaller_ThenQuitsOnce()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(Newer), root.Folder);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            service.Install();
            Fixture.PumpUntil(() => probe.Quits > 0);

            Assert.Equal(1, probe.Quits);
            var installer = Assert.Single(probe.Launched);
            Assert.Equal(Path.Combine(root.Folder, $"NotchTasks-update-{Newer}.exe"), installer);
            Assert.True(File.Exists(installer));
            Assert.Null(service.Error);
        });
    }

    [Fact]
    public void Install_LaunchReturnsFalse_ShowsTheLaunchError_AndDoesNotQuit()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe { Launch = _ => false };
            using var service = probe.Create(FakeUpdateServer.Offering(Newer), root.Folder);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            service.Install();
            Fixture.PumpUntil(() => !service.Installing);

            Assert.Equal(new UpdateFailure(UpdateFailureKind.Launch).Message, service.Error);
            Assert.Equal(FooterState.Failed, service.FooterState);
            Assert.Equal(0, probe.Quits);
        });
    }

    [Fact]
    public void Install_LaunchThrows_ShowsTheLaunchError()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe { Launch = _ => throw new IOException("no shell") };
            using var service = probe.Create(FakeUpdateServer.Offering(Newer), root.Folder);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            service.Install();
            Fixture.PumpUntil(() => !service.Installing);

            Assert.Equal(new UpdateFailure(UpdateFailureKind.Launch).Message, service.Error);
            Assert.Equal(0, probe.Quits);
        });
    }

    [Fact]
    public void Install_ChecksumMismatch_ShowsTheChecksumError()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var probe = new UpdateProbe();
            using var service = probe.Create(FakeUpdateServer.Offering(Newer, sha256: new string('a', 64)), root.Folder);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);

            service.Install();
            Fixture.PumpUntil(() => !service.Installing);

            Assert.Equal(new UpdateFailure(UpdateFailureKind.Hash).Message, service.Error);
            Assert.Empty(probe.Launched);
            Assert.Equal(0, probe.Quits);
        });
    }

    [Fact]
    public void CheckWhileInstalling_DoesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var hold = new TaskCompletionSource<bool>();
            var probe = new UpdateProbe();
            var server = FakeUpdateServer.Offering(Newer, hold: hold.Task);
            using var service = probe.Create(server, root.Folder);
            service.Check(manual: true);
            Fixture.PumpUntil(() => !service.Checking);
            service.Install();
            Assert.True(service.Installing);
            var requests = server.Requests.Count;

            service.Check(manual: true);

            Assert.False(service.Checking);
            Assert.Equal(requests, server.Requests.Count);
            hold.SetResult(true);
            Fixture.PumpUntil(() => probe.Quits > 0);
        });
    }

    [Fact]
    public void InstallerArguments_AreTheSilentSet()
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL", UpdateService.InstallerArguments);
    }
}
