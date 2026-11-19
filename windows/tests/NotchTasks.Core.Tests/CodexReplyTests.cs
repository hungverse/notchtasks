using NotchTasks.Core.Asks;
using Xunit;

namespace NotchTasks.Core.Tests;

public class CodexReplyTests
{
    private static AgentAsk Ask(params AskQuestion[] questions) =>
        new("codex|t1|c1", 0, "Session", "request_user_input", "summary", questions, DateTimeOffset.UtcNow, "codex");

    [Fact]
    public void Thread_IsTheMiddlePartOfTheId()
    {
        Assert.Equal("t1", CodexReply.Thread("codex|t1|c1"));
        Assert.Null(CodexReply.Thread("codex||c1"));
        Assert.Null(CodexReply.Thread("timer"));
        Assert.Null(CodexReply.Thread("codex|t1"));
    }

    [Fact]
    public void Message_OneQuestion_IsTheChosenValue()
    {
        var ask = Ask(new AskQuestion("Pick one?", null, new[] { "Yes", "No" }, false));
        var reply = new AskReply(ReplyKinds.Answers, new Dictionary<string, string> { ["Pick one?"] = "Yes" });

        Assert.Equal("Yes", CodexReply.Message(ask, reply));
    }

    [Fact]
    public void Message_SeveralQuestions_IsOneLinePerQuestion()
    {
        var ask = Ask(
            new AskQuestion("A?", null, new[] { "1" }, false),
            new AskQuestion("B?", null, new[] { "2" }, false));
        var reply = new AskReply(ReplyKinds.Answers, new Dictionary<string, string> { ["A?"] = "1", ["B?"] = "2" });

        Assert.Equal("A?: 1\nB?: 2", CodexReply.Message(ask, reply));
    }

    [Fact]
    public void Message_SeveralQuestions_MissingChoiceIsEmpty()
    {
        var ask = Ask(
            new AskQuestion("A?", null, new[] { "1" }, false),
            new AskQuestion("B?", null, new[] { "2" }, false));
        var reply = new AskReply(ReplyKinds.Answers, new Dictionary<string, string> { ["A?"] = "1" });

        Assert.Equal("A?: 1\nB?: ", CodexReply.Message(ask, reply));
    }
}
