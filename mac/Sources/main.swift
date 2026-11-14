import ImageIO
import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio
import EventKit

// MARK: - Entry

@MainActor func selfTest() {
    let log = """
    {"timestamp":"2026-11-02T10:20:22.796Z","type":"event_msg","payload":{"type":"task_started","turn_id":"a"}}
    {"timestamp":"2026-11-02T10:20:30.000Z","type":"response_item","payload":{"type":"message","text":"\\"type\\":\\"task_complete\\""}}
    """
    precondition(CodexStatus.parse(log)?.working == true, "started, quoted text ignored")
    let done = log + "\n" + #"{"timestamp":"2026-11-02T10:20:32.331Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"a"}}"#
    precondition(CodexStatus.parse(done)?.working == false, "complete")
    precondition(CodexStatus.parse("{}") == nil, "no events")
    let rl = #"{"timestamp":"2026-11-03T03:37:25.781Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":2.0,"window_minutes":300,"resets_at":4000000000},"secondary":{"used_percent":18.0,"window_minutes":10080,"resets_at":1}}}}"#
    let w = CodexStatus.limits(rl, now: Date())?.windows
    precondition(w?.count == 2 && w?[0].label == "5h" && w?[0].used == 2 && w?[1].label == "7d" && w?[1].used == 0, "codex limits, expired window = 0")
    // a long turn pushes task_started far from the end; guardian subagent threads are not tasks
    let dir = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-selftest-\(getpid())")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: dir) }
    let mainFile = dir.appendingPathComponent("rollout-main.jsonl"), child = dir.appendingPathComponent("rollout-child.jsonl")
    let stamp = CodexStatus.iso.string(from: Date())
    let started = #"{"timestamp":""# + stamp + #"","type":"event_msg","payload":{"type":"task_started"}}"#
    let junk = String(repeating: #"{"type":"response_item","payload":{"text":"x"}}"# + "\n", count: 40_000)  // ~2 MB
    try? (#"{"payload":{"cwd":"/x/proj"}}"# + "\n" + started + "\n" + junk).write(to: mainFile, atomically: true, encoding: .utf8)
    try? (#"{"payload":{"parent_thread_id":"a"}}"# + "\n" + started + "\n").write(to: child, atomically: true, encoding: .utf8)
    let scanner = CodexScanner(root: dir)
    var jobs = scanner.jobs(now: Date())
    precondition(jobs.count == 1 && jobs[0].working && jobs[0].title == "proj", "codex: event 2 MB from the end, subagent skipped")
    let h = try! FileHandle(forWritingTo: mainFile)
    h.seekToEndOfFile(); h.write(Data((junk + #"{"timestamp":""# + stamp + #"","type":"event_msg","payload":{"type":"task_comp"#).utf8))
    jobs = scanner.jobs(now: Date())
    precondition(jobs.first?.working == true, "codex: half-written event keeps the status")
    h.write(Data((#"lete"}}"# + "\n").utf8)); try? h.close()
    jobs = scanner.jobs(now: Date())
    precondition(jobs.first?.working == false, "codex: completed after the line is finished")
    precondition(Proc.buildsProject("/Users/x/builds/12onLaEWr/0/acme/mobile-app/android") == "acme/mobile-app", "builds path")
    precondition(Proc.buildsProject("/Users/x/builds/t/0/grp/app.tmp") == "grp/app" && Proc.buildsProject("/Users/x/code") == nil, "builds path edge")
    precondition(Proc.ciEnv(getpid())["CI_SELFTEST"] == "1", "reads CI_* env (build.sh sets CI_SELFTEST=1)")
    let runnerLog = "\u{1B}[0;33mWARNING: Job failed: exit status 65 \u{1B}[0;m \u{1B}[0;33mexit_code\u{1B}[0;m=65 \u{1B}[0;33mfailure_reason\u{1B}[0;m=script_failure \u{1B}[0;33mjob\u{1B}[0;m=17684692495 \u{1B}[0;33mjob_name\u{1B}[0;m=\"test suite\" \u{1B}[0;33mproject_full_path\u{1B}[0;m=acme/mobile-app\nJob succeeded job=1 job_name=verify"
    let received = "Checking for jobs... received job=9 job_name=android:production repo_url=https://gitlab.com/g/app.git\n"
        + "Checking for jobs... received job=17684692495 job_name=x repo_url=https://gitlab.com/acme/mobile-app.git\n"
    let parsed = RunnerLog.parse(received + runnerLog)
    precondition(parsed.running == [RunnerJob(id: "9", name: "android:production", project: "g/app", url: RunnerLog.jobURL("g/app", "9"))],
                 "running = received without a result line")
    let np = NowPlaying.parse("playing\nSong\nArtist\n215,5\n12.3\nhttps://i.scdn.co/image/x\n", source: "com.spotify.client")
    precondition(np?.playing == true && np?.title == "Song" && np?.duration == 215.5 && np?.position == 12.3
                 && np?.artwork?.host == "i.scdn.co", "now playing parse, comma decimals")
    precondition(NowPlaying.parse("stopped", source: "x") == nil && NowPlaying.parse("paused\nA\n\n0\n0\n", source: "x")?.artist == nil, "stopped / no artist")
    precondition(mmss(65) == "1:05" && mmss(3725) == "1:02:05" && mmss(-3) == "0:00", "timer format")
    let ev = EKEvent(eventStore: EKEventStore())
    ev.location = L("Phòng A / https://us02web.zoom.us/j/123?pwd=x")
    ev.notes = "fallback https://meet.google.com/abc-defg-hij"
    precondition(meetingLink(ev)?.host == "us02web.zoom.us", "meeting link from location")
    if let b = BatteryState.read() { precondition((0...100).contains(b.percent), "battery percent") }
    let bt = #"{"SPBluetoothDataType":[{"device_connected":[{"Hùng Airpod":{"device_minorType":"Headphones","device_batteryLevelLeft":"100%","device_batteryLevelRight":"12%","device_batteryLevelCase":"5%"}},{"K68":{"device_minorType":"Keyboard"}}]}]}"#
    let devs = Devices.parseBluetooth(Data(bt.utf8))
    precondition(devs.count == 2 && devs[0].symbol == "airpods" && devs[0].battery == "L 100% · R 12% · Hộp 5%" && devs[0].lowest == 12
                 && devs[1].symbol == "keyboard" && devs[1].battery == nil, "bluetooth parse, case battery not counted as low")
    let fails = parsed.failed
    precondition(fails.count == 1 && fails[0].id == "17684692495" && fails[0].name == "test suite"
                 && fails[0].reason == "script_failure · exit 65"
                 && fails[0].url?.absoluteString == "\(RunnerLog.server)/acme/mobile-app/-/jobs/17684692495", "runner log parse")
    // dev task rules (argv as ps shows it)
    let xb = DevRules.classify("xcodebuild", ["xcodebuild", "-workspace", "/b/mobile-app/ios/Runner.xcworkspace", "-scheme", "Runner", "archive"])
    precondition(xb?.tool == "Xcode" && xb?.action == "archive · Runner" && xb?.subject == "mobile-app", "xcodebuild")
    precondition(DevRules.classify("xcodebuild", ["xcodebuild", "-list"]) == nil, "xcodebuild -list is instant")
    precondition(DevRules.classify("node", ["node", "/n/lib/node_modules/npm/bin/npm-cli.js", "run", "build"])?.action == "run build", "npm run build")
    precondition(DevRules.classify("node", ["npm exec chrome-devtools-mcp@1.10.1"]) == nil, "npm exec is not a task")
    precondition(DevRules.classify("node", ["node", "/p/node_modules/next/dist/bin/next", "dev"]) == nil, "dev server is not a task")
    precondition(DevRules.classify("node", ["node", "/p/node_modules/next/dist/bin/next", "build"])?.tool == "next", "next build")
    precondition(DevRules.classify("node", ["node", "/p/node_modules/.bin/vite"]) == nil, "bare vite = dev server")
    precondition(DevRules.classify("java", ["java", "-Xmx2g", "org.gradle.wrapper.GradleWrapperMain", "assembleRelease"])?.action == "assembleRelease", "gradle")
    precondition(DevRules.classify("java", ["java", "org.gradle.launcher.daemon.bootstrap.GradleDaemon", "8.7"])?.busyOnly == true, "gradle daemon")
    precondition(DevRules.classify("dart", ["dart", "/f/flutter_tools.snapshot", "build", "ios", "--release"])?.action == "build ios", "flutter build")
    precondition(DevRules.classify("dart", ["dart", "/f/flutter_tools.snapshot", "run"]) == nil, "flutter run = app")
    precondition(DevRules.classify("python3.12", ["python3", "-m", "pip", "install", "torch"])?.tool == "pip", "pip install")
    precondition(DevRules.classify("ruby", ["ruby", "/opt/homebrew/bin/pod", "install"])?.tool == "CocoaPods", "pod install")
    precondition(DevRules.classify("docker", ["docker", "compose", "build"])?.action == "compose build", "docker compose build")
    precondition(DevRules.classify("curl", ["curl", "-s", "https://x/api"]) == nil && DevRules.classify("curl", ["curl", "-L", "-o", "/t/a.zip", "u"])?.subject == "a.zip", "curl")
    precondition(DevRules.classify("ffmpeg", ["ffmpeg", "-i", "/v/in.mov", "-c:v", "h264", "/v/out.mp4"])?.subject == "in.mov → out.mp4", "ffmpeg")
    precondition(DevRules.classify("uv", ["uv", "tool", "uvx", "mcp-server"]) == nil && DevRules.classify("uv", ["uv", "sync"])?.tool == "uv", "uvx servers")
    precondition(DevScanner.derivedDataProject(["swift-frontend", "-o", "/U/Library/Developer/Xcode/DerivedData/Mobile-App-abcdef/Build/x.o"]) == "Mobile-App", "derived data")
    let (p1, l1) = TaskHistory.estimate(elapsed: 30, expected: 120)
    precondition(p1 == 0.25 && l1 == "0:30 · ≈ 2 phút nữa", "eta")
    precondition(TaskHistory.estimate(elapsed: 30, expected: nil).0 == nil, "no history, no guess")
    // GitHub
    let gc = GitHub.parse(config: "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = git@github.com:hung/notch.git\n", head: "ref: refs/heads/feat/x\n")
    precondition(gc?.repo == "hung/notch" && gc?.branch == "feat/x", "git config")
    precondition(GitHub.repo(fromRemote: "https://gitlab.com/a/b.git") == nil, "not github")
    let steps = GitHub.steps(["jobs": [["name": "build", "status": "in_progress", "steps": [["name": "Checkout", "status": "completed"],
                                       ["name": "Test", "status": "in_progress"], ["name": "Upload", "status": "queued"], ["name": "Post", "status": "completed"]]]]])
    precondition(steps.step == "build › Test" && steps.progress == 0.5, "github steps")
    // watch items: match by name and/or command line, outermost process only
    let gem = presets.first { $0.name == "Gemini CLI" }!
    let fake: [pid_t: [String]] = [100: ["node", "/x/@google/gemini-cli/bundle/gemini.js"], 101: ["node", "/x/gemini-cli/worker.js"], 102: ["node", "/y/vite"]]
    let ps = [PInfo(pid: 100, ppid: 1, name: "node"), PInfo(pid: 101, ppid: 100, name: "node"), PInfo(pid: 102, ppid: 1, name: "node")]
    precondition(gem.roots(ps, argv: { fake[$0] ?? [] }).map(\.pid) == [100], "gemini root only")
    precondition(!gem.matches(PInfo(pid: 103, ppid: 1, name: "zsh"), argv: { _ in ["zsh", "-c", "echo gemini-cli"] }), "shell mentioning it")
    precondition(WatchItem(name: "x", kind: .process).roots(ps, argv: { fake[$0] ?? [] }).isEmpty, "empty rule matches nothing")
    let old = #"[{"id":"7B1A1F1E-1C1D-4E2B-9E1F-1A2B3C4D5E6F","name":"R","kind":"process","process":"gitlab-runner","childTasks":true,"enabled":true}]"#
    precondition((try? JSONDecoder().decode([WatchItem].self, from: Data(old.utf8)))?.first?.cpuThreshold == 0.5, "old saved lists still decode")
    precondition(buildPresets.allSatisfy(\.inBuild) && !gem.inBuild && buildItem.inBuild, "build vs AI grouping")
    let usage = "You are currently using your subscription\n\nCurrent session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)\nCurrent week (all models): 34% used · resets Nov 5 at 1:59pm (Asia/Saigon)\nCurrent week (Sonnet only): 3% used · resets Nov 5 at 2pm (Asia/Saigon)\n"
    let uw = ClaudeUsage.parse(usage, now: ISO8601DateFormatter().date(from: "2026-11-03T10:00:00Z")!)
    precondition(uw.count == 2 && uw[0].label == "5h" && uw[0].used == 34 && uw[1].label == "7d"
                 && ISO8601DateFormatter().string(from: uw[0].resets) == "2026-11-03T13:09:00Z"
                 && ISO8601DateFormatter().string(from: uw[1].resets) == "2026-11-05T06:59:00Z", "claude /usage parse")
    let hourOnly = ClaudeUsage.parse("Current week (all models): 34% used · resets Nov 5 at 2pm (Asia/Saigon)", now: ISO8601DateFormatter().date(from: "2026-11-03T10:58:00Z")!)
    precondition(ISO8601DateFormatter().string(from: hourOnly[0].resets) == "2026-11-05T07:00:00Z", "reset without minutes")
    let bs = [Badge(symbol: "star.fill"), Badge(image: "/tmp/x.png", shape: .rounded, border: true, right: true), Badge(text: "HN", color: "#FF0000", plain: true)]
    precondition((try? JSONDecoder().decode(Badge.self, from: Data(#"{"id":"7B1A1F1E-1C1D-4E2B-9E1F-1A2B3C4D5E6F","symbol":"star.fill","shape":"circle","border":false,"right":false}"#.utf8))) != nil, "badges saved before text/color decode")
    precondition((try? JSONDecoder().decode([Badge].self, from: JSONEncoder().encode(bs))) == bs, "badges round-trip")
    var sw = [Badge(symbol: "a"), Badge(symbol: "b"), Badge(symbol: "c", right: true)]
    sw.swapBadges(sw[0].id, sw[2].id)
    precondition(sw.map(\.symbol) == ["c", "b", "a"] && sw.map(\.right) == [false, false, true], "swap across sides")
    sw.moveBadge(sw[0].id, right: true)
    precondition(sw.map(\.symbol) == ["b", "a", "c"] && sw.filter(\.right).count == 2, "move to the other side")
    sw.moveBadge(sw[0].id, right: true)
    precondition(sw.filter(\.right).count == 2, "full side refuses")
    precondition(SpriteLibrary.all.count >= 30 && SpriteLibrary.categories.count == 5, "built-in sprites bundled")
    precondition(SpriteLibrary.all.allSatisfy { e in
        e.category == "pets" ? (0..<8).allSatisfy { Animations.pet(e.id, pose: $0)?.images.count == 4 } && e.frames == 32
                             : Animations.builtIn(e.id)?.images.count == e.frames
    }, "every sprite strip slices into its frames; a pet into 8 poses of 4")
    let gifURL = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-selftest.gif")
    if let dest = CGImageDestinationCreateWithURL(gifURL as CFURL, "com.compuserve.gif" as CFString, 3, nil) {
        let one = Animations.builtIn("px-heart")!.images[0]
        for d in [0.2, 0.0, 0.5] { CGImageDestinationAddImage(dest, one, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: d]] as CFDictionary) }
        CGImageDestinationFinalize(dest)
    }
    let gif = Frames.gif(gifURL)
    precondition(gif?.images.count == 3 && gif?.delays == [0.2, 0.1, 0.5] && gif?.image(at: 0.25).width == 16, "gif frames and delays")
    precondition(Frames.strip(Bundle.main.resourceURL!.appendingPathComponent("Sprites/px-coin.png"), count: nil, fps: 10, pixel: true)?.images.count == 6, "strip frame count from shape")
    precondition(SymbolLibrary.all.count > 1000 && SymbolLibrary.search("star", in: "all").contains("star.fill")
                 && SymbolLibrary.search("", in: "weather").contains("cloud.fill"), "symbol catalog")
    precondition(SystemLoad.cpu(from: [100, 50, 850, 0], to: [130, 70, 900, 0]) == 0.5, "cpu busy share")
    precondition(SystemLoad.cpu(from: [UInt32.max - 9, 0, 0, 0], to: [10, 0, 20, 0]) == 0.5, "cpu counter wraparound")
    precondition((SystemLoad.memory() ?? -1) > 0 && SystemLoad.cpuTicks() != nil, "load readable")
    let calEv = CalEvent(id: "e", title: "x", start: Date(timeIntervalSince1970: 1000), end: Date(timeIntervalSince1970: 2000), color: .blue, link: nil)
    precondition(calEv.running(at: Date(timeIntervalSince1970: 1500)) && calEv.elapsed(at: Date(timeIntervalSince1970: 1250)) == 0.25
                 && !calEv.running(at: Date(timeIntervalSince1970: 2000)), "event running and progress")
    var allDayEv = calEv; allDayEv.allDay = true
    precondition(!allDayEv.running(at: Date(timeIntervalSince1970: 1500)), "all-day events never count as running")
    let day0 = Calendar.current.startOfDay(for: Date())
    let oneDay = CalEvent(id: "a", title: "x", start: day0, end: day0.addingTimeInterval(86400), color: .blue, link: nil, allDay: true)
    let threeDays = CalEvent(id: "b", title: "x", start: day0, end: day0.addingTimeInterval(3 * 86400), color: .blue, link: nil, allDay: true)
    precondition(oneDay.untilText == nil && threeDays.untilText?.hasPrefix("đến ") == true, "multi-day all-day label")
    precondition(DeviceArt.key(name: "x", vendor: 0x004C, product: 8194, type: "Headphones") == "banner:Banner-PID-8194", "exact AirPods picture")
    precondition(DeviceArt.key(name: "Hùng Airpod", vendor: 0x004C, product: 0x201B, type: "Headphones") == "banner:Banner-PID-8211", "unknown AirPods: closest look")
    precondition(DeviceArt.key(name: "Magic Mouse", vendor: 0x004C, product: nil, type: "Mouse")?.hasSuffix("Mouse.icns") == true
                 && DeviceArt.key(name: "X3-5.2 Mouse", vendor: nil, product: nil, type: "Mouse") == nil, "Apple accessory vs other brand")
    precondition(DeviceArt.image("banner:Banner-PID-8194") != nil, "system banner loads")
    let twice = #"{"SPBluetoothDataType":[{"device_connected":[{"Pods":{"device_address":"AA:BB","device_minorType":"Headphones"}},{"Pods":{"device_address":"AA:BB","device_minorType":"Headphones"}}]}]}"#
    precondition(Devices.parseBluetooth(Data(twice.utf8)).count == 1, "one device listed twice shows once")
    // compiling needs the browser's own dictionary, so only installed ones can be checked
    for bid in NowPlaying.browsers where NSWorkspace.shared.urlForApplication(withBundleIdentifier: bid) != nil {
        for cmd in [MediaCommand.toggle, .next, .previous, .seek(12)] {
            var err: NSDictionary?
            let ok = NSAppleScript(source: NowPlaying.tabControlScript(bid, cmd))?.compileAndReturnError(&err) == true
                && NSAppleScript(source: NowPlaying.tabStateScript(bid))?.compileAndReturnError(&err) == true
            if !ok { FileHandle.standardError.write(Data("tab control script for \(bid) \(cmd) fails: \(err ?? [:])\n".utf8)) }
            precondition(ok, "tab control script compiles")
        }
    }
    precondition(BrowserJS.installed.contains { $0.bid == "com.apple.Safari" }, "Safari listed")
    _ = BrowserJS.installed.map(BrowserJS.allowed)  // reads without throwing even when a profile is missing
    let tab = NowPlaying.parseTab("paused|61.5|212.3\nSong - YouTube", source: "com.google.Chrome")
    precondition(tab?.playing == false && tab?.position == 61.5 && tab?.duration == 212.3 && tab?.title == "Song - YouTube", "tab state parse")
    precondition(NowPlaying.parseTab("playing|10|0\nLive", source: "x")?.duration == nil && NowPlaying.parseTab("", source: "x") == nil, "live stream / no media")
    precondition(Sensors.callApp(["com.google.Chrome", "us.zoom.xos"]) == "us.zoom.xos", "call app beats a browser")
    precondition(Sensors.appID("com.google.Chrome.helper.renderer") == "com.google.Chrome", "browser helper counts as the browser")
    precondition(Sensors.appID("com.apple.CoreSpeech") == nil, "Hey Siri isn't an app using the mic")
    precondition(Sensors.callApp(["com.apple.VoiceMemos"]) == nil, "recording isn't a call")
    _ = Sensors.read()  // CoreAudio / CoreMediaIO answer without a permission prompt
    precondition(Downloads.finished(in: ["a.pdf", "b.zip.crdownload", ".DS_Store", "c.part", "Song.mp3"]) == ["a.pdf", "Song.mp3"], "only finished downloads")
    precondition(LevelHUD(kind: .volume, value: 0.5, muted: true).symbol == "speaker.slash.fill" && LevelHUD(kind: .brightness, value: 0.9).symbol == "sun.max.fill", "level symbols")
    _ = (NetInfo.vpnUp(), NetInfo.freeDisk(), Levels.brightness(), Levels.output().flatMap(Levels.volume))  // read without crashing
    // sound pack: export → import keeps built-ins by name and carries custom files; junk is dropped
    let src = FileManager.default.temporaryDirectory.appendingPathComponent("selftest-\(getpid()).aiff")
    try? Data("FORM-test".utf8).write(to: src)
    let packJSON = try! JSONEncoder().encode(SoundPackFile(map: ["taskDone": "Glass", "ciFailed": src.path, "bogus": "Glass"], volume: 0.5))
    var pack = try! JSONDecoder().decode(SoundPackFile.self, from: packJSON)
    pack.sounds["meeting"] = "file:../../evil.sh"
    pack.files["../../evil.sh"] = Data("rm -rf".utf8).base64EncodedString()
    let got = pack.resolved(system: ["Glass"])
    precondition(got["taskDone"] == "Glass" && got["bogus"] == nil && got["meeting"] == nil, "pack: names kept, unknown events and non-audio files dropped")
    precondition(got["ciFailed"].map { $0.hasPrefix(SoundPackFile.folder.path) && FileManager.default.contents(atPath: $0) == Data("FORM-test".utf8) } == true,
                 "pack: custom file restored into the app's sound folder")
    if let p = got["ciFailed"] { try? FileManager.default.removeItem(atPath: p) }
    try? FileManager.default.removeItem(at: src)
    let grouped = ModuleID.groups.flatMap(\.modules)
    precondition(Set(grouped) == Set(ModuleID.allCases) && grouped.count == ModuleID.allCases.count, "every module in exactly one Overview group")
    var authFails = 0
    let wrong = "2026-11-04 15:34:36.006 Df loginwindow[411:fb7] [com.apple.loginwindow.logging:Standard] -[LWDefaultScreenLockUI authFailWithMessage:] | enter. INCORRECT password"
    precondition(AuthWatch.classify(wrong, failedSoFar: &authFails) == .failed(touchID: false), "wrong password, as macOS 26 logs it")
    precondition(AuthWatch.classify("-[LWDefaultScreenLockUI authFailWithMessage:] |      setEnabled:YES", failedSoFar: &authFails) == nil, "follow-up lines of one try")
    authFails = 0
    precondition(AuthWatch.classify("loginwindow ... | ======== SCREENLOCK ==== APEventTouchIDMatch =========", failedSoFar: &authFails) == .success, "touch id match")
    precondition(AuthWatch.classify("loginwindow ... SCREENLOCK ==== APEventTouchIDNoMatch ====", failedSoFar: &authFails) == .failed(touchID: true), "touch id miss")
    precondition(AuthWatch.classify("-[LWScreenLock startUnlock:] |      _numFailedAuthAttempts = 0", failedSoFar: &authFails) == nil, "no failures yet")
    precondition(AuthWatch.classify("-[LWScreenLock startUnlock:] |      _numFailedAuthAttempts = 2", failedSoFar: &authFails) == .failed(touchID: false), "counter went up")
    precondition(AuthWatch.classify("-[LWScreenLock startUnlock:] |      _numFailedAuthAttempts = 2", failedSoFar: &authFails) == nil, "same count again")
    let me = Proc.all().first { $0.pid == getpid() }
    precondition(me?.name == "NotchTasks" && me?.ppid == getppid(), "own process name/ppid")
    // agent asks: what each agent's hook sends and what we answer
    let bash: [String: Any] = ["tool_name": "Bash", "tool_input": ["command": "rm -rf build", "description": "Clean"]]
    precondition(AgentAsks.ask(from: bash, kind: .claude, id: "a", pid: 1, session: "s").summary == "rm -rf build", "ask summary is the command")
    let q: [String: Any] = ["tool_name": "AskUserQuestion", "tool_input": ["questions": [["question": L("Màu nào?"), "header": L("Màu"),
        "multiSelect": false, "options": [["label": L("Đỏ"), "description": "x"], ["label": "Xanh", "description": "y"]]]]]]
    let qa = AgentAsks.ask(from: q, kind: .claude, id: "b", pid: 1, session: "s")
    precondition(qa.isQuestion && qa.questions[0].options == ["Đỏ", "Xanh"] && qa.summary == "Màu nào?", "question parsed")
    let codexIn: [String: Any] = ["tool_name": "shell", "tool_input": ["command": ["bash", "-lc", "touch x"]]]
    precondition(AgentAsks.ask(from: codexIn, kind: .codex, id: "c", pid: 1, session: "s").summary == "bash -lc touch x", "codex argv joined")
    let agyIn: [String: Any] = ["toolCall": ["name": "run_command", "args": ["CommandLine": "npm test"]], "workspacePaths": ["/p/app"]]
    let agyAsk = AgentAsks.ask(from: agyIn, kind: .antigravity, id: "d", pid: 1, session: "s")
    precondition(agyAsk.tool == "run_command" && agyAsk.summary == "npm test", "antigravity camelCase input")
    func json(_ s: String?) -> [String: Any]? { s.flatMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) } as? [String: Any] }
    func decision(_ r: AskReply, _ input: [String: Any], _ k: AgentKind = .claude) -> [String: Any]? {
        (json(AgentAsks.output(r, input: input, kind: k))?["hookSpecificOutput"] as? [String: Any])?["decision"] as? [String: Any]
    }
    precondition(AgentAsks.output(.pass, input: bash, kind: .claude) == nil && AgentAsks.output(.pass, input: bash, kind: .gemini) == nil, "pass prints nothing")
    precondition(decision(.allow, bash)?["behavior"] as? String == "allow" && decision(.deny, bash, .codex)?["behavior"] as? String == "deny", "allow / deny")
    let up = decision(.answers([L("Màu nào?"): "Xanh"]), q)?["updatedInput"] as? [String: Any]
    precondition((up?["answers"] as? [String: String])?["Màu nào?"] == "Xanh" && up?["questions"] != nil, "answers keep the questions")
    precondition(json(AgentAsks.output(.deny, input: [:], kind: .gemini))?["decision"] as? String == "deny", "gemini deny")
    precondition(json(AgentAsks.output(.pass, input: [:], kind: .antigravity))?["decision"] as? String == "ask", "antigravity pass = ask")
    AntigravityScanner.selfTest()
    AntigravityLimits.selfTest()
    // An agent set to approve on its own keeps that job; its questions, and every other mode, still come to the notch
    let approvalDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try? FileManager.default.createDirectory(at: approvalDir, withIntermediateDirectories: true)
    let rollout = approvalDir.appendingPathComponent("rollout.jsonl")
    try? (#"{"type":"turn_context","payload":{"approvals_reviewer":"user"}}"# + "\n" + #"{"type":"turn_context","payload":{"approvals_reviewer":"auto_review"}}"# + "\n"
          + #"{"type":"response_item","payload":{"output":"{\"type\":\"turn_context\"}"}}"# + "\n").write(to: rollout, atomically: true, encoding: .utf8)
    let agyConfig = approvalDir.appendingPathComponent("config.json")
    try? #"{"userSettings":{"permissionPreset":"AGENT_PERMISSION_PRESET_TURBO"}}"#.write(to: agyConfig, atomically: true, encoding: .utf8)
    let codexMode = AgentAsks.approvalMode(.codex, input: ["permission_mode": "default", "transcript_path": rollout.path], antigravityConfig: agyConfig)
    precondition(codexMode == "auto_review" && AgentAsks.selfApproved(mode: codexMode, tool: "exec_command"), "codex auto review keeps its commands")
    precondition(!AgentAsks.selfApproved(mode: codexMode, tool: "request_user_input"), "codex questions still ask")
    precondition(!AgentAsks.selfApproved(mode: "user", tool: "exec_command") && !AgentAsks.selfApproved(mode: nil, tool: "exec_command"), "user review still asks")
    let agyMode = AgentAsks.approvalMode(.antigravity, input: [:], antigravityConfig: agyConfig)
    precondition(AgentAsks.selfApproved(mode: agyMode, tool: "run_command") && !AgentAsks.selfApproved(mode: agyMode, tool: "ask_question"), "antigravity turbo")
    precondition(AgentAsks.approvalMode(.claude, input: ["permission_mode": "bypassPermissions"], antigravityConfig: agyConfig) == nil, "claude untouched")
    try? FileManager.default.removeItem(at: approvalDir)
    // Antigravity's ask_question (plain string options), answered on the notch: the dialog is blocked, the answer handed over
    let agyQ: [String: Any] = ["toolCall": ["name": "ask_question", "args": ["questions": [["question": L("Màu?"), "options": [L("Đỏ"), "Xanh"], "is_multi_select": false]]]]]
    let agyQA = AgentAsks.ask(from: agyQ, kind: .antigravity, id: "e", pid: 1, session: "s")
    precondition(agyQA.isQuestion && agyQA.questions[0].options == ["Đỏ", "Xanh"], "antigravity question parsed")
    let handed = json(AgentAsks.output(.answers([L("Màu?"): "Xanh"]), input: agyQ, kind: .antigravity))
    precondition(handed?["decision"] as? String == "deny" && (handed?["reason"] as? String)?.contains("Xanh") == true, "answer handed over")
    precondition(json(AgentAsks.output(.answers(["Màu?": "Xanh"]), input: [:], kind: .gemini))?["decision"] as? String == "deny", "gemini answer handed over")
    // Cursor: flat command input, permission answers; Droid: Claude-style PreToolUse
    let cur = AgentAsks.ask(from: ["command": "npm i", "hook_event_name": "beforeShellExecution"], kind: .cursor, id: "f", pid: 1, session: "s")
    precondition(cur.summary == "npm i" && cur.action == "Chạy lệnh", "cursor input")
    precondition(json(AgentAsks.output(.pass, input: [:], kind: .cursor))?["permission"] as? String == "ask", "cursor pass = ask")
    precondition(LimitWindow.step(84) == 80 && LimitWindow.step(85) == 85 && LimitWindow.step(97) == 95 && LimitWindow.step(99.4) == 99 && LimitWindow.step(100) == 100, "limit steps: 5s, then 99 and 100")
    precondition(LocalModels.parseLMStudio(Data(#"[{"displayName":"Gemma 4 26B","identifier":"g"}]"#.utf8)) == ["Gemma 4 26B"], "lms ps")
    precondition(LocalModels.parseOllama(Data(#"{"models":[{"name":"qwen3:1.7b"}]}"#.utf8)) == ["qwen3:1.7b"], "ollama ps")
    // OpenCode: its Permission object in, {status} out; pass keeps its own prompt
    let ocIn: [String: Any] = ["action": "bash", "resources": ["git push"], "metadata": ["command": "git push origin"]]
    let oc = AgentAsks.ask(from: ocIn, kind: .opencode, id: "g", pid: 1, session: "s")
    precondition(oc.tool == "bash" && oc.summary == "git push origin" && oc.action == "Chạy lệnh", "opencode input")
    precondition(json(AgentAsks.output(.allow, input: ocIn, kind: .opencode))?["status"] as? String == "allow"
                 && json(AgentAsks.output(.pass, input: ocIn, kind: .opencode))?["status"] as? String == "ask", "opencode output")
    // a question the Codex app leaves in a thread stays open until you write back
    let cq = #"{"timestamp":"2026-11-05T03:07:53.935Z","type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","arguments":"{\"questions\":[{\"title\":\"Phần nào?\",\"options\":[\"JSON\",\"Cách duyệt\"]}]}","call_id":"call_1"}}"#
    let open = CodexQuestion.parse(cq, thread: "t1", open: nil)
    precondition(open?.questions.first?.options == ["JSON", "Cách duyệt"] && open?.questions.first?.question == "Phần nào?" && open?.callID == "call_1", "codex question parsed")
    let userMsg = #"{"timestamp":"2026-11-05T03:09:00.000Z","type":"response_item","payload":{"type":"message","id":"m","role":"user","content":[]}}"#
    precondition(CodexQuestion.parse(userMsg, thread: "t1", open: open) == nil, "codex question answered by your message")
    // installing touches only our hook and keeps the rest of the file
    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("nt-settings-\(getpid()).json")
    try? #"{"model":"opus","hooks":{"PermissionRequest":[{"matcher":"Bash","hooks":[{"type":"command","command":"other"}]}]}}"#.write(to: tmp, atomically: true, encoding: .utf8)
    try? AgentAsks.setInstalled(true, .claude, in: tmp)
    try? AgentAsks.setInstalled(true, .claude, in: tmp)  // twice: still one entry
    let after = (try? Data(contentsOf: tmp)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
    let groups = (after?["hooks"] as? [String: Any])?["PermissionRequest"] as? [[String: Any]] ?? []
    precondition(AgentAsks.installed(.claude, in: tmp) && groups.count == 2 && after?["model"] as? String == "opus", "install merges")
    try? AgentAsks.setInstalled(false, .claude, in: tmp)
    let back = (try? Data(contentsOf: tmp)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
    precondition(!AgentAsks.installed(.claude, in: tmp) && ((back?["hooks"] as? [String: Any])?["PermissionRequest"] as? [[String: Any]])?.count == 1, "uninstall keeps others")
    // Cursor: a flat list under "hooks" plus a version
    try? #"{"version":1,"hooks":{"beforeShellExecution":[{"command":"mine.sh"}]}}"#.write(to: tmp, atomically: true, encoding: .utf8)
    try? AgentAsks.setInstalled(true, .cursor, in: tmp)
    let curJ = (try? Data(contentsOf: tmp)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
    precondition(AgentAsks.installed(.cursor, in: tmp) && ((curJ?["hooks"] as? [String: Any])?["beforeShellExecution"] as? [[String: Any]])?.count == 2, "cursor install")
    try? AgentAsks.setInstalled(false, .cursor, in: tmp)
    precondition(!AgentAsks.installed(.cursor, in: tmp), "cursor uninstall")
    // OpenCode: our own plugin file, removed only if it is ours
    let ocFile = tmp.deletingLastPathComponent().appendingPathComponent("nt-oc-\(getpid()).js")
    try? AgentAsks.setInstalled(true, .opencode, in: ocFile)
    precondition(AgentAsks.installed(.opencode, in: ocFile) && ((try? String(contentsOf: ocFile, encoding: .utf8)) ?? "").contains("permission.hook(\"evaluate\""), "opencode plugin written")
    try? AgentAsks.setInstalled(false, .opencode, in: ocFile)
    precondition(!FileManager.default.fileExists(atPath: ocFile.path), "opencode plugin removed")
    // Antigravity: one named hook at the top level, beside the user's own
    try? #"{"lint":{"PostToolUse":[]}}"#.write(to: tmp, atomically: true, encoding: .utf8)
    try? AgentAsks.setInstalled(true, .antigravity, in: tmp)
    let agy = (try? Data(contentsOf: tmp)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
    precondition(AgentAsks.installed(.antigravity, in: tmp) && agy?["lint"] != nil && (agy?["notchtasks"] as? [String: Any])?["PreToolUse"] != nil, "antigravity install")
    try? AgentAsks.setInstalled(false, .antigravity, in: tmp)
    precondition(!AgentAsks.installed(.antigravity, in: tmp), "antigravity uninstall")
    try? FileManager.default.removeItem(at: tmp)
    try? FileManager.default.removeItem(at: tmp.deletingLastPathComponent().appendingPathComponent(tmp.lastPathComponent + ".notchtasks-backup"))
    let pairingSample = #"{"v":1,"g":"group123456789012","k":"key12345678901234567890123456789012","n":"Mac"}"#
    precondition(SyncHub.pairingText(from: SyncHub.pairURL(pairingSample)) == pairingSample, "pairing url round-trips")
    MainActor.assumeIsolated { SyncHub.selfTest() }
    print("selftest ok")
}

// Claude Code's PermissionRequest hook: hand the ask to the running app and wait for the answer (no window, no AppKit app)
if CommandLine.arguments.contains("--hook") { AgentAsks.runHook() }


// the bundle id moved from local.notchtasks to com.notchtasks.mac: bring the old settings over, once
if let id = Bundle.main.bundleIdentifier, id != "local.notchtasks", UserDefaults.standard.persistentDomain(forName: id)?.isEmpty ?? true,
   let old = UserDefaults.standard.persistentDomain(forName: "local.notchtasks"), !old.isEmpty {
    UserDefaults.standard.setPersistentDomain(old, forName: id)
}

if CommandLine.arguments.contains("--selftest") {
    L10n.testing = true  // the checks read the texts as written
    L10n.selfTest()
    Thermals.selfTest()
    SMC.selfTest()
    Fans.selfTest()
    FanNotch.selfTest()
    FanBubblePhase.selfTest()
    PermItem.selfTest()
    Permissions.selfTest()
    OnboardingGuide.selfTest()
    LimitCard.selfTest()
    FanConsentStep.selfTest()
    MainActor.assumeIsolated { FanControl.selfTest() }
    Updater.selfTest()
    Requirement.selfTest()
    MainActor.assumeIsolated { selfTest() }
    exit(0)
}

func runUpdateCommand(install: Bool) {
    Task { @MainActor in
        let updater = Updater()
        do {
            guard let update = try await updater.checkForUpdate() else {
                print("none")
                exit(install ? 1 : 0)
            }
            print("offered \(update.version) (\(update.build))")
            guard install else { exit(0) }
            try await updater.install(manifest: update) { print($0) }
            exit(0)
        } catch {
            print(error.localizedDescription)
            exit(1)
        }
    }
    RunLoop.main.run()
}

if CommandLine.arguments.contains("--update-check") { runUpdateCommand(install: false) }
if CommandLine.arguments.contains("--update-install") { runUpdateCommand(install: true) }

// prints what the notch would show right now, for checking against `ps`
if CommandLine.arguments.contains("--dump") {
    MainActor.assumeIsolated {
        let m = Model()
        RunLoop.main.run(until: Date().addingTimeInterval(10))  // lets file-progress callbacks and the usage check arrive
        m.tick()
        for s in m.states {
            print("\(s.item.name): \(s.jobs.count) tác vụ, \(s.working) đang chạy")
            for j in s.jobs { print("  \(j.working ? "●" : "○") \(j.title) — \(j.detail)") }
        }
        m.scanRunnerLog()
        for a in m.alerts { print("ALERT job \(a.id) \(a.name) — \(a.project) · \(a.reason) → \(a.url?.absoluteString ?? "-")") }
        print("Modules (order, ✕ = off): " + AppSettings.shared.modules.map { ($0.title) + (AppSettings.shared.on($0) ? "" : "✕") }.joined(separator: ", "))
        print("Devices: " + (Devices.bluetooth() + Devices.usb()).map { $0.name + ($0.battery.map { " (\($0))" } ?? "") }.joined(separator: ", "))
        print("Battery: \(BatteryState.read().map { "\($0.percent)% plugged=\($0.plugged)" } ?? "none") · Calendar authorized: \(CalendarSource.authorized)")
        let thermals = Thermals.read()
        print("Temperature: \(Thermals.hottest(thermals).map { "\($0.celsius.formatted(.number.precision(.fractionLength(1))))°C \($0.name)" } ?? "none")")
        for reading in thermals { print("  \(reading.name): \(reading.celsius.formatted(.number.precision(.fractionLength(1))))°C") }
        for fan in Fans.read() {
            print("Fan \(fan.index): actual=\(fan.actual) min=\(fan.minimum) max=\(fan.maximum) target=\(fan.target) forced=\(fan.forced)")
        }
        for p in m.limits {
            print("Limit \(p.name): " + p.windows.map { "\($0.label) \(Int($0.used))% (reset \($0.resets.formatted()))" }.joined(separator: ", "))
        }
    }
    exit(0)
}

MainActor.assumeIsolated {
    let delegate = AppDelegate()
    NSApplication.shared.delegate = delegate
    NSApplication.shared.setActivationPolicy(.accessory)
    NSApplication.shared.run()
}
