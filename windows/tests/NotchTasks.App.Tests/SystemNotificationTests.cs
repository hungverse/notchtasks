using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Xml.Linq;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services.Notifications;
using Xunit;

namespace NotchTasks.AppTests;

/// The OS toasts. A toast the Mac posts as a notification goes to Windows too, when the systemNotifications setting is on;
/// the toast XML keeps its text as text; and the app's identity is the id of the installer's shortcut.
public sealed class SystemNotificationTests
{
    /// A notifier that keeps what it was asked to post, so no test shows a real Windows toast.
    private sealed class RecordingNotifier : INotifier
    {
        public List<Toast> Posted { get; } = new();

        public void Post(Toast t) => Posted.Add(t);
    }

    private static Toast Sample() => new("bell", "#FF9F0A", "Claude needs your approval", "NotchTasks · Bash: ls", Event: null);

    private static AppState StateWith(SettingsStore settings, INotifier notifier) =>
        new(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Notifier = notifier });

    [Fact]
    public void NotifyOn_WithSettingOn_PostsTheToast()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("systemNotifications", JsonNode.Parse("true"));
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);

            var toast = Sample();
            state.Announce(toast, notify: true);

            Assert.Same(toast, Assert.Single(notifier.Posted));
            Assert.Same(toast, state.Toasts.Current);  // the pill shows it as well
        });
    }

    [Fact]
    public void NotifyOn_WithSettingOff_DoesNotPost_ButTheToastStillShowsOnThePill()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("systemNotifications", JsonNode.Parse("false"));
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);

            var toast = Sample();
            state.Announce(toast, notify: true);

            Assert.Empty(notifier.Posted);
            Assert.Same(toast, state.Toasts.Current);
        });
    }

    [Fact]
    public void NotifyOff_DoesNotPost_EvenWithSettingOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("systemNotifications", JsonNode.Parse("true"));
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);

            state.Announce(Sample(), notify: false);

            Assert.Empty(notifier.Posted);
        });
    }

    [Fact]
    public void DefaultNotify_IsOn_SoAnAgentAskReachesWindows()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.Set("systemNotifications", JsonNode.Parse("true"));
            var notifier = new RecordingNotifier();
            using var state = StateWith(settings, notifier);

            state.Announce(Sample());

            Assert.Single(notifier.Posted);
        });
    }

    [Fact]
    public void Build_EscapesMarkupInTitleAndSubtitle_AndKeepsTheTextExact()
    {
        const string title = "<b>Done & \"dusted\" 'now'</b>";
        const string subtitle = "a < b & c > d";

        var doc = XDocument.Parse(ToastXml.Build(title, subtitle));  // throws if the markup leaked into the XML

        Assert.Equal(new[] { title, subtitle }, doc.Descendants("text").Select(e => e.Value).ToArray());
        Assert.Empty(doc.Descendants("b"));
    }

    [Fact]
    public void Build_DropsControlCharactersThatXmlCannotHold()
    {
        var doc = XDocument.Parse(ToastXml.Build("Build\u0001 done\u0008", "tab\tkept"));

        Assert.Equal(new[] { "Build done", "tab\tkept" }, doc.Descendants("text").Select(e => e.Value).ToArray());
    }

    [Fact]
    public void Build_WithoutSubtitle_HasOneTextLine()
    {
        var doc = XDocument.Parse(ToastXml.Build("Time's up", ""));

        Assert.Equal("Time's up", Assert.Single(doc.Descendants("text")).Value);
    }

    [Fact]
    public void Build_DropsUnpairedSurrogates_AndKeepsPairedOnes()
    {
        // A lone surrogate is not XML 1.0 text: XmlDocument rejects the whole toast. A pair (an emoji) is valid and stays.
        var doc = XDocument.Parse(ToastXml.Build("ok 😀 bad\uD800 \uDC00end", ""));

        Assert.Equal("ok 😀 bad end", Assert.Single(doc.Descendants("text")).Value);
    }

    [Fact]
    public async Task Post_ShowsTheEscapedToastXml_OffTheCaller()
    {
        var shown = new List<string>();
        var notifier = new SystemNotifier(xml => shown.Add(xml), _ => { });
        var toast = new Toast("bell", "#FF9F0A", "A < B & C", "\"quoted\"", Event: null);

        await notifier.PostAsync(toast);

        Assert.Equal(ToastXml.Build(toast.Title, toast.Subtitle), Assert.Single(shown));
    }

    [Fact]
    public async Task Post_AFailingShow_IsLoggedOnce_AndNeverThrows()
    {
        var logged = new List<Exception>();
        var notifier = new SystemNotifier(_ => throw new InvalidOperationException("no shortcut"), logged.Add);
        var toast = Sample();

        await notifier.PostAsync(toast);
        await notifier.PostAsync(toast);
        await notifier.PostAsync(toast);

        Assert.Single(logged);
        Assert.IsType<InvalidOperationException>(logged[0]);
    }

    [Fact]
    public void Build_IsSilent_BecauseTheAppPlaysItsOwnSounds()
    {
        var doc = XDocument.Parse(ToastXml.Build("Title", "Subtitle"));

        Assert.Equal("true", doc.Descendants("audio").Single().Attribute("silent")?.Value);
    }

    [Fact]
    public void UserModelId_IsTheInstallersShortcutId()
    {
        var iss = File.ReadAllText(FindInstallerScript());

        Assert.Contains($"AppUserModelID: \"{AppIdentity.UserModelId}\"", iss);
    }

    [Fact]
    public void ThirdPartyNotices_AreCopiedIntoTheStage_AndTheBuildChecksThem()
    {
        // The notices of every shipped component sit beside the installer; build.ps1 copies them into the stage's licenses
        // folder and stops when they are not there, as it does for the Fluent licence.
        var installer = Path.GetDirectoryName(FindInstallerScript())!;
        var build = File.ReadAllText(Path.Combine(installer, "build.ps1"));
        Assert.Contains("Copy-Item -LiteralPath $notices", build);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", build);
        Assert.Contains("Stage is missing the third-party notices", build);

        var notices = File.ReadAllText(Path.Combine(installer, "THIRD-PARTY-NOTICES.txt"));
        foreach (var component in new[]
                 {
                     "Fluent UI System Icons", "QRCoder", "H.NotifyIcon", "Microsoft.Data.Sqlite", "SQLitePCLRaw", "SQLite",
                 })
        {
            Assert.Contains(component, notices);
        }
    }

    /// The installer script sits beside the windows folder; the test output is somewhere below it.
    private static string FindInstallerScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "installer", "NotchTasks.iss");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("windows/installer/NotchTasks.iss was not found above the test output.");
    }
}
