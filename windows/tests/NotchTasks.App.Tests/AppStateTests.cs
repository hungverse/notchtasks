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

            Assert.Equal(new[] { "calls", "timer", "network" }, AppState.ActiveModules(state.Settings).ToArray());
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
}
