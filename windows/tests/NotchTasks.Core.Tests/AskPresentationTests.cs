using NotchTasks.Core;
using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

// The texts go through L10n's static state (Init), so these tests share its collection and run one at a time.
[Collection("L10n")]
public sealed class AskPresentationTests
{
    private static AgentAsk Approval(string agent = "claude") => new(
        "a1", 1, "work", "Bash", "echo hi", [], DateTimeOffset.UnixEpoch, agent);

    private static AgentAsk Question(params AskQuestion[] questions) => new(
        "q1", 1, "work", "AskUserQuestion", "Which one?", questions, DateTimeOffset.UnixEpoch, "claude");

    private static AskQuestion Single(string text, params string[] options) =>
        new(text, null, options, false);

    private static AskQuestion Multi(string text, params string[] options) =>
        new(text, null, options, true);

    private static HashSet<string> Picks(params string[] labels) => new(labels, StringComparer.Ordinal);

    [Fact]
    public void Title_Approval()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.Equal("Claude needs your approval", AskPresentation.Title(Approval()));
    }

    [Fact]
    public void Title_Question()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.Equal("Claude is asking", AskPresentation.Title(Question(Single("Pick", "a", "b"))));
    }

    [Fact]
    public void Subtitle_ApprovalHasAction()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.Equal("work · Run command: echo hi", AskPresentation.Subtitle(Approval()));
    }

    [Fact]
    public void Waiting_FormatsMinutesSeconds()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.Equal("1:05", AskPresentation.Mmss(TimeSpan.FromSeconds(65)));
        Assert.Equal("Waiting 1:05", AskPresentation.Waiting(TimeSpan.FromSeconds(65)));
        Assert.Equal("0:00", AskPresentation.Mmss(TimeSpan.FromSeconds(-3)));
        Assert.Equal("1:01:01", AskPresentation.Mmss(TimeSpan.FromSeconds(3661)));
    }

    [Fact]
    public void SingleSingleChoice_TapSends()
    {
        L10n.Init(AppContext.BaseDirectory, "en");

        Assert.True(AskPresentation.TapSends([Single("Pick", "a", "b")]));
        Assert.False(AskPresentation.TapSends([Multi("Pick", "a", "b")]));
        Assert.False(AskPresentation.TapSends([Single("One", "a"), Single("Two", "b")]));
    }

    [Fact]
    public void MultiSelect_JoinsWithCommaSpace()
    {
        L10n.Init(AppContext.BaseDirectory, "en");
        var questions = new[] { Multi("Which?", "red", "green", "blue") };

        // Picked in another order, the answer lists them in the options' order.
        var answers = AskPresentation.Answers(questions, [Picks("blue", "red")]);

        Assert.Equal("red, blue", answers["Which?"]);
    }

    [Fact]
    public void Send_EnabledOnlyWhenAllAnswered()
    {
        L10n.Init(AppContext.BaseDirectory, "en");
        var questions = new[] { Single("One", "a", "b"), Multi("Two", "c", "d") };

        Assert.False(AskPresentation.Ready(questions, [Picks("a")]));
        Assert.False(AskPresentation.Ready(questions, [Picks(), Picks("c")]));
        Assert.True(AskPresentation.Ready(questions, [Picks("a"), Picks("c", "d")]));
    }
}
