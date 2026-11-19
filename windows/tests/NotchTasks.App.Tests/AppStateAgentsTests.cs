using System.Text.Json.Nodes;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The agents through the app state: a Codex question is answered from the pill, it stays answered across an agents off and on,
/// and turning agents off drops its chips and toasts.
public sealed class AppStateAgentsTests
{
    private static AgentAsk CodexQuestion(string id) => new(
        id,
        0,
        "Session",
        "request_user_input",
        "Pick?",
        new[] { new AskQuestion("Pick?", null, new[] { "Yes", "No" }, false) },
        DateTimeOffset.UtcNow,
        "codex");

    [Fact]
    public void CodexAnswer_IsQueuedIntoItsThread()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("codex", "Codex", WatchKind.Codex) });
            var codex = new Fixture.RecordingCodex();
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Codex = codex });
            try
            {
                Fixture.Call(state, "SetCodexAsks", new List<AgentAsk> { CodexQuestion("codex|t1|c1") });
                Assert.Single(state.Asks);

                state.Answer("codex|t1|c1", new AskReply(ReplyKinds.Answers, new Dictionary<string, string> { ["Pick?"] = "Yes" }));

                Assert.Equal(("t1", "Yes"), Assert.Single(codex.Queued));
                Assert.Empty(state.Asks);
                Assert.Equal(0, codex.Fronted);
            }
            finally
            {
                state.Dispose();
            }
        });
    }

    [Fact]
    public void CodexPass_BringsCodexToTheFront_AndQueuesNothing()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("codex", "Codex", WatchKind.Codex) });
            var codex = new Fixture.RecordingCodex();
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Codex = codex });
            try
            {
                Fixture.Call(state, "SetCodexAsks", new List<AgentAsk> { CodexQuestion("codex|t1|c2") });

                state.Answer("codex|t1|c2", new AskReply(ReplyKinds.Pass, null));

                Assert.Equal(1, codex.Fronted);
                Assert.Empty(codex.Queued);
                Assert.Empty(state.Asks);
            }
            finally
            {
                state.Dispose();
            }
        });
    }

    [Fact]
    public void AnsweredCodexQuestion_StaysGone_AcrossAnAgentsOffAndOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("codex", "Codex", WatchKind.Codex) });
            var codex = new Fixture.RecordingCodex();
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings) with { Codex = codex });
            try
            {
                var question = new List<AgentAsk> { CodexQuestion("codex|t1|c3") };
                Fixture.Call(state, "SetCodexAsks", question);
                state.Answer("codex|t1|c3", new AskReply(ReplyKinds.Pass, null));
                Assert.Empty(state.Asks);

                settings.Set("disabled", JsonNode.Parse("[\"agents\"]"));
                Fixture.Flush();
                settings.Set("disabled", JsonNode.Parse("[]"));
                Fixture.Flush();
                // the scanner still lists the question: it must not come back
                Fixture.Call(state, "SetCodexAsks", question);

                Assert.Empty(state.Asks);
            }
            finally
            {
                state.Dispose();
            }
        });
    }

    [Fact]
    public void ItemsChangeThatRemovesTheLastItem_StopsTheAgentsTimer()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("fake", "Fake", WatchKind.Process, Process: "fakeagent") });
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings));
            try
            {
                var timer = Fixture.Field<System.Windows.Threading.DispatcherTimer>(Fixture.Field<AgentsSource>(state, "_agentsSource"), "_timer");
                Assert.True(timer.IsEnabled);

                settings.SetItems(Array.Empty<WatchItem>());
                Fixture.Flush();

                Assert.False(timer.IsEnabled);
            }
            finally
            {
                state.Dispose();
            }
        });
    }

    [Fact]
    public void TurningAgentsOff_DropsItsChipsAndToasts()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var settings = SettingsStore.Load(AppPaths.SettingsFile);
            settings.SetItems(new[] { new WatchItem("fake", "Fake", WatchKind.Process, Process: "fakeagent") });
            var state = new AppState(settings, new AskStore(AppPaths.AsksDir),
                Fixture.Inert(settings) with { Processes = new Fixture.BusyProcesses("fakeagent.exe") });
            try
            {
                var agents = Fixture.Field<AgentsSource>(state, "_agentsSource");
                Fixture.Call(agents, "Tick");
                Fixture.PumpUntil(() => state.Agents.States.Count > 0);
                var until = DateTime.UtcNow.AddMilliseconds(50);
                Fixture.PumpUntil(() => DateTime.UtcNow >= until);
                Fixture.Call(agents, "Tick");
                Fixture.PumpUntil(() => state.Agents.Running.Count > 0);
                Assert.Contains(ChipBoard.Chips(state.Snapshot), c => c.Kind == ChipKind.Agent);

                state.Announce(new Toast(Glyph: "check", Tint: "#30D158", Title: "Fake done", Subtitle: "s", Module: "agents"));
                Assert.NotNull(state.Toasts.Current);

                settings.Set("disabled", JsonNode.Parse("[\"agents\"]"));
                Fixture.Flush();

                Assert.Empty(state.Agents.Running);
                Assert.DoesNotContain(ChipBoard.Chips(state.Snapshot), c => c.Kind == ChipKind.Agent);
                Assert.Null(state.Toasts.Current);
            }
            finally
            {
                state.Dispose();
            }
        });
    }
}
