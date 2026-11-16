using NotchTasks.Core;

namespace NotchTasks.Core.Asks;

/// What an ask's toast and card say, and when a tap answers. Pure, so the rules are tested here; the WPF controls
/// only show what this gives them (mac/Sources/Model.swift refreshAsks, Views.swift AskCard).
public static class AskPresentation
{
    /// The agent's name as the Mac shows it: its table row, or the id the hook sent when the agent is unknown.
    public static string AgentName(AgentAsk ask) =>
        AgentKinds.Parse(ask.Agent) is { } kind ? AgentKinds.Spec(kind).Name : ask.Agent;

    /// "Claude needs your approval", or "Claude is asking" for questions.
    public static string Title(AgentAsk ask) =>
        AgentName(ask) + (ask.IsQuestion ? L10n.L(" is asking") : L10n.L(" needs your approval"));

    /// "work · Run command: echo hi" for an approval; "work · <the question>" for questions.
    public static string Subtitle(AgentAsk ask) =>
        ask.Session + " · " + (ask.IsQuestion ? ask.Summary : AskParser.ActionLabel(ask.Tool) + ": " + ask.Summary);

    /// "Waiting 1:05": the time since the ask came in.
    public static string Waiting(TimeSpan elapsed) => L10n.L("Waiting ") + Mmss(elapsed);

    /// m:ss, rounded up like the Mac's mmss (minutes are not padded; hours appear from 3600 s).
    public static string Mmss(TimeSpan elapsed)
    {
        var total = (long)Math.Max(0, Math.Ceiling(elapsed.TotalSeconds));
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    /// One question with one single choice is answered by the tap itself.
    public static bool TapSends(IReadOnlyList<AskQuestion> questions) =>
        questions.Count == 1 && !questions[0].MultiSelect;

    /// The answers: question text to the picked labels, in the order the options are listed, joined with ", ".
    /// picks[i] holds the labels picked for question i.
    public static IReadOnlyDictionary<string, string> Answers(
        IReadOnlyList<AskQuestion> questions, IReadOnlyList<IReadOnlySet<string>> picks)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < questions.Count; i++)
        {
            var chosen = i < picks.Count ? picks[i] : new HashSet<string>(StringComparer.Ordinal);
            answers[questions[i].Question] = JoinPicks(questions[i].Options.Where(chosen.Contains));
        }
        return answers;
    }

    /// Multi-select picks are shown and sent as "a, b".
    public static string JoinPicks(IEnumerable<string> labels) => string.Join(", ", labels);

    /// Send is enabled only when every question has at least one pick.
    public static bool Ready(IReadOnlyList<AskQuestion> questions, IReadOnlyList<IReadOnlySet<string>> picks) =>
        questions.Select((_, i) => i).All(i => i < picks.Count && picks[i].Count > 0);
}
