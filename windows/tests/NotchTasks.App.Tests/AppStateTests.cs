using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class AppStateTests
{
    [Fact]
    public void AppState_ActiveModules_IsModulesMinusDisabled_InOrder()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"calls\",\"agents\",\"timer\",\"network\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[\"agents\"]"));

            var active = AppState.ActiveModules(state.Settings).ToArray();
            Assert.Equal(new[] { "calls", "timer", "network" }, active.Take(3));
            Assert.DoesNotContain("agents", active);
        });
    }

    [Fact]
    public void AppState_ActiveModules_FreshSettings_TurnsOnTheModulesTheModulesPaneShowsOn()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();

            // A fresh install: the Modules pane shows devices, calls, privacy and mirror on, so they must run (Mac: every module
            // is on except the disabled set).
            var active = AppState.ActiveModules(state.Settings);

            Assert.Contains("devices", active);
            Assert.Contains("calls", active);
            Assert.Contains("privacy", active);
            Assert.Contains("mirror", active);
            Assert.DoesNotContain("timer", active);  // the one module off by default (DefaultDisabled)
        });
    }

    [Fact]
    public void AppState_NewAsk_BecomesAgentAskToast_AndLeavesWithIt()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var ask = new AgentAsk("ask1", 1, "work", "Bash", "echo hi", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");

            Fixture.Call(state, "SyncAsks", new List<AgentAsk> { ask });

            var toast = state.Toasts.Current;
            Assert.NotNull(toast);
            Assert.Equal("ask1", toast!.AskId);
            Assert.Equal("agentAsk", toast.Event);
            Assert.True(toast.Sticky);
            Assert.Equal("agents", toast.Module);
            Assert.Equal("hand", toast.Glyph);
            Assert.Equal(1, state.Snapshot.AskCount);

            Fixture.Call(state, "SyncAsks", new List<AgentAsk>());

            Assert.Null(state.Toasts.Current);
            Assert.Equal(0, state.Snapshot.AskCount);
        });
    }

    [Fact]
    public void AppState_WaitingAsk_WithAgentsOff_GivesNoToast()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("disabled", JsonNode.Parse("[\"agents\"]"));
            var ask = new AgentAsk("ask4", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");

            Fixture.Call(state, "SyncAsks", new List<AgentAsk> { ask });

            Assert.Null(state.Toasts.Current);
            Assert.Equal(0, state.Snapshot.AskCount);
        });
    }

    [Fact]
    public void AppState_RepeatedSyncAsks_SameAsk_EnqueuesOnlyOneToast()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var ask = new AgentAsk("ask2", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");

            Fixture.Call(state, "SyncAsks", new List<AgentAsk> { ask });
            Fixture.Call(state, "SyncAsks", new List<AgentAsk> { ask });

            Assert.Equal("ask2", state.Toasts.Current?.AskId);
            Assert.Single(state.Asks);
            // Only one toast was queued: dismissing the shown one leaves the queue empty.
            state.Toasts.Dismiss(DateTimeOffset.Now);
            Assert.Null(state.Toasts.Current);
        });
    }
}
