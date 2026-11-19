namespace NotchTasks.Core.Asks;

/// The reply to a Codex question, as the Mac writes it (Model.swift reply, AgentAsks.codexReply): the thread the question is in,
/// and the message the answer becomes.
public static class CodexReply
{
    /// The thread of a codex|thread|callId ask, or null when the id is not one.
    public static string? Thread(string askId)
    {
        var parts = askId.Split('|');
        return parts.Length == 3 && parts[0] == "codex" && parts[1].Length > 0 ? parts[1] : null;
    }

    /// The message for an answer. One question gives the chosen value; several give one "question: choice" line each, in the
    /// questions' order (a question with no choice gives an empty value).
    public static string Message(AgentAsk ask, AskReply reply)
    {
        var picked = reply.Answers;
        if (ask.Questions.Count == 1)
        {
            return picked?.Values.FirstOrDefault() ?? string.Empty;
        }
        return string.Join("\n", ask.Questions.Select(q =>
            $"{q.Question}: {(picked is not null && picked.TryGetValue(q.Question, out var choice) ? choice : string.Empty)}"));
    }
}
