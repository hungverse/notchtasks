import Foundation
import SQLite3

/// Antigravity's agent lives in a language server and mostly waits on the model, so CPU says nothing about whether it
/// works. Each conversation is a SQLite file, and its last step records what the agent is doing right now.
final class AntigravityScanner {
    private let root: URL
    private var cache: [String: (stamp: Date, active: Bool, title: String)] = [:]
    private var runs: [String: (since: Date, seen: Date)] = [:]

    init(root: URL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini/antigravity/conversations")) { self.root = root }

    func jobs(now: Date) -> [Job] {
        let files = (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
        var out: [Job] = [], live: [String: (since: Date, seen: Date)] = [:]
        for db in files where db.pathExtension == "db" {
            // a step left "generating" by a crash stays that way: only a conversation written lately counts
            guard let stamp = Self.stamp(db), now.timeIntervalSince(stamp) < 10 * 60 else { continue }
            let id = db.lastPathComponent
            if cache[id]?.stamp != stamp, let s = Self.read(db) { cache[id] = (stamp, s.active, s.title) }
            guard let e = cache[id] else { continue }
            let run = runs[id]
            // a reply that calls a tool looks finished until the tool's step is written, sometimes a minute later;
            // only a conversation already seen working is held, so opening an old one doesn't count
            let held = run.map { now.timeIntervalSince(max($0.seen, stamp)) < 45 } ?? false
            guard e.active || held else { continue }
            let since = run?.since ?? now
            live[id] = (since, e.active ? now : run?.seen ?? now)
            out.append(Job(id: id, title: e.title, detail: L("Đang chạy · {0}", ago(now.timeIntervalSince(since))), working: true))
        }
        runs = live
        return out
    }

    /// When the conversation last changed. An empty log is only what a reader leaves behind.
    private static func stamp(_ db: URL) -> Date? {
        let wal = try? URL(fileURLWithPath: db.path + "-wal").resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
        let main = try? db.resourceValues(forKeys: [.contentModificationDateKey])
        return [main?.contentModificationDate, (wal?.fileSize ?? 0) > 0 ? wal?.contentModificationDate : nil].compactMap { $0 }.max()
    }

    /// Mid-turn: a step still in progress (pending, running, generating, waiting, queued), or a finished tool step the model
    /// has yet to answer. A turn ends on a finished reply (type 15, rarely 17); 6 and 7 appear after a stop or an error.
    static func midTurn(type: Int32, status: Int32) -> Bool {
        [1, 2, 8, 9, 11].contains(status) || (status == 3 && ![15, 17].contains(type))
    }

    private static func read(_ url: URL) -> (active: Bool, title: String)? {
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        guard sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK else { return nil }
        sqlite3_busy_timeout(db, 100)
        func row<T>(_ sql: String, _ value: (OpaquePointer?) -> T) -> T? {
            var st: OpaquePointer?
            defer { sqlite3_finalize(st) }
            guard sqlite3_prepare_v2(db, sql, -1, &st, nil) == SQLITE_OK, sqlite3_step(st) == SQLITE_ROW else { return nil }
            return value(st)
        }
        guard let last = row("select step_type, status from steps order by idx desc limit 1", { (sqlite3_column_int($0, 0), sqlite3_column_int($0, 1)) }) else { return nil }
        let blob = row("select data from trajectory_metadata_blob limit 1") { st -> Data in
            guard let p = sqlite3_column_blob(st, 0) else { return Data() }
            return Data(bytes: p, count: Int(sqlite3_column_bytes(st, 0)))
        }
        return (midTurn(type: last.0, status: last.1), blob.flatMap(project) ?? "Antigravity")
    }

    /// The workspace folder in the conversation's protobuf metadata. Its strings carry their length, and the byte after one can be printable.
    static func project(_ blob: Data) -> String? {
        guard let r = blob.range(of: Data("file:///".utf8)), r.lowerBound > blob.startIndex else { return nil }
        let at = r.lowerBound
        let low = Int(blob[at - 1]), high = at - 2 >= blob.startIndex ? Int(blob[at - 2]) : 0
        let count = high >= 0x80 ? (high & 0x7f) | low << 7 : low  // 128+ takes two varint bytes
        guard at + count <= blob.endIndex, let uri = String(data: blob[at..<(at + count)], encoding: .utf8) else { return nil }
        return URL(string: uri)?.lastPathComponent
    }

    static func selfTest() {
        var short = Data([0x0a, 26]); short += Data("file:///Users/x/NotchTasks".utf8); short.append(0x7a)
        precondition(project(short) == "NotchTasks", "antigravity project stops at its length")
        let path = "file:///" + String(repeating: "a", count: 121) + "/NT"
        var long = Data([0x0a, 0x84, 0x01]); long += Data(path.utf8); long.append(0x7a)
        precondition(project(long) == "NT", "antigravity project long length")
        precondition(midTurn(type: 15, status: 8) && midTurn(type: 132, status: 3) && midTurn(type: 21, status: 2), "antigravity mid-turn")
        precondition(!midTurn(type: 15, status: 3) && !midTurn(type: 21, status: 6) && !midTurn(type: 21, status: 7), "antigravity turn over")

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        var w: OpaquePointer?
        precondition(sqlite3_open(dir.appendingPathComponent("a.db").path, &w) == SQLITE_OK, "antigravity test db")
        defer { sqlite3_close(w) }
        precondition(sqlite3_exec(w, "pragma journal_mode=wal; create table steps(idx integer, step_type integer, status integer); create table trajectory_metadata_blob(data blob); insert into steps values(1,15,8);", nil, nil, nil) == SQLITE_OK, "antigravity test schema")
        let s = AntigravityScanner(root: dir), t = Date()
        precondition(s.jobs(now: t).first?.working == true, "antigravity generating shows")
        precondition(AntigravityScanner(root: dir).jobs(now: t.addingTimeInterval(11 * 60)).isEmpty, "antigravity step left by a crash")
        precondition(sqlite3_exec(w, "insert into steps values(2,15,3);", nil, nil, nil) == SQLITE_OK, "antigravity test update")
        precondition(AntigravityScanner(root: dir).jobs(now: t.addingTimeInterval(1)).isEmpty, "antigravity idle conversation stays off")
        precondition(s.jobs(now: t.addingTimeInterval(1)).count == 1, "antigravity holds between steps")
        precondition(s.jobs(now: t.addingTimeInterval(60)).isEmpty, "antigravity done leaves")
    }
}
