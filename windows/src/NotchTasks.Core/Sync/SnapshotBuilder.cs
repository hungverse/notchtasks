using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NotchTasks.Core.Sync;

/// What the phone is told, built from the app's state. Port of buildSnapshot, jobOf, record, members, crewSignal, bare, isPro,
/// proNoteText and healthOf in electron/src/core/sync.js (the Mac's SyncHub in Sources/Sync.swift). Not thread-safe: SyncHub calls
/// it under its lock. Times are DateTimeOffset here and ms since 1970 in CrewSig, as the JS keeps them.
public sealed class SnapshotBuilder
{
    private const long LiveJobMs = 300_000;     // a finished build stays on the phone this long
    private const long CrewStepMs = 600_000;    // a count change reaches the push function at most this often
    private const double FailedNearMs = 30_000; // a CI result lands this close to the job's end
    private const int MaxEvents = 20;

    // The kinds the phone gets whatever the caller says (sync.js record): CI results and warnings, never a silent task.
    private static readonly string[] PhoneKinds = ["ciPassed", "ciFailed", "limitWarn", "lowBattery"];

    // JavaScript's \s, written out so the same characters count here as in sync.js.
    private const string Ws = @"[\t\n\v\f\r    -     　﻿]";

    // The clock and the estimate of a detail (sync.js BARE, Vietnamese words included).
    private static readonly Regex BareDetail = new(
        Ws + "·" + Ws + "([0-9]+:[0-9]{2}(?::[0-9]{2})?|≈[^·]*|[0-9]+" + Ws + "(?:giây|phút|giờ|sec|min|hours)[^·]*)",
        RegexOptions.CultureInvariant);

    private readonly Dictionary<string, DateTimeOffset> _since = new(StringComparer.Ordinal);
    private List<SyncEvent> _events = [];
    private SyncRun? _followed;   // the build followed while it runs
    private SyncJob? _ended;      // the build that just ended, kept for 5 minutes

    /// The events the phone gets, oldest first: at most 20, and only the last 24 hours.
    public IReadOnlyList<SyncEvent> Events => _events;

    /// The snapshot as the phone reads it. Runs and builds keep the time they were first seen; the job and the crew follow them.
    public SyncSnapshot Build(SyncInput input, string mac, DateTimeOffset now)
    {
        var runs = input.Runs.Select(r => r with { Since = SeenSince(r.Id, now) }).ToList();
        var builds = input.Builds.Select(b => b with { Agent = null, Since = SeenSince(b.Id, now) }).ToList();

        var ids = new HashSet<string>(runs.Select(r => r.Id).Concat(builds.Select(b => b.Id)), StringComparer.Ordinal);
        foreach (var r in runs.Concat(builds))
        {
            _since[r.Id] = r.Since;
        }
        foreach (var id in _since.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _since.Remove(id);
        }

        var oldest = Oldest(runs);
        return new SyncSnapshot(
            Mac: mac,
            Runs: runs,
            Builds: builds,
            Asks: input.Asks,
            Limits: input.Limits.Select(l => new SyncLimit(l.Provider, l.Label, (int)Math.Truncate(l.Used), WholeMinute(l.Resets))).ToList(),
            Health: HealthOf(input.Health),
            Events: _events,
            Job: JobOf(builds, now),
            Notch: input.Notch,
            Crew: oldest is null
                ? null
                : new SyncCrew(runs.Count, oldest.Title, Bare(oldest.Detail), oldest.Since, oldest.Agent, oldest.Icon, Members(runs)),
            At: now);
    }

    /// Keeps a notable event for the phone when it is for the phone (toPhone) or one of the kinds the phone always gets.
    /// Returns the event kept, or null when it is not kept.
    public SyncEvent? Record(string kind, string title, string detail, bool toPhone, DateTimeOffset now)
    {
        if (!toPhone && !PhoneKinds.Contains(kind, StringComparer.Ordinal))
        {
            return null;
        }
        var ev = new SyncEvent(Guid.NewGuid().ToString().ToUpperInvariant(), kind, title, detail, now);
        var cutoff = now - TimeSpan.FromDays(1);
        _events = _events.Append(ev).Where(e => e.At > cutoff).TakeLast(MaxEvents).ToList();
        return ev;
    }

    /// The detail without its clock and its estimate: the Live Activity counts the time itself.
    public static string Bare(string detail) => BareDetail.Replace(detail, "");

    /// Each agent once (by agent and icon), oldest first, at most four.
    public static IReadOnlyList<SyncMember> Members(IEnumerable<SyncRun> runs)
    {
        var seen = new HashSet<(string? Agent, string Icon)>();
        var members = new List<SyncMember>();
        foreach (var r in runs.OrderBy(r => r.Since))
        {
            if (seen.Add((r.Agent, IconText(r.Icon))))
            {
                members.Add(new SyncMember(r.Agent, r.Icon));
            }
        }
        return members.Take(4).ToList();
    }

    /// What the push function is told about the agents at work: starting and all stopping at once, else at most every 10 minutes.
    public static CrewSig CrewSignal(int count, double at, CrewSig last, double nowMs)
    {
        if ((count > 0) != (last.Count > 0))
        {
            return new CrewSig(count, at, nowMs);
        }
        if ((count != last.Count || at != last.At) && nowMs - last.Stamp >= CrewStepMs)
        {
            return new CrewSig(count, at, nowMs);
        }
        return last;
    }

    /// True only for an entitlement with pro true and until later than now (sync.js isPro: Number(until) for numbers and numeric text).
    public static bool IsPro(JsonNode? entitlement, double nowMs) =>
        entitlement is JsonObject e
        && e["pro"] is JsonValue pro && pro.GetValueKind() == JsonValueKind.True
        && JsNumber(e["until"]) > nowMs;

    /// The note the phone's group shows for a Pro change, or null for a reason it does not know.
    public static string? ProNoteText(string why) => why switch
    {
        "moved" => L10n.L("The group’s Pro moved to another computer"),
        "refunded" => L10n.L("The group’s Pro plan was refunded"),
        "expired" => L10n.L("The group’s Pro plan has expired"),
        _ => null,
    };

    /// The health as the phone shows it: the battery as read, CPU and memory in whole 5 % steps, the temperature in whole degrees.
    /// Thermal is 0 and there is no fan reading, as the Windows side has neither.
    public static SyncHealth? HealthOf(SyncHealthInput? h)
    {
        if (h is null)
        {
            return null;
        }
        return new SyncHealth(
            Battery: h.Battery,
            Charging: h.Charging,
            Thermal: 0,
            Temp: h.Temp is null ? null : (int)JsRound(h.Temp.Value),
            Fan: null,
            Cpu: Step(h.Cpu),
            Memory: Step(h.Memory));
    }

    private DateTimeOffset SeenSince(string id, DateTimeOffset now) =>
        _since.TryGetValue(id, out var since) ? since : now;

    private SyncJob? JobOf(IReadOnlyList<SyncRun> builds, DateTimeOffset now)
    {
        if (builds.Count > 0)
        {
            var b = Oldest(builds)!;
            _followed = b;
            _ended = null;
            return new SyncJob(b.Title, Bare(b.Detail), b.Since, "running", null, b.Icon);
        }
        if (_followed is { } f)
        {
            _followed = null;
            _ended = new SyncJob(f.Title, Bare(f.Detail), f.Since, "done", now, f.Icon);
        }
        if (_ended is not { Ended: { } endedAt } e || now - endedAt >= TimeSpan.FromMilliseconds(LiveJobMs))
        {
            _ended = null;
            return null;
        }
        // a CI result lands around the time its job leaves the list
        var failed = _events.Any(ev => ev.Kind == "ciFailed" && Math.Abs((ev.At - endedAt).TotalMilliseconds) < FailedNearMs);
        return failed ? e with { State = "failed" } : e;
    }

    private static SyncRun? Oldest(IReadOnlyList<SyncRun> runs)
    {
        SyncRun? oldest = null;
        foreach (var r in runs)
        {
            if (oldest is null || r.Since < oldest.Since)
            {
                oldest = r;
            }
        }
        return oldest;
    }

    private static DateTimeOffset WholeMinute(DateTimeOffset d) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)JsRound(d.ToUnixTimeMilliseconds() / 60000.0) * 60000);

    private static int? Step(double? v) => v is null ? null : (int)(JsRound(v.Value * 20) * 5);

    /// JavaScript's Math.round: halves go up, toward +infinity.
    private static double JsRound(double x) => Math.Floor(x + 0.5);

    private static string IconText(JsonObject? icon) => icon is null ? "" : SyncJson.Write(icon);

    /// JavaScript's Number() for what the entitlement carries: a number, or numeric text. Anything else is NaN.
    internal static double JsNumber(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return double.NaN;
        }
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return v.TryGetValue<double>(out var num) ? num : double.NaN;
            case JsonValueKind.String:
                var text = v.GetValue<string>().Trim();
                if (text.Length == 0)
                {
                    return 0;
                }
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            default:
                return double.NaN;
        }
    }
}
