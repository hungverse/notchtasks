// Cross-check of the Windows app's wire format with the Swift side, using the phone's own types (iOS/Shared, field for
// field the Mac's Sources/Sync.swift) and CryptoKit. Run by windows/tools/crosscheck.sh:
//   gen <dir>    writes fixtures sealed here, for the Node tests to open (test/fixtures/crosscheck, checked in)
//   check <dir>  opens the boxes the Node tests sealed (js-*), with the phone's types, as the phone would
import Foundation
import CryptoKit

let args = CommandLine.arguments
func fail(_ m: String) -> Never { FileHandle.standardError.write(Data("FAIL: \(m)\n".utf8)); exit(1) }
guard args.count == 3 else { fail("usage: crosscheck gen|check <dir>") }
let dir = URL(fileURLWithPath: args[2])
let keyData = Data((0..<32).map { UInt8($0 * 7 & 0xFF) })  // a test key: the Node side reads it from key.txt
let key = SymmetricKey(data: keyData)
let k64 = keyData.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
let t0 = Date(timeIntervalSince1970: 1_794_380_000.25)
let groupID = "group123456789012"
let userName = "Hùng’s MacBook \"Pro\""

func write(_ name: String, _ text: String) { try! text.write(to: dir.appendingPathComponent(name), atomically: true, encoding: .utf8) }
func read(_ name: String) -> String {
    guard let s = try? String(contentsOf: dir.appendingPathComponent(name), encoding: .utf8) else { fail("missing \(name)") }
    return s.trimmingCharacters(in: .whitespacesAndNewlines)
}
func openBox<T: Decodable>(_ name: String, _ type: T.Type) -> T {
    do { return try Box.open(read(name), key, as: type) } catch { fail("\(name): \(error)") }
}
func near(_ d: Date, _ s: Double) -> Bool { abs(d.timeIntervalSince1970 - s) < 0.002 }

if args[1] == "gen" {
    var notch = MacNotch.sample
    notch.accent = "custom"
    notch.customAccent = "#00F5D4"
    notch.badges.append(MacBadge(id: UUID(uuidString: "6F9619FF-8B86-D011-B42D-00C04FC964FF")!, symbol: "bolt.fill", text: "AI",
                                 color: "#FF375F", shape: "rounded", border: true, right: true))
    let ask = AgentAsk(id: "a1", pid: 4242, session: "NotchTasks", tool: "Bash", summary: "rm -rf build", questions: [], at: t0,
                       agent: .claude, icon: .sprite("px-cat"))
    let q = AgentAsk(id: "a2", pid: 1, session: "app", tool: "AskUserQuestion", summary: "Which DB?",
                     questions: [.init(question: "Which DB?", header: "DB", options: ["SQLite", "JSON"], multiSelect: false)], at: t0, agent: .codex)
    let snap = Snapshot(mac: userName, runs: [AgentRun(id: "r1", agent: .gemini, title: "api", detail: "Đang chạy test", since: t0)],
                        asks: [ask, q], limits: [Limit(provider: "Claude", label: "5h", used: 72, resets: t0.addingTimeInterval(3600))], at: t0,
                        builds: [AgentRun(id: "b1", agent: nil, title: "Xcode", detail: "build", since: t0)],
                        health: MacHealth(battery: 64, charging: false, thermal: 1, temp: 52, fan: 3200, cpu: 45, memory: 70),
                        events: [MacEvent(id: "e1", kind: "ciFailed", title: "CI lỗi", detail: "deploy", at: t0)],
                        job: JobFocus(title: "Xcode", detail: "build", since: t0, state: "running", ended: nil, icon: .sprite("px-gears")),
                        notch: notch, crew: CrewFocus(count: 1, title: "api", detail: "x", since: t0, agent: .gemini, icon: nil, members: [.init(agent: .gemini, icon: nil)]))
    write("key.txt", k64)
    write("snapshot.box", try! Box.seal(snap, key))
    let replies: [(String, Answer)] = [
        ("allow", Answer(ask: "a1", reply: .allow, at: t0)),
        ("deny", Answer(ask: "a1", reply: .deny, at: t0)),
        ("pass", Answer(ask: "a1", reply: .pass, at: t0)),
        ("answers", Answer(ask: "a2", reply: .answers(["Which DB?": "SQLite, JSON"]), at: t0)),
        ("hello", Answer(ask: "", reply: .pass, at: t0, phone: "uid-1")),
        ("timer", Answer(ask: "", reply: .pass, at: t0, timer: 25)),
        ("refresh", Answer(ask: "", reply: .pass, at: t0, refresh: true)),
        ("notch", Answer(ask: "", reply: .pass, at: t0, notch: notch)),
    ]
    for (name, a) in replies { write("swift-\(name).box", try! Box.seal(a, key)) }
    write("pair.txt", #"{"v":1,"g":"\#(groupID)","k":"\#(k64)","n":"Hùng's Mac"}"#)
    write("pair-url.txt", "https://notchtasks.web.app/pair#" + #"{"v":1,"g":"\#(groupID)","k":"\#(k64)","n":"Hùng's Mac"}"#.addingPercentEncoding(withAllowedCharacters: .urlFragmentAllowed)!)
    write("service.txt", LinkFrames.serviceName(groupID))
    print("generated in \(dir.path)")
} else if args[1] == "check" {
    // the Node tests sealed these with the same key: each must open with the phone's types and say what the Mac meant
    let s = openBox("js-snapshot.box", Snapshot.self)
    guard s.mac == userName, s.runs.count == 1, s.runs[0].agent == .gemini, s.runs[0].icon?.sprite == "px-gears" else { fail("snapshot runs/mac: \(s)") }
    guard s.asks.count == 2, s.asks[0].summary == "rm -rf build", s.asks[0].icon?.sprite == "px-cat", s.asks[0].icon?.plain == true,
          s.asks[1].agent == .codex, s.asks[1].questions.first?.options == ["SQLite", "JSON"], s.asks[1].questions.first?.header == "DB"
    else { fail("snapshot asks: \(s.asks)") }
    guard s.limits.count == 1, s.limits[0].provider == "Claude", s.limits[0].used == 72, near(s.limits[0].resets, 1_794_380_000.25 + 3600) else { fail("snapshot limits") }
    guard s.builds?.count == 1, s.builds?[0].agent == nil, s.builds?[0].title == "Xcode" else { fail("snapshot builds") }
    guard s.health?.battery == 64, s.health?.charging == false, s.health?.thermal == 1, s.health?.temp == 52, s.health?.fan == 3200,
          s.health?.cpu == 45, s.health?.memory == 70 else { fail("snapshot health") }
    guard s.events?.count == 1, s.events?[0].kind == "ciFailed", s.events?[0].title == "CI lỗi" else { fail("snapshot events") }
    guard s.job?.state == "running", s.job?.ended == nil, s.job?.icon?.sprite == "px-gears" else { fail("snapshot job") }
    guard let n = s.notch, n.badges.count == 3, n.badges[2].text == "AI", n.badges[2].border, n.badges[2].shape == "rounded",
          n.badges[2].right, n.accent == "custom", n.customAccent == "#00F5D4", n.toolIcons["Xcode"]?.sprite == "px-gears",
          n.modules.count == 16, n.lockShows == ["lock"], n.seenTools == ["Xcode", "Gradle", "GitHub Actions"] else { fail("snapshot notch") }
    guard let c = s.crew, c.count == 1, c.title == "api", c.members?.count == 1, c.members?[0].agent == .gemini else { fail("snapshot crew") }
    guard near(s.at, 1_794_380_000.25) else { fail("snapshot date \(s.at.timeIntervalSince1970)") }
    print("ok snapshot")

    let a = openBox("js-ask.box", AgentAsk.self)
    guard a.id == "js-a1", a.pid == 4242, a.summary.count == 300, a.questions.count == 2, a.questions[0].question.count == 200,
          a.questions[0].options.count == 4, a.questions[0].options.allSatisfy({ $0.count <= 60 }), a.agent == .opencode
    else { fail("ask trimmed for push: \(a)") }
    guard near(a.at, 1_794_380_000.25) else { fail("ask date") }
    print("ok ask")

    let e = openBox("js-event.box", MacEvent.self)
    guard e.id == "js-e1", e.kind == "taskDone", e.title == "Xong", near(e.at, 1_794_380_000.25) else { fail("event: \(e)") }
    print("ok event")

    // the 4-byte big-endian frame around a box, as LinkFrames.read on the Mac reads it
    guard let frame = Data(base64Encoded: read("js-frame.b64")), frame.count > 4 else { fail("frame: not base64") }
    guard LinkFrames.size(frame.prefix(4)) == frame.count - 4 else { fail("frame: length prefix") }
    let framed = (try? Box.open(frame.dropFirst(4).base64EncodedString(), key, as: Snapshot.self))?.mac
    guard framed == userName else { fail("frame: does not open as a snapshot") }
    print("ok frame")
} else {
    fail("unknown command \(args[1])")
}
