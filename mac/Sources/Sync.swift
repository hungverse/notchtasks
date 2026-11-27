import AppKit
import SwiftUI
import CryptoKit
import Security
import CoreImage.CIFilterBuiltins
import Network

// MARK: - Sync v1 with the iPhone app (docs/sync-v1.md), over Firebase's REST API
// shortcut: REST + one event stream instead of the Firebase SDK (no keychain entitlement, no big dependency); move to the SDK if we need offline queues.

/// The wire types, field for field the iPhone's iOS/NotchTasks/Wire.swift. Default JSON dates on both sides.
struct SyncRun: Codable, Equatable {
    var id: String; var agent: AgentKind?; var title: String; var detail: String; var since: Date
    var icon: Badge? = nil  // the icon the notch uses for it: the app's own, or the build tool's
}
/// The agents at work, for the Live Activity when nothing asks and nothing builds: the longest one, and how many.
struct SyncCrew: Codable, Equatable {
    var count: Int; var title: String; var detail: String; var since: Date; var agent: AgentKind?; var icon: Badge?
    var members: [Member] = []  // each agent at work once, oldest first: the phone stacks their icons
    struct Member: Codable, Equatable { var agent: AgentKind?; var icon: Badge? }
}
struct SyncLimit: Codable, Equatable { var provider: String; var label: String; var used: Double; var resets: Date }
struct SyncSnapshot: Codable, Equatable {
    var mac: String; var runs: [SyncRun]; var asks: [AgentAsk]; var limits: [SyncLimit]; var at: Date
    var builds: [SyncRun] = []   // Build & CI: Xcode, Gradle, runners, GitHub/GitLab jobs at work
    var health: SyncHealth?
    var events: [SyncEvent] = [] // newest last: what finished, failed or needs a look
    var job: SyncJob?            // the build the phone's Live Activity follows
    var notch: SyncNotch?        // the notch's look, which the phone shows and edits (NotchSync.swift)
    var crew: SyncCrew?          // the agents at work, for the Live Activity
}
/// As the iPhone's JobFocus: the longest build at work, or the one that just ended (5 minutes). state: running, done, failed.
struct SyncJob: Codable, Equatable { var title: String; var detail: String; var since: Date; var state: String; var ended: Date?; var icon: Badge? = nil }
/// The Mac's own state, rounded so a small wobble is no new snapshot. Percent values; thermal is ProcessInfo's 0…3.
struct SyncHealth: Codable, Equatable { var battery: Int?; var charging: Bool; var thermal: Int; var temp: Int?; var fan: Int? = nil; var cpu: Int?; var memory: Int? }
/// A notch announcement worth knowing on the phone. kind: taskDone, ciPassed, ciFailed, limitWarn, lowBattery, warning.
struct SyncEvent: Codable, Equatable { var id: String; var kind: String; var title: String; var detail: String; var at: Date }
struct SyncAnswer: Codable, Equatable {
    var ask: String; var reply: AskReply; var at: Date
    var phone: String?          // its Firebase uid, in the Wi-Fi hello
    var notch: SyncNotch? = nil // the notch as the phone edited it (an empty ask)
    var timer: Int? = nil       // the phone's focus timer, in minutes (0: stopped): the notch counts it too
    var refresh: Bool? = nil    // the phone's app opened off the Wi-Fi: write the snapshot now (nothing else writes it on a timer)
}

extension AgentAsk {
    /// As the iPhone's AgentAsk.trimmed (iOS/Shared/Seal.swift): fits a push and a Live Activity.
    var forPush: AgentAsk {
        var a = self
        a.summary = String(summary.prefix(300))
        a.questions = questions.prefix(2).map { .init(question: String($0.question.prefix(200)), header: $0.header, options: $0.options.prefix(4).map { String($0.prefix(60)) }, multiSelect: $0.multiSelect) }
        return a
    }
}

/// What the Mac keeps: the group, its key, and the anonymous Firebase account that owns the group.
struct SyncGroup: Codable, Equatable {
    var g: String
    var k: String       // base64url, 32 bytes
    var uid: String
    var refresh: String // Firebase refresh token: the same uid across launches, or the Mac stops being the group's member
    var key: SymmetricKey { SymmetricKey(data: Data(base64url: k) ?? Data()) }
    /// The QR code's text, read by the iPhone app's Pairing.parse.
    var code: String { #"{"v":1,"g":"\#(g)","k":"\#(k)","n":"\#(SyncHub.macName.replacingOccurrences(of: "\"", with: "'"))"}"# }
    /// Encodes code in https://notchtasks.web.app/pair#...
    var pairURL: String { SyncHub.pairURL(code) }
}

extension Data {
    init?(base64url s: String) {
        var b = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        b += String(repeating: "=", count: (4 - b.count % 4) % 4)
        self.init(base64Encoded: b)
    }
    var base64url: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    static func random(_ n: Int) -> Data {
        var d = Data(count: n)
        _ = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, n, $0.baseAddress!) }
        return d
    }
}

enum SyncBox {
    static func seal<T: Encodable>(_ v: T, _ key: SymmetricKey) throws -> String {
        try AES.GCM.seal(JSONEncoder().encode(v), using: key).combined!.base64EncodedString()
    }
    static func open<T: Decodable>(_ box: String, _ key: SymmetricKey, as: T.Type) throws -> T {
        guard let d = Data(base64Encoded: box) else { throw CocoaError(.coderReadCorrupt) }
        return try JSONDecoder().decode(T.self, from: AES.GCM.open(AES.GCM.SealedBox(combined: d), using: key))
    }
}

@MainActor final class SyncHub: ObservableObject {
    // the Mac app's Firebase client config (Config/GoogleService-Info.plist): public by design, the rules guard the data
    static let apiKey = "AIzaSyDeHy-Giegt9j4OsBLOeYFqHilTZYQ0bDM"
    static let db = "https://notchtasks-default-rtdb.asia-southeast1.firebasedatabase.app"
    nonisolated static var macName: String { Host.current().localizedName ?? "Mac" }

    @Published private(set) var group: SyncGroup? = SyncKeychain.load()
    @Published private(set) var phones: [String: String] = [:] {  // uid → phone name
        didSet {
            // phones seen before are remembered, so a restart or a list that failed to load is not a new pairing
            var seen = Set(UserDefaults.standard.stringArray(forKey: "analytics.phones.seen") ?? [])
            let fresh = Set(phones.keys).subtracting(seen)
            guard !fresh.isEmpty else { return }
            seen.formUnion(fresh)
            UserDefaults.standard.set(Array(seen), forKey: "analytics.phones.seen")
            Track.event("phone_paired", ["phones": phones.count])
        }
    }
    @Published var problem: String?

    /// Same Wi-Fi: the phone talks to the Mac directly, and Firebase is used only when it is away.
    @Published var lan = UserDefaults.standard.object(forKey: "syncLAN") as? Bool ?? true {
        didSet { UserDefaults.standard.set(lan, forKey: "syncLAN"); startLAN() }
    }
    @Published private(set) var lanPhones = 0  // phones on the same network that proved they hold the key
    /// Pushes (new asks, finished or failed tasks) only while you are away from the Mac: at it, the notch already told you.
    @Published var onlyAway = UserDefaults.standard.object(forKey: "syncOnlyAway") as? Bool ?? true {
        didSet { UserDefaults.standard.set(onlyAway, forKey: "syncOnlyAway") }
    }
    private var events: [SyncEvent] = []
    private var eventAt = 0          // ms time of the newest event that should reach the phone by push
    private var eventBox = ""        // that event, sealed: the phone's notification service opens it
    private var sentAway: Bool?
    private var lastTicks: [UInt32]?
    private var followed: SyncRun?   // the build being followed, to say "done" when it disappears
    private var ended: SyncJob?

    weak var model: Model?
    private var token: (id: String, until: Date)?
    private var sent: SyncSnapshot?   // last written to Firebase, `at` zeroed
    private var sentLocal: SyncSnapshot?
    private var sending = false
    private var listener: NWListener?
    private var conns: [ObjectIdentifier: (c: NWConnection, ok: Bool, phone: String?)] = [:]
    private var since: [String: Date] = [:]
    private var stream: Task<Void, Never>?
    /// The group's NotchTasks Pro (bought on an iPhone, written by the Cloud Function). Without it Firebase carries only
    /// what the ask notification needs: the rest (snapshot, builds, agents, events) stays on the Wi-Fi.
    private(set) var pro = false { didSet { Badge.pro = pro } }
    @Published private(set) var proUntil: Double?
    @Published private(set) var proProduct = ""
    @Published private(set) var proTx = ""
    @Published private(set) var proHolders: Set<String> = []
    @Published private(set) var proNote: (why: String, at: Double)?
    private var proAt = Date.distantPast

    func start(_ model: Model) {
        self.model = model
        if group != nil { listen(); startLAN(); Task { await loadPhones() } }
    }

    // MARK: Pairing

    /// A new group (fresh id and key), owned by a fresh anonymous account. Old phones can no longer read anything new.
    func create() async {
        do {
            let r: [String: String] = try await post("https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=\(Self.apiKey)", json: ["returnSecureToken": true])
            guard let id = r["idToken"], let uid = r["localId"], let refresh = r["refreshToken"] else { throw URLError(.badServerResponse) }
            token = (id, Date().addingTimeInterval(3000))
            let g = SyncGroup(g: Data.random(16).base64url, k: Data.random(32).base64url, uid: uid, refresh: refresh)
            try await call("PUT", "groups/\(g.g)/members/\(uid)", ["role": "mac", "name": Self.macName, "at": [".sv": "timestamp"]], group: g)
            SyncKeychain.save(g)
            group = g; sent = nil; phones = [:]; pro = false; proUntil = nil; proProduct = ""; proTx = ""; proHolders = []; proNote = nil; problem = nil
            listen(); startLAN()
            Track.event("pair_code_created")
        } catch { problem = L("Couldn't create a pairing code: {0}", error.localizedDescription) }
    }

    /// Removes every phone, then the Mac itself: nobody can read the old group any more.
    func unpairAll() async {
        guard let g = group else { return }
        Track.event("phone_unpaired")
        stream?.cancel(); stream = nil
        await loadPhones()
        for uid in phones.keys { try? await call("DELETE", "groups/\(g.g)/members/\(uid)", nil, group: g) }
        try? await call("DELETE", "groups/\(g.g)/members/\(g.uid)", nil, group: g)
        SyncKeychain.clear()
        group = nil; phones = [:]; pro = false; proHolders = []; proUntil = nil; proProduct = ""; proTx = ""; proNote = nil; token = nil; sent = nil; problem = nil
        startLAN()
    }

    func loadPhones() async {
        guard let g = group else { return }
        if let d = try? await call("GET", "groups/\(g.g)/members", nil, group: g),
           let all = try? JSONSerialization.jsonObject(with: d) as? [String: [String: Any]] {
            phones = all.compactMapValues { $0["role"] as? String == "phone" ? $0["name"] as? String ?? L("Phone") : nil }
        }
        await checkPro(g, force: true)
    }

    // MARK: Mac → phone

    /// Called every second. A phone on the same Wi-Fi gets every change at once. Firebase (each write runs the push function)
    /// gets only what a phone must learn while closed: an ask, a build, agents starting or stopping, an event, a notch edit,
    /// leaving the Mac while something is live. The rest (limits, CPU, details) waits until the phone's app asks (`refresh`).
    func publish() {
        guard let g = group, let model else { return }
        let s = snapshot(model)
        if lanPhones > 0, s != sentLocal {
            sentLocal = s
            var out = s
            out.at = Date()
            if let d = try? SyncBox.seal(out, g.key), let frame = Data(base64Encoded: d) {
                for peer in conns.values where peer.ok { peer.c.send(content: LinkFrames.wrap(frame), completion: .contentProcessed { _ in }) }
            }
        }
        // phones on this Wi-Fi already have it: Firebase (and its push) only for the others
        let handled = Set(conns.values.filter(\.ok).compactMap(\.phone))
        let needsCloud = phones.isEmpty ? lanPhones == 0 : phones.keys.contains { !handled.contains($0) }
        let away = !onlyAway || Self.away
        // just left (or came back) while something is live: what waits now may need a push
        // (only for asks: agents or a build running while you come and go need no push, so no write)
        if away != sentAway, !s.asks.isEmpty { sent = nil }
        guard needsCloud, !sending, s != sent,
              s.asks != sent?.asks || pro && (s.events != sent?.events || s.job?.state != sent?.job?.state || s.job?.since != sent?.job?.since
                || s.notch != sent?.notch  // the phone waits to see its edit land
                || s.crew?.count != sent?.crew?.count || s.crew?.since != sent?.crew?.since) else { return }
        sending = true
        var out = s
        out.at = Date()
        Task {
            defer { sending = false }
            do {
                // asks/newest stay readable (a count and a time, no content): the push function sees a new ask without the key
                // `push`: the newest ask alone, sealed and cut short, for the phone's notification and Live Activity (4 KB max)
                let push = try s.asks.max(by: { $0.at < $1.at }).map { try SyncBox.seal($0.forPush, g.key) } ?? ""
                // `job`: the followed build, sealed; `jobState`/`jobAt` let the function start, ring and end it unread
                let job = try s.job.map { try SyncBox.seal($0, g.key) } ?? ""
                // `crew`: the agents at work, sealed; `crewCount` lets the function start and end the Live Activity unread
                let crew = try s.crew.map { try SyncBox.seal($0, g.key) } ?? ""
                var body: [String: Any] = ["box": try SyncBox.seal(out, g.key), "at": [".sv": "timestamp"]]
                body["asks"] = s.asks.count
                body["newest"] = Self.newest(s.asks)
                // `direct`: phones on the Wi-Fi, which the push function skips
                body["direct"] = Dictionary(uniqueKeysWithValues: handled.map { ($0, true) })
                body["push"] = push
                // `quiet`: you are at the Mac, no alerts; `eventAt`/`eventBox`: a finished or failed task
                body["quiet"] = !away
                body["eventAt"] = eventAt
                body["eventBox"] = eventBox
                body["job"] = job
                body["jobState"] = s.job?.state ?? ""
                body["jobAt"] = s.job.map { Int($0.since.timeIntervalSince1970 * 1000) } ?? 0
                // the function compares these plain fields: the sealed ones change bytes at every write
                body["crew"] = crew
                body["crewCount"] = s.crew?.count ?? 0
                body["crewAt"] = s.crew.map { Int($0.since.timeIntervalSince1970 * 1000) } ?? 0
                await checkPro(g)
                if !pro {
                    // the plain fields stay (the function needs asks/newest/quiet/direct and the sealed `push`); the rest goes
                    for k in ["job", "eventBox", "crew", "jobState"] { body[k] = "" }
                    for k in ["eventAt", "jobAt", "crewCount", "crewAt"] { body[k] = 0 }
                    body["box"] = ""
                }
                // `sig`: the push function runs only when this changes (each run is billed), so it holds nothing but what
                // a push depends on. Agents starting or all stopping go at once; their number changing waits its turn
                sigCrew = Self.crewSignal(count: body["crewCount"] as? Int ?? 0, at: body["crewAt"] as? Int ?? 0, last: sigCrew, now: Date())
                var sig: [String: Any] = ["crewCount": sigCrew.count, "crewAt": sigCrew.at]
                for k in ["asks", "newest", "direct", "quiet", "eventAt", "jobState", "jobAt"] { sig[k] = body[k] }
                body["sig"] = sig
                try await call("PUT", "groups/\(g.g)/snapshot", body, group: g)
                sent = s
                sentAway = away
                if problem != nil { problem = nil }
            } catch { problem = L("Couldn't send to the phone: {0}", error.localizedDescription) }
        }
    }

    private var sigCrew: (count: Int, at: Int, stamp: Date) = (0, 0, .distantPast)

    /// What the push function is told about the agents at work. Starting and all stopping are told at once; a change
    /// in their number at most every 10 minutes, since each telling is a billed run and the Live Activity lives without it.
    static func crewSignal(count: Int, at: Int, last: (count: Int, at: Int, stamp: Date), now: Date) -> (count: Int, at: Int, stamp: Date) {
        if (count > 0) != (last.count > 0) { return (count, at, now) }
        if (count, at) != (last.count, last.at), now.timeIntervalSince(last.stamp) >= 600 { return (count, at, now) }
        return last
    }

    static func newest(_ asks: [AgentAsk]) -> Int { asks.map(\.at).max().map { Int($0.timeIntervalSince1970 * 1000) } ?? 0 }

    private func snapshot(_ m: Model) -> SyncSnapshot {
        var runs: [SyncRun] = []
        for st in m.states where !st.item.inBuild {
            let agent = AgentKind.allCases.first { $0.item(in: m.items)?.id == st.item.id }
            for j in st.jobs where j.working {
                runs.append(SyncRun(id: j.id, agent: agent, title: j.title, detail: j.detail, since: since[j.id] ?? Date(), icon: st.item.icon))
            }
        }
        for r in runs where since[r.id] == nil { since[r.id] = r.since }
        // whole percents and minutes: a fraction more used, or a reset read a second later, is not worth a write
        let limits = m.limits.flatMap { p in p.windows.map { SyncLimit(provider: p.name, label: $0.label, used: Double(Int($0.used)),
                                                                                   resets: Date(timeIntervalSince1970: ($0.resets.timeIntervalSince1970 / 60).rounded() * 60)) } }
        var builds: [SyncRun] = []
        for st in m.states where st.item.inBuild {
            for j in st.jobs where j.working {
                builds.append(SyncRun(id: j.id, agent: nil, title: j.title, detail: j.detail, since: since[j.id] ?? Date(),
                                      icon: j.tool.flatMap { AppSettings.shared.toolIcons[$0] } ?? st.item.icon))
            }
        }
        for r in builds where since[r.id] == nil { since[r.id] = r.since }
        // forget the ones that stopped (once, after both lists: a build's start time must survive the agents' pass)
        let ids = Set((runs + builds).map(\.id))
        since = since.filter { ids.contains($0.key) }
        // each ask with the notch's icon for its agent, so the phone shows the same one
        let asks = m.asks.map { a in var a = a; a.icon = a.kind.item(in: m.items)?.icon; return a }
        return SyncSnapshot(mac: Self.macName, runs: runs, asks: asks, limits: limits, at: .distantPast,
                            builds: builds, health: health(m), events: events, job: job(builds), notch: .of(AppSettings.shared),
                            crew: runs.min(by: { $0.since < $1.since }).map {
                                SyncCrew(count: runs.count, title: $0.title, detail: Self.bare($0.detail), since: $0.since, agent: $0.agent, icon: $0.icon,
                                         members: Self.members(runs))
                            })
    }

    /// Each agent once (by kind and icon), oldest first, at most four.
    static func members(_ runs: [SyncRun]) -> [SyncCrew.Member] {
        var seen: [SyncCrew.Member] = []
        for r in runs.sorted(by: { $0.since < $1.since }) {
            let m = SyncCrew.Member(agent: r.agent, icon: r.icon)
            if !seen.contains(m) { seen.append(m) }
        }
        return Array(seen.prefix(4))
    }

    /// The Live Activity counts the time itself: "· 0:12" or "· ≈ 40s nữa" in the detail would change it every second.
    static func bare(_ detail: String) -> String {
        detail.replacingOccurrences(of: #"\s·\s(\d+:\d{2}(:\d{2})?|≈[^·]*|\d+\s(giây|phút|giờ|sec|min|hours)[^·]*)"#, with: "", options: .regularExpression)
    }

    /// The longest build at work; when it stops, the same build as done (or failed, when CI said so) for 5 minutes.
    private func job(_ builds: [SyncRun]) -> SyncJob? {
        let now = Date()
        if let b = builds.min(by: { $0.since < $1.since }) {
            followed = b
            ended = nil
            return SyncJob(title: b.title, detail: Self.bare(b.detail), since: b.since, state: "running", ended: nil, icon: b.icon)
        }
        if let f = followed {
            followed = nil
            ended = SyncJob(title: f.title, detail: Self.bare(f.detail), since: f.since, state: "done", ended: now, icon: f.icon)
        }
        guard var e = ended, let at = e.ended, now.timeIntervalSince(at) < 300 else { ended = nil; return nil }
        // a CI result lands around the time its job leaves the list
        if events.contains(where: { $0.kind == "ciFailed" && abs($0.at.timeIntervalSince(at)) < 30 }) { e.state = "failed" }
        return e
    }

    /// CPU and memory in 5 % steps, so the phone sees the trend without a new snapshot every second.
    private func health(_ m: Model) -> SyncHealth {
        let ticks = SystemLoad.cpuTicks()
        let cpu = lastTicks.flatMap { before in ticks.map { SystemLoad.cpu(from: before, to: $0) } }
        lastTicks = ticks
        let step = { (v: Double?) in v.map { Int(($0 * 20).rounded()) * 5 } }
        return SyncHealth(battery: m.battery?.percent, charging: m.battery?.plugged ?? false,
                          thermal: ProcessInfo.processInfo.thermalState.rawValue, temp: Thermals.hottest(m.thermals).map { Int($0.celsius.rounded()) },
                          fan: m.fans.map(\.actual).max(), cpu: step(cpu), memory: step(SystemLoad.memory()))
    }

    /// A notch announcement: CI passed or failed, a task done, a limit near its end, low battery, the Mac running hot.
    func record(_ t: Toast) {
        guard let e = t.event, group != nil,
              t.toPhone || [.ciPassed, .ciFailed, .limitWarn, .lowBattery].contains(e) else { return }
        let ev = SyncEvent(id: UUID().uuidString, kind: e.rawValue, title: t.title, detail: t.subtitle, at: Date())
        events = (events + [ev]).filter { $0.at > Date().addingTimeInterval(-86400) }.suffix(20)
        // the push carries it only when you are away (and limits never wake the phone: the app shows them)
        if (!onlyAway || Self.away), e != .limitWarn, let g = group, let box = try? SyncBox.seal(ev, g.key) {
            eventAt = Int(ev.at.timeIntervalSince1970 * 1000)
            eventBox = box
        }
    }

    /// Away: the screen is locked, or nothing was typed or clicked for 2 minutes.
    nonisolated static var away: Bool {
        let locked = (CGSessionCopyCurrentDictionary() as? [String: Any])?["CGSSessionScreenIsLocked"] as? Bool ?? false
        return locked || CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!) >= 120
    }

    // MARK: Phone → Mac

    /// Firebase's event stream on answers/: each new answer is opened, applied, then deleted.
    /// Reads the group's Pro at most every 2 minutes; a failed read keeps what was known.
    private func checkPro(_ g: SyncGroup, force: Bool = false) async {
        guard force || Date().timeIntervalSince(proAt) > 120 else { return }
        proAt = Date()
        guard let d = try? await call("GET", "groups/\(g.g)/entitlement", nil, group: g),
              let v = try? JSONSerialization.jsonObject(with: d, options: .fragmentsAllowed) else { return }
        let e = v as? [String: Any]
        pro = Self.isPro(e)
        proUntil = pro ? (e?["until"] as? NSNumber)?.doubleValue : nil
        proProduct = pro ? e?["product"] as? String ?? "" : ""
        proTx = pro ? e?["tx"] as? String ?? "" : ""
        if let data = try? await call("GET", "groups/\(g.g)/grants", nil, group: g),
           let grants = try? JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed) as? [String: [String: Any]] {
            proHolders = Set(grants.values.compactMap { grant -> String? in
                guard ((grant["until"] as? NSNumber)?.doubleValue ?? 0) > Date().timeIntervalSince1970 * 1000 else { return nil }
                return grant["uid"] as? String
            })
        } else { proHolders = [] }
        if let data = try? await call("GET", "groups/\(g.g)/proNote", nil, group: g),
           let note = try? JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed) as? [String: Any],
           let why = note["why"] as? String, let at = (note["at"] as? NSNumber)?.doubleValue {
            proNote = (why, at)
            let noteKey = "lastNotchProNoteAt.\(g.g)"
            if at > UserDefaults.standard.double(forKey: noteKey), let message = Self.proNoteText(why) {
                UserDefaults.standard.set(at, forKey: noteKey)
                model?.announce(Toast(symbol: "sparkles", tint: .orange, title: message, event: nil), notify: false)
            }
        } else { proNote = nil }
    }

    static func proNoteText(_ why: String) -> String? {
        switch why {
        case "moved": return L("The group’s Pro moved to another computer")
        case "refunded": return L("The group’s Pro plan was refunded")
        case "expired": return L("The group’s Pro plan has expired")
        default: return nil
        }
    }

    static func isPro(_ e: [String: Any]?, now: Date = Date()) -> Bool {
        (e?["pro"] as? Bool ?? false) && ((e?["until"] as? NSNumber)?.doubleValue ?? 0) > now.timeIntervalSince1970 * 1000
    }

    private func listen() {
        stream?.cancel()
        stream = Task {
            while !Task.isCancelled, let g = group {
                do {
                    var req = URLRequest(url: try await url("groups/\(g.g)/answers", g))
                    req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
                    req.timeoutInterval = 3600
                    let (bytes, resp) = try await URLSession.shared.bytes(for: req)
                    guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.userAuthenticationRequired) }
                    var event = ""
                    for try await line in bytes.lines {
                        if line.hasPrefix("event: ") { event = String(line.dropFirst(7)) }
                        else if line.hasPrefix("data: "), event == "put" || event == "patch" { await handle(String(line.dropFirst(6)), g) }
                        if event == "auth_revoked" || event == "cancel" { token = nil; break }
                    }
                } catch is CancellationError { return } catch {
                    if !Task.isCancelled { problem = L("Lost connection to the phone, retrying…") }
                }
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    private func handle(_ data: String, _ g: SyncGroup) async {
        guard let o = try? JSONSerialization.jsonObject(with: Data(data.utf8)) as? [String: Any], let path = o["path"] as? String else { return }
        // "/" carries every answer (or null); "/<id>" one answer
        let found: [String: Any] = path == "/" ? (o["data"] as? [String: Any] ?? [:]) : [String(path.dropFirst()): o["data"] ?? NSNull()]
        for (id, v) in found {
            guard let v = v as? [String: Any], let box = v["box"] as? String else { continue }
            let fresh = (v["at"] as? Double).map { Date().timeIntervalSince1970 - $0 / 1000 < AgentAsks.wait } ?? false
            if fresh, let a = try? SyncBox.open(box, g.key, as: SyncAnswer.self) { apply(a) }
            try? await call("DELETE", "groups/\(g.g)/answers/\(id)", nil, group: g)
        }
        if problem != nil { problem = nil }
    }

    private func apply(_ a: SyncAnswer) {
        if a.refresh == true {
            sent = nil  // the next publish writes it all
            publish()
            return
        }
        if let t = a.timer {
            if t > 0 { model?.startTimer(minutes: min(t, 180)) } else { model?.stopTimer() }
            publish()
            return
        }
        if let n = a.notch {
            n.apply(to: AppSettings.shared)
            publish()
            return
        }
        guard let model, let ask = model.asks.first(where: { $0.id == a.ask }) else { return }
        Track.event("remote_answer", ["agent": ask.kind.rawValue])
        model.reply(ask, a.reply, remote: true)
        publish()
    }

    // MARK: Same Wi-Fi (Bonjour + TCP, the same sealed boxes as Firebase)

    private func startLAN() {
        listener?.cancel(); listener = nil
        conns.values.forEach { $0.c.cancel() }
        conns = [:]; lanPhones = 0
        guard lan, let g = group, let l = try? NWListener(using: LinkFrames.params) else { return }
        l.service = NWListener.Service(name: LinkFrames.serviceName(g.g), type: LinkFrames.type)
        l.newConnectionHandler = { [weak self] c in MainActor.assumeIsolated { self?.accept(c, g) } }
        l.start(queue: .main)
        listener = l
    }

    private func accept(_ c: NWConnection, _ g: SyncGroup) {
        guard conns.count < 8 else { c.cancel(); return }
        let id = ObjectIdentifier(c)
        conns[id] = (c, false, nil)
        c.stateUpdateHandler = { [weak self] st in
            MainActor.assumeIsolated {
                guard let self else { return }
                switch st {
                case .failed, .cancelled:
                    // gone (left the Wi-Fi, app closed): Firebase takes over at the next tick
                    if self.conns.removeValue(forKey: id)?.ok == true { self.lanPhones -= 1; self.sent = nil }
                default: break
                }
            }
        }
        c.start(queue: .main)
        LinkFrames.read(c) { [weak self] frame in
            MainActor.assumeIsolated {
                // the phone's first frame is a sealed hello: opening it proves it holds the key
                guard let self, let a = try? SyncBox.open(frame.base64EncodedString(), g.key, as: SyncAnswer.self) else { c.cancel(); return }
                if let phone = a.phone { self.conns[id]?.phone = phone }
                if self.conns[id]?.ok == false {
                    self.conns[id]?.ok = true
                    self.lanPhones += 1
                    self.sentLocal = nil  // send it everything now
                    self.publish()
                }
                if Date().timeIntervalSince(a.at) < AgentAsks.wait { self.apply(a) }
            }
        }
        // a connection that never says hello is not a phone
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
            MainActor.assumeIsolated { if self?.conns[id]?.ok == false { c.cancel() } }
        }
    }

    // MARK: REST

    private func idToken(_ g: SyncGroup) async throws -> String {
        if let t = token, t.until > Date() { return t.id }
        let r: [String: String] = try await post("https://securetoken.googleapis.com/v1/token?key=\(Self.apiKey)",
                                                 form: "grant_type=refresh_token&refresh_token=\(g.refresh)")
        guard let id = r["id_token"] else { throw URLError(.userAuthenticationRequired) }
        token = (id, Date().addingTimeInterval(3000))  // Firebase ID tokens last an hour
        return id
    }

    private func url(_ path: String, _ g: SyncGroup) async throws -> URL {
        URL(string: "\(Self.db)/\(path).json?auth=\(try await idToken(g))")!
    }

    @discardableResult
    private func call(_ method: String, _ path: String, _ body: Any?, group g: SyncGroup) async throws -> Data {
        var req = URLRequest(url: try await url(path, g))
        req.httpMethod = method
        if let body { req.httpBody = try JSONSerialization.data(withJSONObject: body) }
        let (d, resp) = try await URLSession.shared.data(for: req)
        guard let code = (resp as? HTTPURLResponse)?.statusCode, code < 300 else {
            if (resp as? HTTPURLResponse)?.statusCode == 401 { token = nil }
            throw NSError(domain: "Firebase", code: (resp as? HTTPURLResponse)?.statusCode ?? 0,
                          userInfo: [NSLocalizedDescriptionKey: String(decoding: d, as: UTF8.self)])
        }
        return d
    }

    func sendReport(topic: String, text: String, contact: String) async throws -> String {
        let g: SyncGroup
        if let group { g = group }
        else {
            let r: [String: String] = try await post("https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=\(Self.apiKey)", json: ["returnSecureToken": true])
            guard let id = r["idToken"], let uid = r["localId"], let refresh = r["refreshToken"] else { throw URLError(.userAuthenticationRequired) }
            token = (id, Date().addingTimeInterval(3000))
            g = SyncGroup(g: "", k: "", uid: uid, refresh: refresh)
        }
        let body = MacReport.payload(uid: g.uid, g: g.g, phones: phones.count, proUntil: proUntil,
                                     product: proProduct, tx: proTx,
                                     kind: topic, text: text, contact: contact)
        let data = try await call("POST", "reports", body, group: g)
        guard let id = (try JSONSerialization.jsonObject(with: data) as? [String: String])?["name"] else { throw URLError(.badServerResponse) }
        return id
    }

    private func post(_ url: String, json: Any? = nil, form: String? = nil) async throws -> [String: String] {
        var req = URLRequest(url: URL(string: url)!)
        req.httpMethod = "POST"
        if let json { req.httpBody = try JSONSerialization.data(withJSONObject: json); req.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        if let form { req.httpBody = Data(form.utf8); req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type") }
        let (d, _) = try await URLSession.shared.data(for: req)
        let o = try JSONSerialization.jsonObject(with: d) as? [String: Any] ?? [:]
        return o.compactMapValues { $0 as? String }
    }

    nonisolated static func pairURL(_ text: String) -> String {
        let frag = text.addingPercentEncoding(withAllowedCharacters: .urlFragmentAllowed) ?? text
        return "https://notchtasks.web.app/pair#" + frag
    }

    nonisolated static func pairingText(from urlString: String) -> String? {
        guard let url = URL(string: urlString.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "https",
              url.host == "notchtasks.web.app",
              url.path == "/pair",
              let frag = url.fragment, !frag.isEmpty else { return nil }
        return frag.removingPercentEncoding
    }

    static func qr(_ text: String) -> NSImage? {
        let f = CIFilter.qrCodeGenerator()
        f.message = Data(text.utf8)
        f.correctionLevel = "M"
        guard let ci = f.outputImage?.transformed(by: .init(scaleX: 10, y: 10)),
              let cg = CIContext().createCGImage(ci, from: ci.extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: ci.extent.width / 2, height: ci.extent.height / 2))
    }

    static func selfTest() {
        let key = SymmetricKey(size: .bits256)
        SyncNotch.selfTest()
        MacReport.selfTest()
        // the push function is told at once when agents start or all stop, and at most every 10 minutes in between
        let c0 = Date(timeIntervalSince1970: 1000), idle = (count: 0, at: 0, stamp: Date.distantPast)
        let on = crewSignal(count: 1, at: 5, last: idle, now: c0)
        precondition(on.count == 1 && on.stamp == c0, "agents starting is signalled")
        precondition(crewSignal(count: 3, at: 5, last: on, now: c0.addingTimeInterval(599)).count == 1, "a count change waits")
        precondition(crewSignal(count: 3, at: 5, last: on, now: c0.addingTimeInterval(600)).count == 3, "then goes")
        precondition(crewSignal(count: 0, at: 0, last: on, now: c0.addingTimeInterval(1)).count == 0, "all stopping is signalled at once")
        let a = SyncAnswer(ask: "x", reply: .answers(["Q": "A"]), at: Date(timeIntervalSince1970: 1))
        precondition((try? SyncBox.open(SyncBox.seal(a, key), key, as: SyncAnswer.self)) == a, "sync box round trip")
        let samplePair = #"{"v":1,"g":"group123456789012","k":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","n":"MacBook Pro"}"#
        let sample = try? JSONSerialization.jsonObject(with: Data(samplePair.utf8)) as? [String: Any]
        precondition((sample?["k"] as? String).flatMap { Data(base64url: $0) }?.count == 32, "sample pairing key is 32 bytes")
        precondition(pairingText(from: pairURL(samplePair)) == samplePair, "pairing URL round-trips")
        let p0 = Date(timeIntervalSince1970: 1000)
        precondition(isPro(["pro": true, "until": 1_000_001], now: p0) && !isPro(["pro": true, "until": 999_999], now: p0)
                     && !isPro(nil, now: p0) && !isPro(["pro": false, "until": 2_000_000], now: p0), "pro entitlement")
        precondition(proNoteText("moved") == L("The group’s Pro moved to another computer")
                     && proNoteText("refunded") == L("The group’s Pro plan was refunded")
                     && proNoteText("expired") == L("The group’s Pro plan has expired")
                     && proNoteText("unknown") == nil, "pro notice texts")
        precondition(PhonePane.status(until: nil, now: p0) == L("No Pro: one phone, basic notifications")
                     && PhonePane.status(until: 32_503_680_000_000, now: p0) == L("Pro lifetime")
                     && PhonePane.status(until: 1_001_000, now: p0).hasPrefix("Pro until "), "pro status lines")
        let legacy = Data(#"{"ask":"x","reply":{"pass":{}},"at":0}"#.utf8)
        precondition((try? JSONDecoder().decode(SyncAnswer.self, from: legacy))?.phone == nil, "old answers still decode")
        let hello = SyncAnswer(ask: "", reply: .pass, at: Date(timeIntervalSince1970: 1), phone: "uid-1")
        precondition((try? SyncBox.open(SyncBox.seal(hello, key), key, as: SyncAnswer.self))?.phone == "uid-1", "sealed hello names the phone")
        let d = Data.random(32)
        precondition(Data(base64url: d.base64url) == d, "base64url round trip")
        let g = SyncGroup(g: Data.random(16).base64url, k: d.base64url, uid: "u", refresh: "r")
        let code = try? JSONSerialization.jsonObject(with: Data(g.code.utf8)) as? [String: Any]
        precondition(code?["v"] as? Int == 1 && code?["g"] as? String == g.g && code?["k"] as? String == g.k, "pairing code")
        precondition(qr(g.code) != nil, "qr image")
        precondition(bare("clean · NotchTasks · 0:04 · ≈ 40s left") == "clean · NotchTasks", "job detail without the clock")
        precondition(bare("Working · 9 min") == "Working" && bare("Đang làm việc · 9 phút") == "Đang làm việc", "agent detail without its minutes")
        let t0 = Date(timeIntervalSince1970: 0)
        let crew = members([SyncRun(id: "b", agent: .codex, title: "", detail: "", since: t0.addingTimeInterval(5)),
                            SyncRun(id: "a", agent: .claude, title: "", detail: "", since: t0), SyncRun(id: "c", agent: .claude, title: "", detail: "", since: t0)])
        precondition(crew.map(\.agent) == [.claude, .codex], "each agent once, oldest first")
        // an ask answered in the agent: its own tool call gets a result, other calls (in parallel) do not count
        let log: [[String: Any]] = [
            ["type": "assistant", "message": ["content": [["type": "tool_use", "id": "t1", "name": "Bash", "input": ["command": "ls"]],
                                                          ["type": "tool_use", "id": "t2", "name": "Bash", "input": ["command": "rm x"]]]]],
            ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "t1"]]]],
        ]
        let call = AgentAsks.pendingCall(.claude, tool: "Bash", input: ["command": "rm x"], in: log)
        precondition(call == "t2" && !AgentAsks.settled(.claude, call: "t2", in: log), "another call's result is no answer")
        precondition(AgentAsks.settled(.claude, call: "t2", in: log + [["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": "t2"]]]]]),
                     "its own result is")
        let codex: [[String: Any]] = [["payload": ["type": "function_call", "call_id": "c1"]], ["payload": ["type": "function_call_output", "call_id": "c1"]]]
        precondition(AgentAsks.settled(.codex, call: AgentAsks.pendingCall(.codex, tool: "shell", input: nil, in: codex) ?? "", in: codex), "Codex ran it")
        let long = AgentAsk(id: "a", pid: 1, session: "s", tool: "Bash", summary: String(repeating: "x", count: 5000), questions: [], at: Date(), agent: .claude)
        precondition(((try? SyncBox.seal(long.forPush, key))?.count ?? 9999) < 3000, "push box fits APNs")
        let body = Data(repeating: 7, count: 300)
        let f = LinkFrames.wrap(body)
        precondition(f.count == 304 && LinkFrames.size(f.prefix(4)) == 300 && f.dropFirst(4) == body, "frame")
        precondition(LinkFrames.serviceName("abc") == LinkFrames.serviceName("abc") && !LinkFrames.serviceName("abc").contains("abc"), "service name")
    }
}

/// Length-prefixed frames over TCP: 4 bytes big-endian size, then one AES-GCM box (combined bytes).
enum LinkFrames {
    static let type = "_notchtasks._tcp"
    /// The Bonjour name carries a hash of the group, never the group: the phone finds its own Mac, a stranger learns nothing.
    static func serviceName(_ g: String) -> String {
        "NotchTasks " + SHA256.hash(data: Data(g.utf8)).prefix(6).map { String(format: "%02x", $0) }.joined()
    }
    static var params: NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true  // a phone that left the Wi-Fi is noticed in ~15 s
        tcp.keepaliveIdle = 5
        tcp.keepaliveInterval = 3
        tcp.keepaliveCount = 3
        tcp.connectionTimeout = 5
        return NWParameters(tls: nil, tcp: tcp)
    }
    static func wrap(_ d: Data) -> Data { withUnsafeBytes(of: UInt32(d.count).bigEndian) { Data($0) } + d }
    static func size(_ head: Data) -> Int { head.reduce(0) { $0 << 8 | Int($1) } }

    static func read(_ c: NWConnection, _ handle: @escaping (Data) -> Void) {
        c.receive(minimumIncompleteLength: 4, maximumLength: 4) { head, _, _, err in
            guard err == nil, let head, head.count == 4 else { c.cancel(); return }
            let n = size(head)
            guard n > 0, n < 1_000_000 else { c.cancel(); return }
            c.receive(minimumIncompleteLength: n, maximumLength: n) { body, _, _, err in
                guard err == nil, let body, body.count == n else { c.cancel(); return }
                handle(body)
                read(c, handle)
            }
        }
    }
}

/// The group lives in the login Keychain, never in preferences.
enum SyncKeychain {
    private static let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                               kSecAttrService as String: "com.notchtasks.sync", kSecAttrAccount as String: "group"]
    static func load() -> SyncGroup? {
        var q = query
        q[kSecReturnData as String] = true
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let d = out as? Data else { return nil }
        return try? JSONDecoder().decode(SyncGroup.self, from: d)
    }
    static func save(_ g: SyncGroup) {
        clear()
        var q = query
        q[kSecValueData as String] = try? JSONEncoder().encode(g)
        SecItemAdd(q as CFDictionary, nil)
    }
    static func clear() { SecItemDelete(query as CFDictionary) }
}

// MARK: - Settings: pair the iPhone app

struct PhonePane: View {
    @ObservedObject var sync: SyncHub
    @State private var showCode = false
    @State private var busy = false
    @State private var showReport = false

    static func status(until: Double?, now: Date = Date()) -> String {
        guard let until, until > now.timeIntervalSince1970 * 1000 else {
            return L("No Pro: one phone, basic notifications")
        }
        return until >= 30_000_000_000_000 ? L("Pro lifetime")
            : L("Pro until {0}", Date(timeIntervalSince1970: until / 1000).formatted(date: .abbreviated, time: .omitted))
    }

    private var proStatus: String { Self.status(until: sync.proUntil) }

    var body: some View {
        Form {
            Section {
                Text(proStatus).font(.subheadline.weight(.medium))
                if let note = sync.proNote, let text = SyncHub.proNoteText(note.why) {
                    Text(text).font(.footnote).foregroundStyle(.secondary)
                }
                Text(L("Answer agents and see what's running right on your phone. Data is encrypted between your devices."))
                    .foregroundStyle(.secondary)
                if sync.phones.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(L("1. Install NotchTasks on your phone"))
                        Text(L("2. Click Pair device"))
                        Text(L("3. Scan the QR code with the app or Camera"))
                    }
                    .font(.subheadline).padding(.vertical, 4)
                    if let g = sync.group {
                        VStack(alignment: .center, spacing: 10) {
                            if let img = SyncHub.qr(g.pairURL) {
                                Image(nsImage: img).interpolation(.none).resizable().frame(width: 240, height: 240)
                                    .padding(14).background(.white, in: RoundedRectangle(cornerRadius: 14))
                            }
                            Text(L("This code only works for your devices.")).font(.callout).foregroundStyle(.secondary)
                            Button(L("Copy code")) {
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(g.pairURL, forType: .string)
                            }
                        }
                        .frame(maxWidth: .infinity).padding(.vertical, 6)
                    } else {
                        Button(busy ? L("Creating code…") : L("Pair device…")) {
                            busy = true
                            Task { await sync.create(); busy = false; showCode = sync.group != nil }
                        }
                        .disabled(busy)
                    }
                } else if let g = sync.group {
                    if showCode {
                        HStack(spacing: 16) {
                            if let img = SyncHub.qr(g.pairURL) {
                                Image(nsImage: img).interpolation(.none).resizable().frame(width: 240, height: 240)
                                    .padding(14).background(.white, in: RoundedRectangle(cornerRadius: 14))
                            }
                            VStack(alignment: .leading, spacing: 8) {
                                Text(L("Open NotchTasks on your phone → Pair with computer, then scan this code."))
                                Text(L("This code only works for your devices.")).font(.callout).foregroundStyle(.secondary)
                                HStack {
                                    Button(L("Copy code")) {
                                        NSPasteboard.general.clearContents()
                                        NSPasteboard.general.setString(g.pairURL, forType: .string)
                                    }
                                    Button(L("Hide code")) { showCode = false }
                                }
                            }
                        }
                    } else {
                        Button(L("Pair another device…")) { showCode = true }
                    }
                }
                if let p = sync.problem { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
                Button(L("Report a problem")) { showReport = true }
            }
            if sync.group != nil, !sync.phones.isEmpty {
                Section {
                    Toggle(L("Connect directly on the same Wi‑Fi"), isOn: $sync.lan)
                    Toggle(L("Only alert my phone when I'm away"), isOn: $sync.onlyAway)
                    LabeledContent(L("Connecting…"), value: sync.lanPhones > 0 ? L("{0} devices over Wi‑Fi", sync.lanPhones) : "Qua Internet")
                } footer: {
                    Text(L("On the same Wi‑Fi your phone gets updates instantly; elsewhere, over the Internet. While you're using this computer, your phone stays quiet."))
                }
                Section {
                    ForEach(sync.phones.sorted { $0.value < $1.value }, id: \.key) { entry in
                        HStack {
                            Label(entry.value, systemImage: "iphone")
                            Spacer()
                            if sync.proHolders.contains(entry.key) { Text("Pro").font(.caption.weight(.semibold)).foregroundStyle(.orange) }
                        }
                    }
                    Button(L("Unpair all"), role: .destructive) {
                        busy = true
                        Task { await sync.unpairAll(); busy = false; showCode = false }
                    }
                    .disabled(busy)
                } header: {
                    Text(L("Paired"))
                } footer: {
                    Text(L("Every device will disconnect from this computer. Pairing again uses a new code."))
                }
            }
        }
        .formStyle(.grouped)
        .sheet(isPresented: $showReport) { MacReportSheet(sync: sync) }
        .padding(.horizontal, -20)
        .onDisappear { showCode = false }  // the code is the key: never left on screen
        // new phones show up while the pane is open
        .task(id: sync.group?.g) { while !Task.isCancelled && sync.group != nil { await sync.loadPhones(); try? await Task.sleep(for: .seconds(4)) } }
    }
}
