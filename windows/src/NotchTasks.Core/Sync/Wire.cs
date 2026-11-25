using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;

namespace NotchTasks.Core.Sync;

/// What the Mac sends and receives, field for field electron/src/core/wire.js, Sources/Sync.swift (SyncSnapshot, SyncAnswer,
/// AgentAsk.forPush) and the phone's Wire.swift and Notch.swift. Dates are Swift seconds (SwiftDate). Anything the phone could
/// not decode is refused here, before it is sealed. Invalid input throws FormatException with the wire.js message.
public static class Wire
{
    /// The agents the phone knows, by their lowercase ids.
    public static readonly string[] AgentKinds = ["claude", "codex", "gemini", "antigravity", "cursor", "opencode"];

    /// The keys of the notch's look (SyncNotch), as wire.js NOTCH_KEYS has them. Every one is required.
    public static readonly string[] NotchKeys =
    [
        "size", "expandMode", "accent", "customAccent", "background", "gradientA", "gradientB", "toastStyle", "motion",
        "modules", "disabled", "placements", "haptics", "sounds", "volume", "systemNotifications", "doneAfter", "limitWarn", "lockScreen",
        "lockShows", "badges", "lockOwnBadges", "lockBadges", "toolIcons", "seenTools",
    ];

    /// Swift's String.prefix(n): n characters (text elements, as Intl.Segmenter counts graphemes), not code units.
    public static string PrefixChars(string s, int n)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        var end = 0;
        for (var i = 0; i < n && e.MoveNext(); i++)
        {
            end = e.ElementIndex + e.GetTextElement().Length;
        }
        return s[..end];
    }

    /// SyncSnapshot: what the phone decodes as Snapshot. Optional fields are left out when absent, as Swift does.
    public static JsonObject Snapshot(SyncSnapshot s) => Obj(
        ("mac", JsonValue.Create(s.Mac)),
        ("runs", Arr(s.Runs.Select(RunJson))),
        ("asks", Arr(s.Asks.Select(a => Ask(a)))),
        ("limits", Arr(s.Limits.Select(LimitJson))),
        ("at", Date(s.At)),
        ("builds", Arr(s.Builds.Select(RunJson))),
        ("health", s.Health is null ? null : HealthJson(s.Health)),
        ("events", Arr(s.Events.Select(EventJson))),
        ("job", s.Job is null ? null : JobJson(s.Job)),
        ("notch", s.Notch is null ? null : Notch(s.Notch)),
        ("crew", s.Crew is null ? null : CrewJson(s.Crew)));

    /// AgentAsk as the phone decodes it. Questions and options are always written; header is left out when absent.
    public static JsonObject Ask(SyncAsk a)
    {
        var x = a.Ask;
        return Obj(
            ("id", JsonValue.Create(x.Id)),
            ("pid", JsonValue.Create(x.Pid)),
            ("session", JsonValue.Create(x.Session)),
            ("tool", JsonValue.Create(x.Tool)),
            ("summary", JsonValue.Create(x.Summary)),
            ("questions", Arr(x.Questions.Select(QuestionJson))),
            ("at", Date(x.At)),
            ("agent", AgentNode(x.Agent)),
            ("icon", a.Icon is null ? null : Badge(a.Icon)));
    }

    /// AgentAsk.forPush (Sources/Sync.swift): the summary keeps 300 characters, the first 2 questions keep 200 and their first
    /// 4 options 60 each. Everything else stays as it is.
    public static SyncAsk AskForPush(SyncAsk a)
    {
        var questions = a.Ask.Questions.Take(2).Select(q => new AskQuestion(
            PrefixChars(q.Question, 200),
            q.Header,
            q.Options.Take(4).Select(o => PrefixChars(o, 60)).ToList(),
            q.MultiSelect)).ToList();
        return a with { Ask = a.Ask with { Summary = PrefixChars(a.Ask.Summary, 300), Questions = questions } };
    }

    /// A MacBadge / Badge as the phone decodes it. Its pictures (image, frames, fps, sprite) stay the Mac's own.
    /// A missing id gets a new upper-case one; symbol, shape, border and right get the phone's defaults.
    public static JsonObject Badge(JsonObject b)
    {
        var id = Text(b["id"]);
        var upperId = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString().ToUpperInvariant() : id.ToUpperInvariant();
        return Obj(
            ("id", JsonValue.Create(upperId)),
            ("symbol", Clone(b["symbol"]) ?? JsonValue.Create("star.fill")),
            ("image", Clone(b["image"])),
            ("text", Clone(b["text"])),
            ("color", Clone(b["color"])),
            ("fg", Clone(b["fg"])),
            ("plain", Clone(b["plain"])),
            ("shape", Clone(b["shape"]) ?? JsonValue.Create("circle")),
            ("border", Clone(b["border"]) ?? JsonValue.Create(false)),
            ("right", Clone(b["right"]) ?? JsonValue.Create(false)),
            ("sprite", Clone(b["sprite"])),
            ("frames", Clone(b["frames"])),
            ("fps", Clone(b["fps"])));
    }

    /// The notch's look (SyncNotch). Every key of NotchKeys is required, so a missing one is a FormatException.
    public static JsonObject Notch(JsonObject n)
    {
        foreach (var key in NotchKeys)
        {
            if (n[key] is null)
            {
                throw new FormatException($"notch: missing {key}");
            }
        }
        return Obj(
            ("size", Clone(n["size"])),
            ("expandMode", Clone(n["expandMode"])),
            ("accent", Clone(n["accent"])),
            ("customAccent", Clone(n["customAccent"])),
            ("background", Clone(n["background"])),
            ("gradientA", Clone(n["gradientA"])),
            ("gradientB", Clone(n["gradientB"])),
            ("toastStyle", Clone(n["toastStyle"])),
            ("motion", Clone(n["motion"])),
            ("modules", CloneList(n, "modules")),
            ("disabled", CloneList(n, "disabled")),
            ("placements", CloneObject(n, "placements")),
            ("haptics", Clone(n["haptics"])),
            ("sounds", Clone(n["sounds"])),
            ("volume", Clone(n["volume"])),
            ("systemNotifications", Clone(n["systemNotifications"])),
            ("doneAfter", Clone(n["doneAfter"])),
            ("limitWarn", Clone(n["limitWarn"])),
            ("lockScreen", Clone(n["lockScreen"])),
            ("lockShows", CloneList(n, "lockShows")),
            ("badges", BadgeList(n, "badges")),
            ("lockOwnBadges", Clone(n["lockOwnBadges"])),
            ("lockBadges", BadgeList(n, "lockBadges")),
            ("toolIcons", BadgeMap(n, "toolIcons")),
            ("seenTools", CloneList(n, "seenTools")));
    }

    /// The reply as Swift encodes AskReply: {"allow":{}}, {"deny":{}}, {"pass":{}} or {"answers":{"_0":{question: label}}}.
    public static JsonObject EncodeReply(AskReply r) => r.Kind switch
    {
        ReplyKinds.Allow or ReplyKinds.Deny or ReplyKinds.Pass => new JsonObject { [r.Kind] = new JsonObject() },
        ReplyKinds.Answers => new JsonObject { [ReplyKinds.Answers] = new JsonObject { ["_0"] = AnswersObject(r.Answers) } },
        _ => throw new FormatException("reply: unknown kind"),
    };

    /// The inverse of EncodeReply. Throws FormatException on any other shape.
    public static AskReply DecodeReply(JsonNode? j)
    {
        if (j is JsonObject o && o.Count == 1)
        {
            var entry = o.First();
            if (entry.Value is JsonObject or JsonArray)
            {
                if (entry.Key is ReplyKinds.Allow or ReplyKinds.Deny or ReplyKinds.Pass)
                {
                    return new AskReply(entry.Key, null);
                }
                if (entry.Key == ReplyKinds.Answers && entry.Value is JsonObject inner && inner["_0"] is { } answers)
                {
                    return new AskReply(ReplyKinds.Answers, AnswersFrom(answers));
                }
            }
        }
        throw new FormatException("reply: unknown shape");
    }

    /// A phone's sealed Answer, as the Mac reads it. The optional keys are read only when present and not null.
    public static PhoneAnswer ReadAnswer(JsonNode? o)
    {
        if (o is not JsonObject obj)
        {
            throw new FormatException("answer: not an object");
        }
        var ask = Str(obj["ask"], "answer ask");
        var reply = DecodeReply(obj["reply"]);
        var at = SwiftDate.To(Num(obj["at"], "answer at"));
        var phone = Present(obj["phone"]) ? Str(obj["phone"], "phone") : null;
        var notch = Present(obj["notch"]) ? Notch(AsObject(obj["notch"], "notch")) : null;
        var timer = Present(obj["timer"]) ? Int(obj["timer"], "timer") : (int?)null;
        var refresh = Present(obj["refresh"]) && Bool(obj["refresh"], "refresh");
        return new PhoneAnswer(ask, reply, at, phone, notch, timer, refresh);
    }

    private static JsonObject Obj(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
        {
            if (value is not null)
            {
                o[key] = value;
            }
        }
        return o;
    }

    private static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.ToArray());

    private static JsonNode Date(DateTimeOffset d) => JsonValue.Create(SwiftDate.From(d))!;

    private static JsonNode? Clone(JsonNode? n) => n?.DeepClone();

    private static bool Present(JsonNode? n) => n is not null;

    private static JsonNode? AgentNode(string? agent) => agent is null ? null : JsonValue.Create(CheckAgent(agent));

    private static string CheckAgent(string agent) =>
        AgentKinds.Contains(agent) ? agent : throw new FormatException($"unknown agent {agent}");

    private static JsonObject RunJson(SyncRun r) => Obj(
        ("id", JsonValue.Create(r.Id)),
        ("agent", AgentNode(r.Agent)),
        ("title", JsonValue.Create(r.Title)),
        ("detail", JsonValue.Create(r.Detail)),
        ("since", Date(r.Since)),
        ("icon", r.Icon is null ? null : Badge(r.Icon)));

    private static JsonObject LimitJson(SyncLimit l) => Obj(
        ("provider", JsonValue.Create(l.Provider)),
        ("label", JsonValue.Create(l.Label)),
        ("used", JsonValue.Create(l.Used)),
        ("resets", Date(l.Resets)));

    private static JsonObject HealthJson(SyncHealth h) => Obj(
        ("battery", OptInt(h.Battery)),
        ("charging", JsonValue.Create(h.Charging)),
        ("thermal", JsonValue.Create(h.Thermal)),
        ("temp", OptInt(h.Temp)),
        ("fan", OptInt(h.Fan)),
        ("cpu", OptInt(h.Cpu)),
        ("memory", OptInt(h.Memory)));

    private static JsonObject EventJson(SyncEvent e) => Obj(
        ("id", JsonValue.Create(e.Id)),
        ("kind", JsonValue.Create(e.Kind)),
        ("title", JsonValue.Create(e.Title)),
        ("detail", JsonValue.Create(e.Detail)),
        ("at", Date(e.At)));

    private static JsonObject JobJson(SyncJob j) => Obj(
        ("title", JsonValue.Create(j.Title)),
        ("detail", JsonValue.Create(j.Detail)),
        ("since", Date(j.Since)),
        ("state", JsonValue.Create(j.State)),
        ("ended", j.Ended is null ? null : Date(j.Ended.Value)),
        ("icon", j.Icon is null ? null : Badge(j.Icon)));

    private static JsonObject CrewJson(SyncCrew c) => Obj(
        ("count", JsonValue.Create(c.Count)),
        ("title", JsonValue.Create(c.Title)),
        ("detail", JsonValue.Create(c.Detail)),
        ("since", Date(c.Since)),
        ("agent", AgentNode(c.Agent)),
        ("icon", c.Icon is null ? null : Badge(c.Icon)),
        ("members", Arr(c.Members.Select(m => Obj(
            ("agent", AgentNode(m.Agent)),
            ("icon", m.Icon is null ? null : Badge(m.Icon)))))));

    private static JsonObject QuestionJson(AskQuestion q) => Obj(
        ("question", JsonValue.Create(q.Question)),
        ("header", q.Header is null ? null : JsonValue.Create(q.Header)),
        ("options", Arr(q.Options.Select(o => JsonValue.Create(o)))),
        ("multiSelect", JsonValue.Create(q.MultiSelect)));

    private static JsonNode? OptInt(int? v) => v is null ? null : JsonValue.Create(v.Value);

    private static JsonObject AnswersObject(IReadOnlyDictionary<string, string>? answers)
    {
        if (answers is null)
        {
            throw new FormatException("answers: not an object");
        }
        var o = new JsonObject();
        foreach (var (question, answer) in answers)
        {
            o[question] = JsonValue.Create(answer);
        }
        return o;
    }

    private static IReadOnlyDictionary<string, string> AnswersFrom(JsonNode node)
    {
        if (node is not JsonObject o)
        {
            throw new FormatException("answers: not an object");
        }
        var answers = new Dictionary<string, string>();
        foreach (var (question, answer) in o)
        {
            answers[question] = Str(answer, "answer");
        }
        return answers;
    }

    private static JsonArray CloneList(JsonObject n, string key) =>
        n[key] is JsonArray a ? Arr(a.Select(Clone)) : throw new FormatException($"notch: {key} is not a list");

    private static JsonObject CloneObject(JsonObject n, string key) =>
        n[key] is JsonObject o ? (JsonObject)Clone(o)! : throw new FormatException($"notch: {key} is not an object");

    private static JsonArray BadgeList(JsonObject n, string key) =>
        n[key] is JsonArray a ? Arr(a.Select(x => Badge(AsObject(x, "badge")))) : throw new FormatException($"notch: {key} is not a list");

    private static JsonObject BadgeMap(JsonObject n, string key) =>
        n[key] is JsonObject m
            ? Obj(m.Select(p => (p.Key, (JsonNode?)Badge(AsObject(p.Value, "badge")))).ToArray())
            : throw new FormatException($"notch: {key} is not an object");

    private static JsonObject AsObject(JsonNode? n, string what) =>
        n as JsonObject ?? throw new FormatException($"{what}: not an object");

    private static bool IsKind(JsonNode? n, JsonValueKind kind) => n is JsonValue v && v.GetValueKind() == kind;

    /// The text of a JSON string; other values are written as their JSON, as JavaScript's String() would show them. Null for none.
    private static string? Text(JsonNode? n) => n is null ? null : IsKind(n, JsonValueKind.String) ? n.GetValue<string>() : n.ToJsonString();

    private static string Str(JsonNode? n, string what) =>
        IsKind(n, JsonValueKind.String) ? n!.GetValue<string>() : throw new FormatException($"{what}: not a string");

    private static double Num(JsonNode? n, string what) =>
        IsKind(n, JsonValueKind.Number) ? n!.GetValue<double>() : throw new FormatException($"{what}: not a number");

    private static int Int(JsonNode? n, string what)
    {
        if (!IsKind(n, JsonValueKind.Number))
        {
            throw new FormatException($"{what}: not an integer");
        }
        var d = n!.GetValue<double>();
        if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
        {
            throw new FormatException($"{what}: not an integer");
        }
        return (int)d;
    }

    private static bool Bool(JsonNode? n, string what) =>
        (IsKind(n, JsonValueKind.True) || IsKind(n, JsonValueKind.False))
            ? n!.GetValue<bool>()
            : throw new FormatException($"{what}: not a boolean");
}
