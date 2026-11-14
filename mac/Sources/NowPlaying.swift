import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - Now Playing (public APIs only: CoreAudio says which app plays, AppleScript gives track info)

enum MediaCommand: Equatable { case toggle, next, previous, seek(Double) }

struct NowPlaying: Equatable {
    var source: String       // bundle id: com.spotify.client, com.google.Chrome…
    var title: String?
    var artist: String?
    var playing: Bool
    var position: Double?
    var duration: Double?
    var artwork: URL?

    var appName: String { NSRunningApplication.runningApplications(withBundleIdentifier: source).first?.localizedName ?? source }
    var item: WatchItem { WatchItem(name: appName, kind: .app, bundleID: source) }

    /// Players with an AppleScript dictionary: track info + controls. Value = duration expression in seconds.
    static let players = ["com.spotify.client": "(duration of t) / 1000", "com.apple.Music": "duration of t"]
    static let browsers = ["com.google.Chrome", "com.microsoft.edgemac", "com.brave.Browser", "company.thebrowser.Browser", "com.apple.Safari"]
    static let mediaSites = ["youtube.com/watch", "music.youtube.com", "soundcloud.com", "open.spotify.com", "zingmp3.vn", "nhaccuatui.com", "netflix.com/watch", "tiktok.com"]

    // "is running" first: a tell to a quit app would launch it
    static func playerScript(_ bid: String) -> String {
        """
        if application id "\(bid)" is not running then return "stopped"
        tell application id "\(bid)"
          set s to player state as string
          if s is "stopped" then return "stopped"
          set t to current track
          set art to ""
          \(bid == "com.spotify.client" ? "set art to artwork url of t" : "")
          return s & linefeed & (name of t) & linefeed & (artist of t) & linefeed & (\(players[bid] ?? "0")) & linefeed & player position & linefeed & art
        end tell
        """
    }

    /// Title of the first tab on a media site (browsers don't say which tab is audible).
    static func tabScript(_ bid: String) -> String {
        let cond = mediaSites.map { "u contains \"\($0)\"" }.joined(separator: " or ")
        return """
        if application id "\(bid)" is not running then return ""
        tell application id "\(bid)"
          repeat with w in windows
            repeat with t in tabs of w
              set u to URL of t
              if \(cond) then return \(bid == "com.apple.Safari" ? "name" : "title") of t
            end repeat
          end repeat
        end tell
        return ""
        """
    }

    /// Runs a command in the first media tab's page (the same tab tabScript names). Needs the browser's
    /// "Allow JavaScript from Apple Events" (off by default): without it osascript fails and nothing is sent.
    static func tabControlScript(_ bid: String, _ cmd: MediaCommand) -> String {
        let js: String
        switch cmd {
        case .seek(let s): js = "(function(){var a=Array.from(document.querySelectorAll('video,audio'));var m=a.find(function(e){return !e.paused})||a[0];if(!m)return 'none';m.currentTime=\(String(format: "%.1f", s));return 'ok'})()"
        case .toggle: js = "(function(){var a=Array.from(document.querySelectorAll('video,audio'));var m=a.find(function(e){return !e.paused})||a[0];if(!m)return 'none';if(m.paused){m.play();return 'play'}m.pause();return 'pause'})()"
        case .next: js = "(function(){var b=document.querySelector('.ytp-next-button,[aria-label=Next]');if(b){b.click();return 'ok'}return 'none'})()"
        case .previous: js = "(function(){var m=document.querySelector('video,audio');if(!m)return 'none';m.currentTime=0;return 'ok'})()"
        }
        return inMediaTab(bid, js)
    }

    /// The media tab's real state, "playing|position|duration" then its title on the next line: audio output alone
    /// can't tell (a paused tab keeps its audio stream open for a while).
    static func tabStateScript(_ bid: String) -> String {
        inMediaTab(bid, "(function(){var a=Array.from(document.querySelectorAll('video,audio'));var m=a.find(function(e){return !e.paused})||a[0];if(!m)return '';return (m.paused?'paused':'playing')+'|'+m.currentTime+'|'+(isFinite(m.duration)?m.duration:0)})()",
                   withTitle: true)
    }

    static func parseTab(_ out: String, source: String) -> NowPlaying? {
        let lines = out.components(separatedBy: "\n")
        let f = lines[0].components(separatedBy: "|")
        guard f.count == 3, ["playing", "paused"].contains(f[0]) else { return nil }
        let dur = Double(f[2]) ?? 0
        return NowPlaying(source: source, title: lines.count > 1 && !lines[1].isEmpty ? lines[1] : nil, playing: f[0] == "playing",
                          position: dur > 0 ? Double(f[1]) : nil, duration: dur > 0 ? dur : nil)
    }

    private static func inMediaTab(_ bid: String, _ js: String, withTitle: Bool = false) -> String {
        let cond = mediaSites.map { "u contains \"\($0)\"" }.joined(separator: " or ")
        let run = bid == "com.apple.Safari" ? "do JavaScript \"\(js)\" in t" : "execute t javascript \"\(js)\""
        let result = withTitle ? "(\(run)) & linefeed & (\(bid == "com.apple.Safari" ? "name" : "title") of t)" : run
        return """
        tell application id "\(bid)"
          repeat with w in windows
            repeat with t in tabs of w
              set u to URL of t
              if \(cond) then return \(result)
            end repeat
          end repeat
        end tell
        return "none"
        """
    }

    /// playerScript output: state, title, artist, duration s, position s, artwork url (decimals may use ",").
    static func parse(_ out: String, source: String) -> NowPlaying? {
        let f = out.trimmingCharacters(in: .whitespacesAndNewlines).components(separatedBy: "\n")
        guard f.count >= 5, f[0] != "stopped" else { return nil }
        let num = { (s: String) in Double(s.replacingOccurrences(of: ",", with: ".")) }
        return NowPlaying(source: source, title: f[1], artist: f[2].isEmpty ? nil : f[2], playing: f[0] == "playing",
                          position: num(f[4]), duration: num(f[3]), artwork: f.count > 5 ? URL(string: f[5]) : nil)
    }
}

func osascript(_ script: String) -> String? { osascriptResult(script).out }

/// Output, or nil and the error text (e.g. "Executing JavaScript through AppleScript is turned off").
func osascriptResult(_ script: String) -> (out: String?, error: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
    p.arguments = ["-e", script]
    let out = Pipe(), err = Pipe()
    p.standardOutput = out
    p.standardError = err
    guard (try? p.run()) != nil else { return (nil, "") }
    let data = out.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    guard p.terminationStatus == 0 else {
        // e.g. -1743: the user hasn't allowed NotchTasks to control that app (System Settings › Privacy › Automation)
        let text = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        NSLog("NotchTasks osascript: %@", text)
        return (nil, text)
    }
    return (String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines), "")
}

/// Browsers & other apps follow the system media keys (posting them needs Accessibility once).
func postMediaKey(_ cmd: MediaCommand) {
    if case .seek = cmd { return }  // no key for that
    let key: Int32 = cmd == .toggle ? 16 : cmd == .next ? 17 : 18  // NX_KEYTYPE_PLAY / NEXT / PREVIOUS
    for down in [true, false] {
        let state: Int32 = down ? 0xa00 : 0xb00
        NSEvent.otherEvent(with: .systemDefined, location: .zero, modifierFlags: NSEvent.ModifierFlags(rawValue: UInt(state)),
                           timestamp: 0, windowNumber: 0, context: nil, subtype: 8, data1: Int((key << 16) | state), data2: -1)?
            .cgEvent?.post(tap: .cghidEventTap)
    }
}

enum AudioOut {
    /// Bundle ids of processes currently sending audio to an output device (helpers included), or recording from a microphone.
    static func bundles(input: Bool = false) -> [String] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var addr = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyProcessObjectList,
                                              mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard prop(id, input ? kAudioProcessPropertyIsRunningInput : kAudioProcessPropertyIsRunningOutput, UInt32(0)) == 1 else { return nil }
            return prop(id, kAudioProcessPropertyBundleID, "" as CFString).map { $0 as String }
        }
    }

    static func prop<T>(_ obj: AudioObjectID, _ sel: AudioObjectPropertySelector, _ initial: T) -> T? {
        var addr = AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        return AudioObjectGetPropertyData(obj, &addr, 0, nil, &size, &value) == noErr ? value : nil
    }
}

// MARK: - "Allow JavaScript from Apple Events": what lets the notch pause, play and skip inside a browser tab

enum BrowserJS {
    struct Browser { let bid, name: String; let support: String? }  // support: Application Support folder (Chromium)
    static let all = [
        Browser(bid: "com.google.Chrome", name: "Chrome", support: "Google/Chrome"),
        Browser(bid: "com.microsoft.edgemac", name: "Edge", support: "Microsoft Edge"),
        Browser(bid: "com.brave.Browser", name: "Brave", support: "BraveSoftware/Brave-Browser"),
        Browser(bid: "company.thebrowser.Browser", name: "Arc", support: "Arc/User Data"),
        Browser(bid: "com.apple.Safari", name: "Safari", support: nil),
    ]
    static var installed: [Browser] { all.filter { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0.bid) != nil } }
    static func browser(_ bid: String) -> Browser? { all.first { $0.bid == bid } }

    /// Read from the browser's own settings file, so it works even while the browser is closed.
    static func allowed(_ b: Browser) -> Bool {
        let home = FileManager.default.homeDirectoryForCurrentUser
        guard let support = b.support else {
            let plist = home.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Preferences/com.apple.Safari.plist")
            return (NSDictionary(contentsOf: plist)?["AllowJavaScriptFromAppleEvents"] as? Bool) ?? false
        }
        let root = home.appendingPathComponent("Library/Application Support/" + support)
        // the setting is per profile and any open profile may hold the playing tab: on in one of them counts.
        // Only the Permissions list uses this; the notch asks the playing tab itself (Model.browserBlocked).
        let state = (try? Data(contentsOf: root.appendingPathComponent("Local State"))).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
        let info = state?["profile"] as? [String: Any]
        let infoCache = info?["info_cache"] as? [String: Any]
        let profiles = Set((infoCache?.keys.map { $0 } ?? []) + ((info?["last_active_profiles"] as? [String]) ?? []) + [(info?["last_used"] as? String) ?? "Default"])
        return profiles.contains { profile in
            let prefs = (try? Data(contentsOf: root.appendingPathComponent(profile + "/Preferences"))).flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            return ((prefs?["browser"] as? [String: Any])?["allow_javascript_apple_events"] as? Bool) ?? false
        }
    }

    /// Where the switch is, in the browser's own words.
    static func steps(_ b: Browser) -> String {
        b.bid == "com.apple.Safari"
            ? L("1. Safari › Cài đặt › Nâng cao: bật \"Hiện tính năng cho nhà phát triển web\".\n2. Menu Phát triển › Cho phép JavaScript từ Apple Events.")
            : L("Trong {0}, trên thanh menu: Xem (View) › Nhà phát triển (Developer) › Cho phép JavaScript từ Apple Events.", b.name)
    }

    /// The media tab runs a script: the switch is on for the profile that holds it (false also when no media tab is open).
    static func works(_ b: Browser) -> Bool {
        guard let out = osascript(NowPlaying.tabStateScript(b.bid)) else { return false }
        return out != "none"
    }

    /// Brings the playing tab's window to the front, so the switch the user ticks belongs to that tab's profile.
    /// Talks to the browser only; the settings themselves are left to the user.
    static func showPlayingTab(_ b: Browser) {
        let cond = NowPlaying.mediaSites.map { "u contains \"\($0)\"" }.joined(separator: " or ")
        let select = b.bid == "com.apple.Safari" ? "set current tab of w to t" : "set active tab index of w to i"
        _ = osascript("""
        tell application id "\(b.bid)"
          activate
          repeat with w in windows
            set i to 0
            repeat with t in tabs of w
              set i to i + 1
              set u to URL of t
              if \(cond) then
                set index of w to 1
                \(select)
                return "ok"
              end if
            end repeat
          end repeat
        end tell
        """)
    }
}

// MARK: - Step-by-step guide to the browser switch, with a picture of the menu, that notices when it is on

@MainActor enum BrowserGuide {
    private static var window: NSWindow?

    static func show(_ b: BrowserJS.Browser, done: @escaping () -> Void) {
        window?.close()
        let w = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 520, height: 520), styleMask: [.titled, .closable, .fullSizeContentView],
                        backing: .buffered, defer: false)
        w.title = L("Bật điều khiển nhạc")
        w.titlebarAppearsTransparent = true
        w.level = .floating  // stays visible over the browser while its menu is open
        w.isReleasedWhenClosed = false
        w.contentView = NSHostingView(rootView: BrowserGuideView(browser: b, close: { w.close() }, done: done))
        w.center()
        window = w
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }
}

struct BrowserGuideView: View {
    let browser: BrowserJS.Browser
    let close: () -> Void
    let done: () -> Void
    @State private var step = 0       // the highlighted step, cycling through the picture
    @State private var enabled = false

    var safari: Bool { browser.bid == "com.apple.Safari" }
    var steps: [(String, String)] {
        safari ? [(L("Mở cài đặt Safari"), L("Safari › Cài đặt… (⌘,)")),
                  (L("Hiện tính năng nhà phát triển"), L("Tab Nâng cao: bật \"Show features for web developers\"")),
                  (L("Bật JavaScript từ Apple Events"), "Tab Developer: tick \"Allow JavaScript from Apple Events\"")]
               : [(L("Mở menu Xem"), L("Trên thanh menu của {0}, bấm View (Xem)", browser.name)),
                  (L("Chọn Nhà phát triển"), L("Rê chuột vào Developer (Nhà phát triển)")),
                  (L("Bấm Allow JavaScript from Apple Events"), L("Mục cuối cùng. Có dấu ✓ là xong"))]
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 14) {
                if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: browser.bid) {
                    Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().frame(width: 48, height: 48)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(L("Cho notch điều khiển nhạc trong {0}", browser.name)).font(.title3.weight(.semibold))
                    Text(L("Làm một lần, khoảng 10 giây. Sau đó notch dừng, phát, tua và chuyển bài ngay trong tab."))
                        .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }

            // a fixed box: the drawn menus must never widen the window (that pushed everything off the left edge)
            MenuPicture(safari: safari, step: step).frame(width: 472, height: 170)

            VStack(alignment: .leading, spacing: 10) {
                ForEach(Array(steps.enumerated()), id: \.offset) { i, s in
                    HStack(alignment: .top, spacing: 12) {
                        Text("\(i + 1)").font(.system(size: 12, weight: .bold)).foregroundStyle(step == i ? .white : .secondary)
                            .frame(width: 22, height: 22).background(Circle().fill(step == i ? Color.accentColor : Color.secondary.opacity(0.2)))
                        VStack(alignment: .leading, spacing: 1) {
                            Text(s.0).font(.body.weight(.medium))
                            Text(s.1).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { step = i }
                }
            }

            HStack(spacing: 8) {
                if enabled {
                    Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    Text(L("Đã bật. Notch đã điều khiển được {0}.", browser.name)).font(.callout.weight(.medium))
                } else {
                    ProgressView().controlSize(.small)
                    Text(L("Đang chờ bạn bật… cửa sổ này tự nhận ra khi xong")).font(.callout).foregroundStyle(.secondary)
                }
            }
            .padding(10).frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 10).fill(enabled ? Color.green.opacity(0.12) : Color.secondary.opacity(0.08)))

            HStack {
                Button(enabled ? "Xong" : L("Để sau"), action: close).keyboardShortcut(.cancelAction)
                Spacer()
                if !enabled {
                    Button { Task.detached { BrowserJS.showPlayingTab(browser) } } label: { Label(L("Mở {0}", browser.name), systemImage: "arrow.up.forward.app") }
                        .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(24)
        .frame(width: 520)
        .task {
            // walk through the picture until the switch is on, checking the playing tab every 1.5 s
            var tick = 0
            while !enabled && !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1.5))
                tick += 1
                withAnimation(.easeInOut(duration: 0.35)) { step = tick % steps.count }
                if await Task.detached(operation: { BrowserJS.works(browser) }).value {
                    withAnimation(.spring) { enabled = true }
                    done()
                    try? await Task.sleep(for: .seconds(2))
                    close()
                }
            }
        }
    }
}

/// A drawing of where the switch is: the menu bar, the menu, the item, lit up one step at a time.
struct MenuPicture: View {
    let safari: Bool
    let step: Int

    var body: some View {
        ZStack(alignment: .topLeading) {
            RoundedRectangle(cornerRadius: 12).fill(LinearGradient(colors: [Color(white: 0.22), Color(white: 0.14)], startPoint: .top, endPoint: .bottom))
            if safari { settings } else { menus }
        }
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .environment(\.colorScheme, .dark)
    }

    func bar(_ items: [String], lit: String?) -> some View {
        HStack(spacing: 14) {
            Image(systemName: "apple.logo").font(.system(size: 11))
            ForEach(items, id: \.self) { i in
                Text(i).font(.system(size: 11, weight: i == items.first ? .bold : .regular))
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(RoundedRectangle(cornerRadius: 4).fill(i == lit ? Color.accentColor : .clear))
            }
            Spacer()
        }
        .foregroundStyle(.white).padding(.horizontal, 12).frame(height: 24).background(Color.white.opacity(0.08))
    }

    func menu(_ items: [String], lit: String?, checked: String? = nil, width: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(items, id: \.self) { i in
                HStack(spacing: 4) {
                    Text(i == checked ? "✓" : " ").frame(width: 10)
                    Text(i.hasSuffix(" ›") ? String(i.dropLast(2)) : i)
                    Spacer(minLength: 6)
                    if i.hasSuffix(" ›") { Image(systemName: "chevron.right").font(.system(size: 8, weight: .bold)) }
                }
                .font(.system(size: 11)).foregroundStyle(.white)
                .padding(.horizontal, 6).frame(height: 19)
                .background(RoundedRectangle(cornerRadius: 4).fill(i == lit ? Color.accentColor : .clear))
            }
        }
        .padding(4).frame(width: width)
        .background(RoundedRectangle(cornerRadius: 7).fill(Color(white: 0.2)).shadow(radius: 6))
        .overlay(RoundedRectangle(cornerRadius: 7).stroke(.white.opacity(0.12)))
    }

    var menus: some View {
        VStack(alignment: .leading, spacing: 0) {
            bar(["Chrome", "File", "Edit", "View", "History", "Bookmarks"], lit: "View")
            HStack(alignment: .top, spacing: -4) {
                menu(["Always Show Bookmarks Bar", "Full Screen", "Actual Size", "Developer ›"], lit: step >= 1 ? "Developer ›" : nil, width: 182)
                if step >= 1 {
                    menu(["Developer Tools", "JavaScript Console", "Allow JavaScript from Apple Events"],
                         lit: step >= 2 ? "Allow JavaScript from Apple Events" : nil,
                         checked: step >= 2 ? "Allow JavaScript from Apple Events" : nil, width: 248)
                        .padding(.top, 57)
                        .transition(.opacity.combined(with: .move(edge: .leading)))
                }
            }
            .padding(.leading, 38)
        }
    }

    var settings: some View {
        VStack(alignment: .leading, spacing: 10) {
            bar(["Safari", "File", "Edit", "View", "History"], lit: step == 0 ? "Safari" : nil)
            HStack(spacing: 14) {
                ForEach([("gearshape", "General"), ("lock", "Privacy"), ("gearshape.2", "Advanced"), ("hammer", "Developer")], id: \.1) { icon, name in
                    VStack(spacing: 2) {
                        Image(systemName: icon).font(.system(size: 14))
                        Text(name).font(.system(size: 9))
                    }
                    .foregroundStyle(.white).padding(6)
                    .background(RoundedRectangle(cornerRadius: 6).fill((step == 1 && name == "Advanced") || (step == 2 && name == "Developer") ? Color.accentColor : .clear))
                }
            }
            .padding(.horizontal, 14)
            HStack(spacing: 6) {
                Image(systemName: step >= 1 ? "checkmark.square.fill" : "square").foregroundStyle(step >= 1 ? Color.accentColor : .white)
                Text(step == 2 ? "Allow JavaScript from Apple Events" : "Show features for web developers").font(.system(size: 11)).foregroundStyle(.white)
            }
            .padding(.horizontal, 18)
        }
    }
}
