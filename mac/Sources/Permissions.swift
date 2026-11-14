import AppKit
import SwiftUI
import EventKit
import UserNotifications
import ServiceManagement
import ApplicationServices
import AVFoundation

// MARK: - Permission center: real status, ask, open Settings, revoke

enum PermState: Equatable {
    case granted, denied, notAsked, checking, needsApp  // needsApp: Automation can only be checked while the target app runs

    var label: String {
        switch self {
        case .granted: return L("Đã cho phép")
        case .denied: return L("Đã từ chối")
        case .notAsked: return L("Chưa cấp")
        case .checking: return L("Đang kiểm tra…")
        case .needsApp: return L("Chưa kiểm tra")
        }
    }
    var color: Color {
        switch self {
        case .granted: return .green
        case .denied: return .red
        case .notAsked: return .orange
        case .checking, .needsApp: return .gray
        }
    }

    var afterTimeout: PermState { self == .checking ? .needsApp : self }
}

struct PermItem: Identifiable, Equatable {
    enum Group { case needed, granted, unused }
    let id: String
    let title: String
    let detail: String
    let symbol: String
    let module: ModuleID?
    var state: PermState
    let pane: String        // System Settings deep link
    let tccService: String? // `tccutil reset <service>` revokes it for this app

    func group(featureOn: Bool) -> Group {
        if !featureOn { return .unused }
        return state == .granted ? .granted : .needed
    }

    static func updating(_ list: [PermItem], id: String, state: PermState) -> [PermItem] {
        list.map { item in var item = item; if item.id == id { item.state = state }; return item }
    }

    static func selfTest() {
        let item = PermItem(id: "test", title: "", detail: "", symbol: "", module: .media,
                            state: .notAsked, pane: "", tccService: nil)
        precondition(item.group(featureOn: true) == .needed
                     && item.group(featureOn: false) == .unused
                     && PermItem(id: item.id, title: item.title, detail: item.detail, symbol: item.symbol,
                                 module: item.module, state: .granted, pane: item.pane,
                                 tccService: item.tccService).group(featureOn: true) == .granted, "permission grouping")
        precondition(updating([item], id: "test", state: .granted).first?.state == .granted
                     && updating([item], id: "other", state: .granted) == [item], "late automation answer")
        precondition(PermState.checking.afterTimeout == .needsApp
                     && PermState.granted.afterTimeout == .granted, "automation timeout")
    }
}

@MainActor final class Permissions: ObservableObject {
    @Published var items: [PermItem] = [] { didSet {
        let before = Dictionary(oldValue.map { ($0.id, $0.state) }, uniquingKeysWith: { a, _ in a })
        for i in items where i.state == .granted && [.notAsked, .denied].contains(before[i.id]) {
            Track.event("permission_granted", ["perm": i.id.hasPrefix("js.") ? "browser" : i.id])
        }
    } }
    private let calendarStore = EKEventStore()  // must outlive the request, or the prompt never comes back
    private var automationInFlight: Set<String> = []

    nonisolated static func begin(_ id: String, in flights: inout Set<String>) -> Bool { flights.insert(id).inserted }

    nonisolated static func selfTest() {
        var flights: Set<String> = []
        precondition(begin("app", in: &flights) && !begin("app", in: &flights), "one automation check per target")
    }

    static let automationTargets: [(id: String, name: String)] = [
        ("com.spotify.client", "Spotify"), ("com.apple.Music", "Music"), ("com.google.Chrome", "Google Chrome"),
        ("com.apple.Safari", "Safari"), ("com.microsoft.edgemac", "Microsoft Edge"), ("com.brave.Browser", "Brave"),
        ("company.thebrowser.Browser", "Arc"),
    ]
    static let privacy = "x-apple.systempreferences:com.apple.preference.security?"

    var grantedCount: Int { items.filter { $0.state == .granted }.count }

    func refresh() {
        var list: [PermItem] = []
        let previous = Dictionary(uniqueKeysWithValues: items.map { ($0.id, $0.state) })
        list.append(PermItem(id: "notifications", title: L("Thông báo"), detail: L("Báo khi tác vụ xong hoặc có lỗi."),
                             symbol: "bell.badge.fill", module: nil,
                             state: previous["notifications"] ?? .notAsked,
                             pane: "x-apple.systempreferences:com.apple.Notifications-Settings.extension", tccService: nil))
        let cal = EKEventStore.authorizationStatus(for: .event)
        list.append(PermItem(id: "calendar", title: L("Lịch"), detail: L("Hiện sự kiện sắp tới."),
                             symbol: "calendar", module: .calendar,
                             state: cal == .fullAccess ? .granted : cal == .notDetermined ? .notAsked : .denied,
                             pane: Self.privacy + "Privacy_Calendars", tccService: "Calendar"))
        let camera = AVCaptureDevice.authorizationStatus(for: .video)
        list.append(PermItem(id: "camera", title: L("Gương camera"), detail: L("Xem camera trước khi vào họp"),
                             symbol: "camera.fill", module: .mirror,
                             state: camera == .authorized ? .granted : camera == .notDetermined ? .notAsked : .denied,
                             pane: Self.privacy + "Privacy_Camera", tccService: "Camera"))
        list.append(PermItem(id: "accessibility", title: L("Trợ năng"), detail: L("Điều khiển nhạc trong trình duyệt."),
                             symbol: "accessibility", module: .media, state: AXIsProcessTrusted() ? .granted : .notAsked,
                             pane: Self.privacy + "Privacy_Accessibility", tccService: "Accessibility"))
        for t in Self.automationTargets where NSWorkspace.shared.urlForApplication(withBundleIdentifier: t.id) != nil {
            list.append(PermItem(id: "ae." + t.id, title: L("Điều khiển {0}", t.name), detail: L("Đọc và điều khiển bài đang phát."),
                                 symbol: "music.note", module: .media,
                                 state: automationInFlight.contains(t.id) ? previous["ae." + t.id] ?? .checking : .checking,
                                 pane: Self.privacy + "Privacy_Automation", tccService: "AppleEvents"))
        }
        for b in BrowserJS.installed {
            list.append(PermItem(id: "js." + b.bid, title: L("Nhạc trong {0}", b.name), detail: L("Dừng, phát, chuyển bài ngay trong tab."),
                                 symbol: "play.rectangle.fill", module: .media, state: BrowserJS.allowed(b) ? .granted : .notAsked,
                                 pane: "", tccService: nil))
        }
        list.append(PermItem(id: "login", title: L("Mở cùng macOS"), detail: L("Tự chạy khi đăng nhập."),
                             symbol: "power", module: nil, state: SMAppService.mainApp.status == .enabled ? .granted : .notAsked,
                             pane: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension", tccService: nil))
        if list != items {
            appLog("permissions.log", list.map { "\($0.id)=\($0.state)" }.joined(separator: " ")
                   + " · cal=\(cal.rawValue) ax=\(AXIsProcessTrusted())")
            items = list
        }
        Task {
            let notif = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
            setState("notifications", notif == .authorized || notif == .provisional ? .granted : notif == .denied ? .denied : .notAsked)
        }
        for t in Self.automationTargets where list.contains(where: { $0.id == "ae." + t.id }) {
            checkAutomation(t.id)
        }
    }

    private func setState(_ id: String, _ state: PermState) {
        let updated = PermItem.updating(items, id: id, state: state)
        if updated != items { items = updated }
    }

    private func checkAutomation(_ id: String, ask: Bool = false) {
        guard Self.begin(id, in: &automationInFlight) else { return }
        setState("ae." + id, .checking)
        let timeout = Task {
            guard (try? await Task.sleep(for: .seconds(3))) != nil else { return }
            if let state = items.first(where: { $0.id == "ae." + id })?.state {
                setState("ae." + id, state.afterTimeout)
            }
        }
        Task {
            let state = await Task.detached { Self.automation(id, ask: ask) }.value
            timeout.cancel()
            automationInFlight.remove(id)
            setState("ae." + id, state)
        }
    }

    nonisolated static func automation(_ bundleID: String, ask: Bool) -> PermState {
        guard let desc = NSAppleEventDescriptor(bundleIdentifier: bundleID).aeDesc else { return .needsApp }
        switch AEDeterminePermissionToAutomateTarget(desc, typeWildCard, typeWildCard, ask) {
        case noErr: return .granted
        case OSStatus(errAEEventNotPermitted): return .denied
        case OSStatus(errAEEventWouldRequireUserConsent): return .notAsked
        default: return .needsApp  // procNotFound: target isn't running
        }
    }

    /// Ask for it; macOS never re-asks after a refusal, so a denied permission opens Settings instead.
    /// Set by the app: the browser switch is explained and flipped by the model, which can also report back on the notch.
    var enableBrowser: ((String) -> Void)?

    func request(_ p: PermItem) {
        Track.event("permission_request", ["perm": p.id.hasPrefix("js.") ? "browser" : p.id])
        if p.id.hasPrefix("js.") { enableBrowser?(String(p.id.dropFirst(3))); refreshUntilGranted(p.id); return }
        if p.state == .denied { openSettings(p); return }
        switch p.id {
        case "notifications":
            Task { _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]); refresh() }
        case "calendar":
            Task {
                do { appLog("permissions.log", "calendar request → \(try await calendarStore.requestFullAccessToEvents())") }
                catch { appLog("permissions.log", "calendar request failed: \(error)") }
                refresh()
            }
        case "camera":
            Task { _ = await AVCaptureDevice.requestAccess(for: .video); refresh() }
        case "accessibility":
            let prompt = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            appLog("permissions.log", "accessibility prompt → \(AXIsProcessTrustedWithOptions(prompt))")
            refreshUntilGranted(p.id)
        case "login":
            try? SMAppService.mainApp.register()
            refresh()
        default:
            let bid = String(p.id.dropFirst(3))
            if p.state == .needsApp, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bid) {
                NSWorkspace.shared.openApplication(at: url, configuration: .init())  // must run to be asked
                refreshSoon()
                return
            }
            checkAutomation(bid, ask: true)
        }
    }

    /// macOS has no "revoke" API: reset this app's entry (it will ask again next time) or open Settings.
    func revoke(_ p: PermItem) {
        if p.id.hasPrefix("js."), let b = BrowserJS.browser(String(p.id.dropFirst(3))) {
            // a switch inside the browser: only the browser can turn it off
            let alert = NSAlert()
            alert.messageText = L("Tắt trong {0}", b.name)
            alert.informativeText = L("Bỏ chọn cùng mục đó:\n") + BrowserJS.steps(b)
            alert.runModal()
            return
        }
        if p.id == "login" {
            try? SMAppService.mainApp.unregister()
        } else if let service = p.tccService {
            let tcc = Process()
            tcc.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
            tcc.arguments = ["reset", service, Bundle.main.bundleIdentifier ?? "com.notchtasks.mac"]
            try? tcc.run()
            tcc.waitUntilExit()
        } else {
            openSettings(p)
        }
        refresh()
    }

    /// Clears this app's stale entry, then asks again so a fresh one appears in Settings.
    func resetAndAsk(_ p: PermItem) {
        revoke(p)
        appLog("permissions.log", "reset \(p.id)")
        request(PermItem(id: p.id, title: p.title, detail: p.detail, symbol: p.symbol, module: p.module, state: .notAsked,
                         pane: p.pane, tccService: p.tccService))
        openSettings(p)
    }

    func openSettings(_ p: PermItem) {
        if let url = URL(string: p.pane) { NSWorkspace.shared.open(url) }
        refreshSoon()
    }

    private func refreshSoon() {
        Task {
            try? await Task.sleep(for: .seconds(2))
            refresh()
        }
    }

    /// Waits for a switch flipped in another app (browsers write their settings file a few seconds late).
    private func refreshUntilGranted(_ id: String) {
        Task {
            for _ in 0..<30 {   // about a minute
                try? await Task.sleep(for: .seconds(2))
                refresh()
                if items.first(where: { $0.id == id })?.state == .granted { return }
            }
        }
    }
}
