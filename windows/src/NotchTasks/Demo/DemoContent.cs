using System.Windows.Media;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;

namespace NotchTasks.Demo;

/// The made-up content of the demo run: the agents, the build row, the now playing session, the limits and the toast texts. These are
/// demo literals in English (the recording is a fixed one), not localized texts, so they are kept here and nowhere else.
internal static class DemoContent
{
    /// The artwork key of the demo's session. Its image is drawn in code (Artwork), so no image file is read.
    public const string ArtKey = "demo-art";

    public const string PushedTitle = "Pushed to origin/main";
    public const string PushedSubtitle = "api-server · Claude";
    public const string ChargeTitle = "AirPods Pro · 80%";
    public const string ChargeSubtitle = "Connected";

    /// The agents' reading: Claude's api-server and Codex's web-app are running.
    public static AgentInputs AgentsInput(WatchItem claude, WatchItem codex, long nowMs) => new(
        Items: new[] { claude, codex },
        AgentsOn: true,
        Snapshot: new ProcSnapshot(Array.Empty<ProcInfo>()),
        Cpu: new CpuMeter(),
        Argv: _ => Array.Empty<string>(),
        ClaudeJobs: new[] { new Job("api-server", "api-server", "Running · 4 min", Working: true) },
        CodexJobs: new[] { new Job("web-app", "web-app", "Running · 2 min", Working: true) },
        AntigravityJobs: Array.Empty<Job>(),
        LocalModels: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        DoneAfterSeconds: 30);

    /// The build row's reading: one npm build is running. No process, log or GitHub run is read.
    public static CiInputs BuildInput(long nowMs) => new(
        Snapshot: new ProcSnapshot(Array.Empty<ProcInfo>()),
        DevJobs: new[] { new Job("npm-build", "npm run build", "Running", Working: true) },
        FileJobs: Array.Empty<Job>(),
        GitLabLogs: null,
        Workers: Array.Empty<(int Pid, string? Exe, string? LogFile)>(),
        GitHubJobs: Array.Empty<Job>(),
        GitHubFinished: Array.Empty<GhRun>(),
        Cwd: _ => null,
        ChildJob: _ => new Job("", "", "", Working: false),
        DoneAfterSeconds: 30);

    /// The Now Playing session: a song playing at 1:12 of 4:03.
    public static MediaSessionFact MediaSession(long nowMs) => new(
        Source: "Spotify.exe",
        Title: "Midnight City",
        Artist: "M83",
        Status: MediaStatus.Playing,
        Position: 72,
        Duration: 243,
        UpdatedAtMs: nowMs,
        CanSeek: true,
        CanNext: true,
        CanPrevious: true,
        CanToggle: true,
        ArtworkKey: ArtKey,
        IsCurrent: true);

    /// The limits: Claude at 42 % (5 h) and 18 % (7 d), Codex at 23 % and 9 %. Both are under the warning level.
    public static LimitInputs LimitsInput(DateTimeOffset now, double warn) => new(
        On: true,
        ClaudeEnabled: true,
        CodexEnabled: true,
        AgyEnabled: false,
        Claude: new ProviderLimits(
            "claude", "Claude", null,
            new[] { new LimitWindow("5h", 42, now.AddHours(2)), new LimitWindow("7d", 18, now.AddDays(3)) },
            now),
        Codex: new ProviderLimits(
            "codex", "Codex", null,
            new[] { new LimitWindow("5h", 23, now.AddHours(3)), new LimitWindow("7d", 9, now.AddDays(5)) },
            now),
        Antigravity: null,
        LimitWarn: warn);

    /// The artwork: a plain coloured square, drawn in code.
    public static ImageSource Artwork()
    {
        var drawing = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)),
            pen: null,
            geometry: new RectangleGeometry(new System.Windows.Rect(0, 0, 100, 100)));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
