namespace NotchTasks.Core.Asks;

/// One question inside an ask: options are labels (or plain strings, as Antigravity and the Codex app send them).
public sealed record AskQuestion(string Question, string? Header, IReadOnlyList<string> Options, bool MultiSelect);

/// An ask as the hook writes it to Asks\<id>.json. IsQuestion is true when the agent asked the user questions.
public sealed record AgentAsk(
    string Id,
    int Pid,
    string Session,
    string Tool,
    string Summary,
    IReadOnlyList<AskQuestion> Questions,
    DateTimeOffset At,
    string Agent)
{
    public bool IsQuestion => Questions.Count > 0;
}

/// The user's answer as the app writes it to Asks\<id>.reply. Kind is one of ReplyKinds.
public sealed record AskReply(string Kind, IReadOnlyDictionary<string, string>? Answers);

public static class ReplyKinds
{
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Pass = "pass";
    public const string Answers = "answers";
}
