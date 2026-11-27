using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;

namespace NotchTasks.Demo;

/// The made-up content of the demo run: the agents, the build row and the toast texts. These are demo literals in English (the
/// recording is a fixed one), not localized texts, so they are kept here and nowhere else. The ask's own wording is the app's
/// (AskPresentation), which is the Mac's.
internal static class DemoContent
{
    public const string AskSession = "web-shop";
    public const string AskSummary = "git push origin main";
    public const string PushedTitle = "Pushed to origin/main · web-shop";
    public const string ChargeTitle = "AirPods Pro connected";
    public const string ChargeSubtitle = "L 100% · R 98% · Case 62%";
    public const string BuildTitle = "npm run build";

    /// The agents' reading: Claude and Codex are running. Both stay running for the whole loop.
    public static AgentInputs AgentsInput(WatchItem claude, WatchItem codex, long nowMs) => new(
        Items: new[] { claude, codex },
        AgentsOn: true,
        Snapshot: new ProcSnapshot(Array.Empty<ProcInfo>()),
        Cpu: new CpuMeter(),
        Argv: _ => Array.Empty<string>(),
        ClaudeJobs: new[] { new Job("web-shop", "web-shop", "Running · 4 min", Working: true) },
        CodexJobs: new[] { new Job("web-app", "web-app", "Running · 2 min", Working: true) },
        AntigravityJobs: Array.Empty<Job>(),
        LocalModels: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        DoneAfterSeconds: 30);

    /// The build row's reading: "npm run build" is running, or done once the timeline says so. No process, log or GitHub run is read.
    public static CiInputs BuildInput(bool done) => new(
        Snapshot: new ProcSnapshot(Array.Empty<ProcInfo>()),
        DevJobs: new[] { new Job("npm-build", BuildTitle, done ? "Done" : "Running", Working: !done) },
        FileJobs: Array.Empty<Job>(),
        GitLabLogs: null,
        Workers: Array.Empty<(int Pid, string? Exe, string? LogFile)>(),
        GitHubJobs: Array.Empty<Job>(),
        GitHubFinished: Array.Empty<GhRun>(),
        Cwd: _ => null,
        ChildJob: _ => new Job("", "", "", Working: false),
        DoneAfterSeconds: 30);
}
