import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - Usage limits

struct LimitWindow: Identifiable, Equatable {
    let label: String
    let used: Double     // 0-100
    let resets: Date
    var id: String { label }
    /// When to warn: 5 % steps (84 → 80), then 99 (last call) and 100 (out: you wait for the reset). Never shown.
    static func step(_ used: Double) -> Int { used >= 100 ? 100 : used >= 99 ? 99 : Int(used / 5) * 5 }

    /// When it resets, as short as it stays clear: the time today, the weekday and time within a week, else the date.
    func resetText(now: Date = Date(), locale: Locale = L10n.locale, calendar: Calendar = .current) -> String {
        var style = Date.FormatStyle(locale: locale, calendar: calendar, timeZone: calendar.timeZone)
        if calendar.isDate(resets, inSameDayAs: now) { style = style.hour().minute() }
        else if resets.timeIntervalSince(now) < 6 * 86400 { style = style.weekday(.abbreviated).hour().minute() }
        else { style = style.day().month(.abbreviated) }
        return resets.formatted(style)
    }
}

/// Antigravity's quota, asked from its own language server on this Mac: it writes the port and token its apps use into
/// ~/.gemini/antigravity/daemon. Models that share a quota reset together, so each quota is one window named by its families.
enum AntigravityLimits {
    static let daemon = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini/antigravity/daemon")

    static func parse(_ data: Data, now: Date) -> ProviderLimits? {
        let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let configs = ((root?["userStatus"] as? [String: Any])?["cascadeModelConfigData"] as? [String: Any])?["clientModelConfigs"] as? [[String: Any]]
        let iso = ISO8601DateFormatter()
        var quotas: [(reset: Date, used: Double, families: [String])] = []
        for c in configs ?? [] {
            guard let q = c["quotaInfo"] as? [String: Any], let reset = (q["resetTime"] as? String).flatMap(iso.date),
                  let family = (c["label"] as? String)?.split(separator: " ").first.map(String.init) else { continue }
            // a fraction left out of the JSON is a zero: nothing remains
            let used = (1 - ((q["remainingFraction"] as? NSNumber)?.doubleValue ?? 0)) * 100
            if let i = quotas.firstIndex(where: { abs($0.reset.timeIntervalSince(reset)) < 120 && abs($0.used - used) < 0.5 }) {
                if !quotas[i].families.contains(family) { quotas[i].families.append(family) }
            } else {
                quotas.append((reset, used, [family]))
            }
        }
        // the fullest first: it is the one shown where there is room for one
        let windows = quotas.sorted { $0.used > $1.used }
            .map { LimitWindow(label: $0.families.joined(separator: "/"), used: $0.reset > now ? $0.used : 0, resets: $0.reset) }
        return windows.isEmpty ? nil : ProviderLimits(kind: .process, name: "Antigravity", bundleID: "com.google.antigravity", windows: windows, updated: now)
    }

    /// Nil when Antigravity is not running (its limits then leave the notch).
    static func fetch(now: Date = Date()) async -> ProviderLimits? {
        let files = ((try? FileManager.default.contentsOfDirectory(at: daemon, includingPropertiesForKeys: [.contentModificationDateKey])) ?? [])
            .filter { $0.pathExtension == "json" }
            .sorted { ((try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast)
                > ((try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast) }
        for f in files {
            guard let d = try? Data(contentsOf: f), let hub = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any],
                  let pid = hub["pid"] as? Int, kill(pid_t(pid), 0) == 0, let port = hub["httpPort"] as? Int, let token = hub["csrfToken"] as? String,
                  let url = URL(string: "http://127.0.0.1:\(port)/exa.language_server_pb.LanguageServerService/GetUserStatus") else { continue }
            var req = URLRequest(url: url, timeoutInterval: 5)
            req.httpMethod = "POST"
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.setValue("1", forHTTPHeaderField: "Connect-Protocol-Version")
            req.setValue(token, forHTTPHeaderField: "X-Codeium-Csrf-Token")
            req.httpBody = Data(#"{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}"#.utf8)
            if let (body, _) = try? await URLSession.shared.data(for: req), let found = parse(body, now: now) { return found }
        }
        return nil
    }

    static func selfTest() {
        let now = Date(timeIntervalSince1970: 1_794_380_000)  // 2026-11-11 06:53 UTC
        let json = #"{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini 3.8 Flash (High)","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-11-11T07:21:06Z"}},{"label":"Gemini 3.8 Flash (Low)","quotaInfo":{"remainingFraction":0.25,"resetTime":"2026-11-11T07:21:06Z"}},{"label":"Claude Sonnet 4.6","quotaInfo":{"remainingFraction":1,"resetTime":"2026-11-14T12:19:38Z"}},{"label":"GPT-OSS 120B","quotaInfo":{"remainingFraction":1,"resetTime":"2026-11-14T12:19:38Z"}},{"label":"Gemini 4 Argon","quotaInfo":{}}]}}}"#
        let p = parse(Data(json.utf8), now: now)
        precondition(p?.windows.map(\.label) == ["Gemini", "Claude/GPT-OSS"] && p?.windows.first?.used == 75 && p?.windows.last?.used == 0, "antigravity quotas grouped")
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let en = Locale(identifier: "en_GB")
        precondition(p?.windows[0].resetText(now: now, locale: en, calendar: utc) == "07:21", "a reset today is a time")
        precondition(p?.windows[1].resetText(now: now, locale: en, calendar: utc) == "Sat 12:19", "this week: the weekday too")
        let far = LimitWindow(label: "30d", used: 10, resets: now.addingTimeInterval(20 * 86400))
        precondition(far.resetText(now: now, locale: en, calendar: utc) == "1 Dec", "later: the date")
    }
}

struct ProviderLimits: Identifiable, Equatable {
    let kind: WatchItem.Kind
    let name: String
    let bundleID: String
    let windows: [LimitWindow]
    let updated: Date
    var id: String { name }
    /// The one window shown: the shortest one present (5h, else weekly).
    var main: LimitWindow? { windows.first }
}

func windowLabel(minutes: Int) -> String {
    minutes < 1440 ? "\(minutes / 60)h" : "\(minutes / 1440)d"
}

func readTail(_ url: URL, _ bytes: UInt64 = 1_000_000) -> String {
    guard let h = try? FileHandle(forReadingFrom: url) else { return "" }
    defer { try? h.close() }
    let size = (try? h.seekToEnd()) ?? 0
    try? h.seek(toOffset: size > bytes ? size - bytes : 0)
    return String(decoding: (try? h.readToEnd()) ?? Data(), as: UTF8.self)
}

// MARK: - Claude Code: session registry + limits

let claudeDir = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].map { URL(fileURLWithPath: $0) }
    ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")

/// ~/.claude/sessions/<pid>.json, written by Claude Code itself ("busy" while a turn runs).
struct ClaudeSession: Decodable {
    let pid: Int32
    let sessionId: String?
    let cwd: String?
    let name: String?
    let status: String?
    let statusUpdatedAt: Double?

    static func all() -> [ClaudeSession] {
        let dir = claudeDir.appendingPathComponent("sessions")
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }.compactMap {
            (try? Data(contentsOf: $0)).flatMap { try? JSONDecoder().decode(ClaudeSession.self, from: $0) }
        }
    }
}

/// Claude Code only exposes plan limits to the statusline; notch-statusline.sh saves that JSON here.
func claudeLimits(now: Date) -> ProviderLimits? {
    let url = claudeDir.appendingPathComponent("notchtasks-status.json")
    guard let data = try? Data(contentsOf: url),
          let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let rl = json["rate_limits"] as? [String: Any] else { return nil }
    let windows = [("five_hour", "5h"), ("seven_day", "7d")].compactMap { key, label -> LimitWindow? in
        guard let w = rl[key] as? [String: Any], let used = w["used_percentage"] as? Double, let r = w["resets_at"] as? Double,
              r > now.timeIntervalSince1970 else { return nil }
        return LimitWindow(label: label, used: used, resets: Date(timeIntervalSince1970: r))
    }
    let updated = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? now
    return windows.isEmpty ? nil : ProviderLimits(kind: .claude, name: "Claude", bundleID: "com.anthropic.claudefordesktop", windows: windows, updated: updated)
}

/// `claude -p /usage` answers with the plan's real usage without sending a prompt. The statusline only knows the numbers
/// from its own session's last request, so it goes stale while Claude is used elsewhere (desktop app, other sessions).
enum ClaudeUsage {
    static var cli: String? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return ["\(home)/.local/bin/claude", "/opt/homebrew/bin/claude", "/usr/local/bin/claude"].first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static let usedPattern = try! Regex(#"(\d+(?:\.\d+)?)% used"#)
    static let resetPattern = try! Regex(#"resets (.+?) \(([^)]+)\)"#)

    /// "Current session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)" → 5h window; "Current week (all models)…" → 7d.
    static func parse(_ text: String, now: Date) -> [LimitWindow] {
        text.split(separator: "\n").compactMap { line -> LimitWindow? in
            let label = line.hasPrefix("Current session") ? "5h" : line.hasPrefix("Current week (all models)") ? "7d" : nil
            guard let label, let m = line.firstMatch(of: usedPattern), let pct = Double(m.output[1].substring ?? "") else { return nil }
            var resets = now.addingTimeInterval(label == "5h" ? 5 * 3600 : 7 * 86400)
            if let r = line.firstMatch(of: resetPattern), let when = r.output[1].substring, let tz = r.output[2].substring {
                let f = DateFormatter()
                f.locale = Locale(identifier: "en_US_POSIX")
                f.timeZone = TimeZone(identifier: String(tz)) ?? .current
                // missing fields (year; minutes in "2pm") come from here: start of this year, not "now"
                var cal = Calendar(identifier: .gregorian)
                cal.timeZone = f.timeZone
                f.defaultDate = cal.dateInterval(of: .year, for: now)?.start
                let when = String(when).uppercased()
                for format in ["MMM d 'AT' h:mma", "MMM d 'AT' ha", "MMM d"] {
                    f.dateFormat = format
                    if var d = f.date(from: when) {
                        if d < now.addingTimeInterval(-86400) { d = Calendar.current.date(byAdding: .year, value: 1, to: d) ?? d }  // across new year
                        resets = d
                        break
                    }
                }
            }
            return LimitWindow(label: label, used: pct, resets: resets)
        }
    }
}

// MARK: - Codex: real task status from rollout files

struct CodexStatus: Equatable {
    let working: Bool
    let at: Date

    static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// The last task_started / task_complete / turn_aborted event decides the status.
    static func parse(_ text: String) -> CodexStatus? {
        for line in text.split(separator: "\n").reversed() where line.contains("\"type\":\"event_msg\"") {
            let started = line.contains("\"payload\":{\"type\":\"task_started\"")
            guard started || line.contains("\"payload\":{\"type\":\"task_complete\"") || line.contains("\"payload\":{\"type\":\"turn_aborted\"") else { continue }
            guard let r = line.range(of: "\"timestamp\":\""), let date = iso.date(from: String(line[r.upperBound...].prefix(24))) else { return nil }
            return CodexStatus(working: started, at: date)
        }
        return nil
    }

    static func limits(_ text: String, now: Date) -> (at: Date, windows: [LimitWindow])? {
        for line in text.split(separator: "\n").reversed() where line.contains("\"rate_limits\":{") {
            guard let json = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  let rl = (json["payload"] as? [String: Any])?["rate_limits"] as? [String: Any] else { continue }
            let at = (json["timestamp"] as? String).flatMap { iso.date(from: $0) } ?? .distantPast
            return (at, ["primary", "secondary"].compactMap { key -> LimitWindow? in
                guard let w = rl[key] as? [String: Any], let used = w["used_percent"] as? Double,
                      let mins = w["window_minutes"] as? Int, let r = w["resets_at"] as? Double else { return nil }
                // window already reset since the last event: nothing used yet
                return LimitWindow(label: windowLabel(minutes: mins), used: r > now.timeIntervalSince1970 ? used : 0,
                                   resets: Date(timeIntervalSince1970: r))
            })
        }
        return nil
    }
}

/// A question Codex left for you (request_user_input): it waits for your next message in that thread.
struct CodexQuestion: Equatable {
    let thread: String
    let callID: String
    let questions: [AgentAsk.Question]
    let at: Date

    /// Reads the question lines and your messages from new rollout text; returns the one still open, if any.
    static func parse(_ text: String, thread: String, open: CodexQuestion?) -> CodexQuestion? {
        var current = open
        for line in text.split(separator: "\n") where line.contains("\"type\":\"response_item\"") {
            if line.contains("\"role\":\"user\"") { current = nil; continue }  // you wrote back: answered
            guard line.contains("request_user_input"), line.contains("\"type\":\"function_call\""),
                  let json = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                  let p = json["payload"] as? [String: Any], (p["name"] as? String)?.hasPrefix("request_user_input") == true,
                  let args = (p["arguments"] as? String).flatMap({ try? JSONSerialization.jsonObject(with: Data($0.utf8)) }) as? [String: Any]
            else { continue }
            // the desktop app sends {title, options: [String]}; the terminal one {question, header, options: [{label}]}
            let qs = (args["questions"] as? [[String: Any]] ?? []).map { q in
                AgentAsk.Question(question: q["title"] as? String ?? q["question"] as? String ?? "", header: q["header"] as? String,
                                  options: q["options"] as? [String] ?? (q["options"] as? [[String: Any]] ?? []).compactMap { $0["label"] as? String },
                                  multiSelect: q["multiSelect"] as? Bool ?? false)
            }
            let at = (json["timestamp"] as? String).flatMap { CodexStatus.iso.date(from: $0) } ?? Date()
            if !qs.isEmpty { current = CodexQuestion(thread: thread, callID: p["call_id"] as? String ?? UUID().uuidString, questions: qs, at: at) }
        }
        return current
    }
}

final class CodexScanner {
    typealias Entry = (mtime: Date, offset: UInt64, status: CodexStatus?, title: String, child: Bool, thread: String, question: CodexQuestion?)
    private let root: URL
    private var cache: [String: Entry] = [:]

    init(root: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex/sessions")) { self.root = root }

    func jobs(now: Date) -> [Job] {
        guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return [] }
        var out: [Job] = []
        for case let url as URL in files where url.pathExtension == "jsonl" {
            // read for 3 h, so a question left open stays on the notch; a task with no writes for 20 min is dead
            guard let m = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                  now.timeIntervalSince(m) < 3 * 3600 else { continue }
            if cache[url.path]?.mtime != m { update(url, m) }
            guard now.timeIntervalSince(m) < 20 * 60, let entry = cache[url.path], !entry.child, let s = entry.status else { continue }
            let age = now.timeIntervalSince(s.at)
            if s.working {
                out.append(Job(id: url.lastPathComponent, title: entry.title, detail: L("Running · {0}", ago(age)), working: true))
            } else if age < 10 * 60 {
                out.append(Job(id: url.lastPathComponent, title: entry.title, detail: L("Done · {0} ago", ago(age)), working: false))
            }
        }
        return out
    }

    /// Newest rate_limits event across rollouts written this week (the newest file may have none).
    func limits(now: Date) -> ProviderLimits? {
        guard let files = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return nil }
        var best: (at: Date, windows: [LimitWindow])?
        for case let url as URL in files where url.pathExtension == "jsonl" {
            guard let m = try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate,
                  now.timeIntervalSince(m) < 7 * 86400, m > best?.at ?? .distantPast,
                  let found = CodexStatus.limits(readTail(url), now: now), found.at > best?.at ?? .distantPast else { continue }
            best = found
        }
        return best.map { ProviderLimits(kind: .codex, name: "Codex", bundleID: "com.openai.codex", windows: $0.windows, updated: $0.at) }
    }

    /// Rollouts grow to tens of MB and one turn can write >10 MB after its task_started,
    /// so read only the bytes added since the last scan and keep the last known status.
    private func update(_ url: URL, _ mtime: Date) {
        guard let h = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? h.close() }
        var entry: Entry = cache[url.path] ?? {
            let head = String(decoding: (try? h.read(upToCount: 16384)) ?? Data(), as: UTF8.self)
            // guardian/subagent threads start and finish every few seconds while the parent works: not tasks of their own
            return (mtime, 0, nil, Self.title(head), head.contains("\"parent_thread_id\""), Self.thread(head), nil)
        }()
        let size = (try? h.seekToEnd()) ?? 0
        if !entry.child {
            let from = entry.offset <= size ? entry.offset : 0  // file replaced: start over
            try? h.seek(toOffset: from)
            let text = String(decoding: (try? h.readToEnd()) ?? Data(), as: UTF8.self)
            entry.status = CodexStatus.parse(text) ?? entry.status
            entry.question = CodexQuestion.parse(text, thread: entry.thread, open: entry.question)
            // resume at the last full line so an event split by a write in progress is read next time
            let tail = text.utf8.lastIndex(of: UInt8(ascii: "\n")).map { text.utf8.distance(from: text.utf8.startIndex, to: $0) + 1 } ?? 0
            entry.offset = from + UInt64(tail)
        }
        entry.mtime = mtime
        cache[url.path] = entry
    }

    /// Questions still waiting on you, from threads written in the last 3 hours.
    func questions(now: Date) -> [(question: CodexQuestion, title: String)] {
        cache.values.compactMap { e in
            e.question.flatMap { now.timeIntervalSince($0.at) < 3 * 3600 ? ($0, e.title) : nil }
        }
    }

    private static func thread(_ head: String) -> String {
        guard let r = head.range(of: "\"session_id\":\""), let end = head[r.upperBound...].firstIndex(of: "\"") else { return "" }
        return String(head[r.upperBound..<end])
    }

    private static func title(_ head: String) -> String {
        guard let r = head.range(of: "\"cwd\":\""), let end = head[r.upperBound...].firstIndex(of: "\"") else { return "Codex" }
        return (String(head[r.upperBound..<end]) as NSString).lastPathComponent
    }
}

// MARK: - Local model runners: which models LM Studio and Ollama have loaded

enum LocalModels {
    static let lmStudio = "ai.elementlabs.lmstudio"

    /// `lms ps --json`: [{displayName, identifier…}]. Talks to the app itself, server on or off.
    static func lmStudioLoaded() -> [String] {
        let lms = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".lmstudio/bin/lms").path
        guard FileManager.default.isExecutableFile(atPath: lms) else { return [] }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: lms)
        p.arguments = ["ps", "--json"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [] }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return parseLMStudio(data)
    }

    static func parseLMStudio(_ data: Data) -> [String] {
        ((try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] ?? []).compactMap {
            $0["displayName"] as? String ?? $0["identifier"] as? String ?? $0["modelKey"] as? String
        }
    }

    /// Ollama's /api/ps: {"models": [{"name": "qwen3:1.7b", …}]}; nothing when it isn't running.
    static func ollamaLoaded() -> [String] {
        var req = URLRequest(url: URL(string: "http://127.0.0.1:11434/api/ps")!)
        req.timeoutInterval = 1.5
        var names: [String] = []
        let done = DispatchSemaphore(value: 0)
        URLSession.shared.dataTask(with: req) { data, _, _ in
            names = data.map(parseOllama) ?? []
            done.signal()
        }.resume()
        _ = done.wait(timeout: .now() + 2)
        return names
    }

    static func parseOllama(_ data: Data) -> [String] {
        (((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["models"] as? [[String: Any]] ?? []).compactMap { $0["name"] as? String }
    }
}
