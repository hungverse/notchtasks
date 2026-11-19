using System.IO;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The limits source: its timer runs only with Claude, Codex or Antigravity on, the Claude CLI is given up after a while and
/// never runs twice at once, and turning the modules off clears the state.
public sealed class LimitsSourceTests
{
    private sealed class QuietAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    /// A CLI that counts its calls and never finishes.
    private sealed class HangingCli : IClaudeCli
    {
        public int Calls { get; private set; }

        public Task<string?> UsageAsync(CancellationToken ct)
        {
            Calls++;
            return new TaskCompletionSource<string?>().Task;
        }
    }

    private static readonly WatchItem ClaudeItem = new("claude", "Claude", WatchKind.Claude);

    /// A temp home whose statusline says the 5 h window is 42 % used, and the readers that use it.
    private static LimitReaders Readers(TempRoot root, IClaudeCli cli)
    {
        var home = Path.Combine(root.Folder, "home");
        var claudeDir = Path.Combine(home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var resets = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        File.WriteAllText(Path.Combine(claudeDir, "notchtasks-status.json"),
            $"{{\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":42,\"resets_at\":{resets}}}}}}}");
        return new LimitReaders(new Fixture.NoHttp(), cli, home, claudeDir);
    }

    private static (LimitsSource Source, LimitsModule Module) Source(TempRoot root, SettingsStore settings, IClaudeCli cli,
        Action publish, TimeSpan giveUp)
    {
        var module = new LimitsModule(new QuietAnnouncer());
        var readers = Readers(root, cli);
        var agents = new AgentsSource(new AgentsModule(new QuietAnnouncer()), settings,
            new AgentReaders(new Fixture.NoProcesses(), new Fixture.NoSqlite(), new Fixture.NoHttp(), _ => null, readers.Home, readers.ClaudeDir),
            () => { }, _ => { });
        return (new LimitsSource(module, settings, readers, agents, publish, giveUp), module);
    }

    [Fact]
    public void LimitsSource_NoEnabledItems_NoTimer()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("timer", "Timer", WatchKind.Process, Process: "timer", Enabled: true) });
            var (source, _) = Source(root, settings, new HangingCli(), () => { }, TimeSpan.FromMilliseconds(50));

            source.Start();

            Assert.False(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
            source.Stop();
        });
    }

    [Fact]
    public void LimitsSource_ClaudeCliHangs_GivesUpAndUsesStatusline()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { ClaudeItem });
            var cli = new HangingCli();
            var (source, module) = Source(root, settings, cli, () => { }, TimeSpan.FromMilliseconds(50));

            source.Start();  // the CLI runs once at start; it never answers
            Fixture.PumpUntil(() => module.Limits.Count > 0);

            Assert.Equal(1, cli.Calls);
            var claude = Assert.Single(module.Limits);
            Assert.Equal("claude", claude.Kind);
            Assert.Equal(42, claude.Windows.Single(w => w.Label == "5h").Used);
            source.Stop();
        });
    }

    [Fact]
    public void LimitsSource_OneCliAtATime()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { ClaudeItem });
            var cli = new HangingCli();
            var (source, module) = Source(root, settings, cli, () => { }, TimeSpan.FromMilliseconds(50));
            source.Start();
            Fixture.PumpUntil(() => module.Limits.Count > 0);
            source.Stop();

            // a restart asks again for the CLI, but the first call still hangs: no second one starts
            source.Start();
            Fixture.Call(source, "Tick");
            Fixture.Call(source, "Tick");
            Fixture.PumpUntil(() => module.Limits.Count > 0);

            Assert.Equal(1, cli.Calls);
            source.Stop();
        });
    }

    [Fact]
    public void ModulesOff_StopEverything_ClearSnapshot()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { ClaudeItem });
            var (source, module) = Source(root, settings, new HangingCli(), () => { }, TimeSpan.FromMilliseconds(50));
            source.Start();
            Fixture.PumpUntil(() => module.Limits.Count > 0);

            source.Stop();

            Assert.False(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
            Assert.Empty(module.Limits);
        });
    }

    [Fact]
    public void Inert_TouchesNoRealReader()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { ClaudeItem });

            var options = Fixture.Inert(settings);
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), options);
            try
            {
                Assert.IsType<Fixture.NoClaudeCli>(options.ClaudeCli);
                Assert.IsType<Fixture.NoProcesses>(options.Processes);
                Assert.Empty(state.LimitsModule.Limits);
                Assert.Empty(state.Agents.States);
            }
            finally
            {
                state.Dispose();
            }
        });
    }
}
