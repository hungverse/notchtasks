using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Updates;

namespace NotchTasks.Services.Updates;

/// The app's self-update: the Mac's Updater. It checks the feed for a newer build, offers it, downloads its installer and starts
/// it silently. The first check runs 30 s after launch, then one every 6 h, and CheckIfDue runs one when the Control Panel opens.
/// Everything runs on the UI thread: the network waits are async and resume there, so the state needs no lock.
// shortcut: the installer is started by the shell (UseShellExecute), so a per-machine install raises UAC; no Authenticode check.
public sealed class UpdateService : IDisposable
{
    /// The state.json key of the build whose notice was shown: one notice per build.
    public const string AnnouncedKey = "updateAnnouncedBuild";

    /// The state.json key of a feed override. It is used only when UpdateRules.IsAllowedUrl accepts it.
    public const string FeedKey = "updateFeed";

    /// The installer's silent arguments: no wizard, no message boxes, no restart and no cancel button.
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL";

    /// The build this app runs: the assembly version (3.0.0 is 30000), as the feed numbers its builds.
    public static readonly int CurrentBuild = UpdateRules.BuildOf(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0));

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(60);
    private const string NoticeGlyph = "arrow.down.circle.fill";
    private const string NoticeTint = "#0A84FF";

    private readonly UpdateClient _client;
    private readonly Func<string> _stateFile;
    private readonly Action<Toast> _announce;
    private readonly Action _quit;
    private readonly Func<string, bool> _launch;
    private readonly Dispatcher _dispatcher;
    private readonly string _tempDir;
    private readonly Version _os = Environment.OSVersion.Version;
    private DispatcherTimer? _firstCheck;
    private DispatcherTimer? _timer;
    private DateTime _lastAttempt = DateTime.MinValue;
    private bool _disposed;

    /// client reads the feed; stateFile is state.json's path; announce shows the notice; quit ends the app after the installer
    /// starts; launch starts an installer file (StartInstaller). dispatcher runs the timers. tempDir receives the installer
    /// (null: the temp folder of this user).
    public UpdateService(UpdateClient client, Func<string> stateFile, Action<Toast> announce, Action quit,
        Func<string, bool> launch, Dispatcher dispatcher, string? tempDir = null)
    {
        _client = client;
        _stateFile = stateFile;
        _announce = announce;
        _quit = quit;
        _launch = launch;
        _dispatcher = dispatcher;
        _tempDir = tempDir ?? Path.GetTempPath();
    }

    /// Raised on the UI thread after each change the footer shows.
    public event Action? Changed;

    /// The newer build the feed offers, or null (the Mac's offered).
    public UpdateManifest? Offered { get; private set; }

    /// True while a check runs.
    public bool Checking { get; private set; }

    /// True from Install until the installer has started (or failed).
    public bool Installing { get; private set; }

    /// 0 to 1 while the installer downloads. Null while it verifies, or when no download runs.
    public double? Progress { get; private set; }

    /// The text of the last failure, or null. It is shown with Try again when an update is offered.
    public string? Error { get; private set; }

    /// True after a manual check that found no newer build: the footer says the app is up to date.
    public bool CheckedManually { get; private set; }

    /// The footer's state (UpdateRules.Footer).
    public FooterState FooterState => UpdateRules.Footer(Checking, Installing, Offered is not null, Error is not null, CheckedManually);

    /// Starts the timers: the first check 30 s from now, then one every 6 h. Runs on the UI thread.
    public void Start()
    {
        _firstCheck = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = FirstCheckDelay };
        _firstCheck.Tick += (_, _) =>
        {
            _firstCheck?.Stop();
            Check(manual: false);
        };
        _firstCheck.Start();
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = CheckInterval };
        _timer.Tick += (_, _) => Check(manual: false);
        _timer.Start();
    }

    /// Checks the feed now. A manual check reports its result in the footer (up to date, or the error); an automatic one that fails
    /// stays silent. Nothing runs while an install or another check does.
    public void Check(bool manual)
    {
        if (_disposed || Installing || Checking)
        {
            return;
        }
        _lastAttempt = DateTime.UtcNow;
        CheckedManually = false;
        Error = null;
        Checking = true;
        Changed?.Invoke();
        _ = RunCheckAsync(manual);
    }

    /// Checks when the last attempt was at least a minute ago (the Control Panel opens with this).
    public void CheckIfDue()
    {
        if (DateTime.UtcNow - _lastAttempt >= RetryGap)
        {
            Check(manual: false);
        }
    }

    /// Downloads the offered installer and starts it silently; the app then quits (quit). A failure is shown in the footer.
    public void Install()
    {
        var offer = Offered;
        if (_disposed || offer is null || Installing)
        {
            return;
        }
        Installing = true;
        Progress = 0;
        Error = null;
        Changed?.Invoke();
        _ = RunInstallAsync(offer);
    }

    /// Starts an installer file with the silent arguments. The shell runs it, so it may raise UAC. True when the process started.
    public static bool StartInstaller(string path)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(path, InstallerArguments) { UseShellExecute = true }) is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            ErrorLog.Write(e);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _firstCheck?.Stop();
        _timer?.Stop();
    }

    private async Task RunCheckAsync(bool manual)
    {
        try
        {
            var offer = await _client.CheckAsync(FeedUrl(), CurrentBuild, _os, CancellationToken.None);
            Offered = offer;
            if (offer is not null && UpdateRules.ShouldAnnounce(offer.Build, AnnouncedBuild()))
            {
                RememberAnnounced(offer.Build);
                _announce(NoticeFor(offer));
            }
            CheckedManually = manual && offer is null;
        }
        catch (Exception e)
        {
            // Any failure of the check counts: the feed's own errors, and the IO and JSON ones that can escape the client.
            if (manual)
            {
                Error = Message(e);
            }
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    private async Task RunInstallAsync(UpdateManifest offer)
    {
        try
        {
            // The progress comes on the UI thread (Progress captures it). At 100% the download is over and the checksum runs.
            var progress = new Progress<double>(value =>
            {
                Progress = value < 1 ? value : null;
                Changed?.Invoke();
            });
            var installer = await _client.DownloadAsync(offer, _tempDir, progress, CancellationToken.None);
            Progress = null;
            if (!TryLaunch(installer))
            {
                throw new UpdateFailure(UpdateFailureKind.Launch);
            }
            _quit();
        }
        catch (Exception e)
        {
            Progress = null;
            Installing = false;
            Error = Message(e);
        }
        Changed?.Invoke();
    }

    private bool TryLaunch(string installer)
    {
        try
        {
            return _launch(installer);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            return false;
        }
    }

    /// The footer's text for a failure: an update step's own message (the checksum's, say), else the download's.
    private static string Message(Exception e) =>
        e is UpdateFailure failure ? failure.Message : new UpdateFailure(UpdateFailureKind.Download).Message;

    private static Toast NoticeFor(UpdateManifest offer) => new(
        NoticeGlyph, NoticeTint, L10n.L("New version {0}", offer.Version), Event: null, Action: L10n.L("Control Panel"), Pane: "overview");

    /// The feed override from state.json when it is allowed; else the Windows feed.
    private string FeedUrl()
    {
        var stored = ReadText(FeedKey);
        return stored is not null && UpdateRules.IsAllowedUrl(stored) ? stored : UpdateClient.DefaultFeed;
    }

    private int AnnouncedBuild() => ReadState(AnnouncedKey) is JsonValue value && value.TryGetValue(out int build) ? build : 0;

    private void RememberAnnounced(int build)
    {
        try
        {
            StateJson.Set(_stateFile(), AnnouncedKey, JsonValue.Create(build));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The notice may show again next launch; the update itself is not affected.
            ErrorLog.Write(e);
        }
    }

    private string? ReadText(string key) => ReadState(key) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private JsonNode? ReadState(string key)
    {
        try
        {
            return StateJson.Get(_stateFile(), key);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable state file: the Windows feed and a notice for the build are the defaults.
            return null;
        }
    }
}
