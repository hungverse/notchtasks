using System.IO;
using System.Windows.Threading;
using NotchTasks.Core.Agents;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The agents source: its timer runs only with an enabled item, a tick's reads land in the module and the notch, and
/// stopping it clears the module.
public sealed class AgentsSourceTests
{
    private sealed class QuietAnnouncer : IAnnouncer
    {
        public void Announce(Toast t, bool notify = true)
        {
        }
    }

    private static AgentReaders Readers(TempRoot root, IProcessReader processes) => new(
        processes,
        new Fixture.NoSqlite(),
        new Fixture.NoHttp(),
        _ => null,
        Path.Combine(root.Folder, "home"),
        Path.Combine(root.Folder, "home", ".claude"));

    /// Ticks until the process is working: the first sample has no earlier one to compare with, so it reads 0 %; the second,
    /// after a short wait, reads its CPU time's growth.
    private static void TickUntilRunning(AgentsSource source, AgentsModule module)
    {
        Fixture.Call(source, "Tick");
        Fixture.PumpUntil(() => module.States.Count > 0);
        var until = DateTime.UtcNow.AddMilliseconds(50);
        Fixture.PumpUntil(() => DateTime.UtcNow >= until);
        Fixture.Call(source, "Tick");
        Fixture.PumpUntil(() => module.Running.Count > 0);
    }

    private static AgentsSource Source(SettingsStore settings, AgentReaders readers, AgentsModule module, Action publish) =>
        new(module, settings, readers, publish, _ => { });

    [Fact]
    public void AgentsSource_NoEnabledItems_NoTimer()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(Array.Empty<WatchItem>());
            var source = Source(settings, Readers(root, new Fixture.BusyProcesses("fakeagent.exe")), new AgentsModule(new QuietAnnouncer()), () => { });

            source.Start();

            Assert.False(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
            source.Stop();
        });
    }

    [Fact]
    public void AgentsSource_OnlyDisabledItems_NoTimer()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("fake", "Fake", WatchKind.Process, Process: "fakeagent", Enabled: false) });
            var source = Source(settings, Readers(root, new Fixture.BusyProcesses("fakeagent.exe")), new AgentsModule(new QuietAnnouncer()), () => { });

            source.Start();

            Assert.False(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
            source.Stop();
        });
    }

    [Fact]
    public void AgentsSource_FakeSnapshot_PublishesRunningAndChip()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("fake", "Fake", WatchKind.Process, Process: "fakeagent") });
            var module = new AgentsModule(new QuietAnnouncer());
            var published = 0;
            var source = Source(settings, Readers(root, new Fixture.BusyProcesses("fakeagent.exe")), module, () => published++);

            source.Start();
            Assert.True(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
            TickUntilRunning(source, module);

            Assert.Single(module.Running);
            Assert.Equal("fake", module.Running[0].Item.Id);
            Assert.True(published > 0);
            var snap = new NotchSnapshot(new[] { "agents" }, 0, null, null, Fixture.QuietSensors, null, null) { Running = module.Running };
            Assert.Equal(ChipKind.Agent, Assert.Single(ChipBoard.Chips(snap)).Kind);
            source.Stop();
        });
    }

    [Fact]
    public void AgentsSource_Stop_ClearsTheModule()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("fake", "Fake", WatchKind.Process, Process: "fakeagent") });
            var module = new AgentsModule(new QuietAnnouncer());
            var source = Source(settings, Readers(root, new Fixture.BusyProcesses("fakeagent.exe")), module, () => { });
            source.Start();
            TickUntilRunning(source, module);

            source.Stop();

            Assert.Empty(module.Running);
            Assert.Empty(module.States);
            Assert.False(Fixture.Field<DispatcherTimer>(source, "_timer").IsEnabled);
        });
    }

    [Fact]
    public void AgentsSource_Codex_QuestionsAreHandedToTheApp()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("codex", "Codex", WatchKind.Codex) });
            var seen = new List<IReadOnlyList<AgentAsk>>();
            var source = new AgentsSource(new AgentsModule(new QuietAnnouncer()), settings,
                Readers(root, new Fixture.BusyProcesses("codex.exe")), () => { }, list => seen.Add(list));

            source.Start();
            Fixture.Call(source, "Tick");
            Fixture.PumpUntil(() => seen.Count > 0);

            // no rollouts in the temp home: the list handed over is empty, and Stop hands over an empty list again
            Assert.Empty(seen[^1]);
            source.Stop();
            Assert.Empty(seen[^1]);
        });
    }

    /// A fixed process table that records the pids whose command line is read.
    private sealed class ArgvRecording : IProcessReader
    {
        private readonly ProcInfo[] _procs;

        public ArgvRecording(params ProcInfo[] procs) => _procs = procs;

        public List<int> Read { get; } = new();

        public ProcSnapshot Snapshot() => new(_procs);

        public string? CommandLine(ProcInfo p)
        {
            Read.Add(p.Pid);
            return "\"" + p.Name + "\" --flag";
        }

        public string? ImagePath(ProcInfo p) => null;
        public string? Cwd(ProcInfo p) => null;
    }

    [Fact]
    public void AgentsSource_AnAppItemAlone_ReadsNoCommandLine()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("app1", "Foo", WatchKind.App, BundleID: "foo.exe") });
            var processes = new ArgvRecording(new ProcInfo(10, 1, "foo.exe", 1, 0));
            var module = new AgentsModule(new QuietAnnouncer());
            var source = Source(settings, Readers(root, processes), module, () => { });

            source.Start();
            Fixture.Call(source, "Tick");
            Fixture.PumpUntil(() => module.States.Count > 0);
            source.Stop();

            Assert.Empty(processes.Read);
        });
    }

    [Fact]
    public void AgentsSource_AProcessItem_ReadsOnlyItsProcessesAndTheirChildren()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("p1", "Agent", WatchKind.Process, Process: "fakeagent.exe", ChildTasks: true) });
            var processes = new ArgvRecording(
                new ProcInfo(10, 1, "fakeagent.exe", 1, 0),
                new ProcInfo(11, 10, "helper.exe", 1, 0),
                new ProcInfo(12, 1, "unrelated.exe", 1, 0));
            var module = new AgentsModule(new QuietAnnouncer());
            var source = Source(settings, Readers(root, processes), module, () => { });

            source.Start();
            Fixture.Call(source, "Tick");
            Fixture.PumpUntil(() => module.States.Count > 0);
            source.Stop();

            Assert.Contains(10, processes.Read);
            Assert.Contains(11, processes.Read);  // a child task's title reads its words
            Assert.DoesNotContain(12, processes.Read);
        });
    }
}
