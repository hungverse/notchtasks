import AppKit
import SwiftUI
import IOKit.ps
import EventKit
import UserNotifications

// MARK: - Toasts: short Dynamic-Island style announcements under the notch

struct Toast: Identifiable, Equatable {
    let id = UUID()
    var symbol: String
    var tint: Color
    var title: String
    var subtitle: String = ""
    var icon: WatchItem? = nil   // app icon shown instead of the symbol
    var event: SoundEvent? = .taskDone  // which sound plays (Settings › Âm thanh)
    var url: URL? = nil          // click opens it
    var compact = false          // fits on the notch row (icon left, subtitle right), no drop-down
    var art: ToastArt? = nil     // an animated picture instead of the symbol
    var sticky = false           // stays until replaced (work in progress), at most 30 s
    var action: String? = nil    // the button's words; a toast with a url always gets a button
    var pane: String? = nil      // control-panel page opened by this toast's action
    var ask: AgentAsk? = nil     // an agent waiting on you: answer buttons instead of the action
    var toPhone = false          // also goes to the paired iPhone (task done, Mac too hot); CI, limits, low battery always do
    var actionTitle: String? { url == nil && pane == nil ? nil : action ?? L("Open") }
}

enum ToastArt: Equatable {
    case charge(percent: Int, plugged: Bool)
    case device(art: String?, symbol: String, connected: Bool)
    case loading
}

enum Notifier {
    static func post(_ t: Toast) {
        let c = UNMutableNotificationContent()
        c.title = t.title
        c.body = t.subtitle
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: t.id.uuidString, content: c, trigger: nil))
    }
}

// MARK: - Battery (IOKit power sources, no permission)

struct BatteryState: Equatable {
    var percent: Int
    var charging: Bool
    var plugged: Bool

    var symbol: String {
        if plugged { return "battery.100percent.bolt" }
        let bucket = [100, 75, 50, 25, 0].first { percent >= $0 - 12 } ?? 0
        return "battery.\(bucket)percent"
    }

    static func read() -> BatteryState? {
        guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else { return nil }
        for source in list {
            guard let d = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
                  d[kIOPSTypeKey] as? String == kIOPSInternalBatteryType else { continue }
            let current = d[kIOPSCurrentCapacityKey] as? Int ?? 0
            let capacity = max(d[kIOPSMaxCapacityKey] as? Int ?? 100, 1)
            return BatteryState(percent: current * 100 / capacity, charging: d[kIOPSIsChargingKey] as? Bool ?? false,
                                plugged: d[kIOPSPowerSourceStateKey] as? String == kIOPSACPowerValue)
        }
        return nil  // desktop Mac
    }
}

// MARK: - Calendar (EventKit, needs the Calendar permission)

struct CalEvent: Equatable {
    let id: String
    let title: String
    let start: Date
    let end: Date
    let color: Color
    let link: URL?
    var allDay = false
    var calendar = ""       // the calendar's name, so an event can be told apart from a same-named one
    var eventID: String?    // EventKit id: opens the event in Calendar

    /// Calendar's own link to show this event.
    var calendarURL: URL? { eventID.flatMap { URL(string: "ical://ekevent/\($0.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? $0)?method=show&options=more") } }
    /// "đến 6/11" for an all-day event spanning several days.
    var untilText: String? {
        let days = Calendar.current.dateComponents([.day], from: Calendar.current.startOfDay(for: start), to: end.addingTimeInterval(-1)).day ?? 0
        return allDay && days >= 1 ? L("until ") + end.addingTimeInterval(-1).formatted(.dateTime.day().month(.defaultDigits)) : nil
    }

    func running(at now: Date) -> Bool { !allDay && start <= now && now < end }
    /// 0…1 of the way through, while it runs.
    func elapsed(at now: Date) -> Double { min(1, max(0, now.timeIntervalSince(start) / max(end.timeIntervalSince(start), 1))) }
}

@MainActor final class CalendarSource {
    private let store = EKEventStore()

    static var authorized: Bool { EKEventStore.authorizationStatus(for: .event) == .fullAccess }

    /// Today's all-day events, then timed events that haven't ended and start within `hours`, in order.
    func events(within hours: Double) -> [CalEvent] {
        guard Self.authorized else { return [] }
        let now = Date()
        let predicate = store.predicateForEvents(withStart: now.addingTimeInterval(-6 * 3600), end: now.addingTimeInterval(hours * 3600), calendars: nil)
        let today = Calendar.current.startOfDay(for: now)
        return store.events(matching: predicate)
            .filter { $0.isAllDay ? $0.startDate < today.addingTimeInterval(86400) && $0.endDate > today : $0.endDate > now }
            .sorted { ($0.isAllDay ? 0 : 1, $0.startDate) < ($1.isAllDay ? 0 : 1, $1.startDate) }
            .prefix(10)
            .map { e in
                CalEvent(id: (e.eventIdentifier ?? "") + "\(e.startDate.timeIntervalSince1970)", title: e.title ?? L("Events"),
                         start: e.startDate, end: e.endDate, color: Color(nsColor: e.calendar.color), link: meetingLink(e), allDay: e.isAllDay,
                         calendar: e.calendar.title, eventID: e.eventIdentifier)
            }
    }
}

let meetingPattern = try! Regex(#"https://[\w.-]*(meet\.google\.com|zoom\.us|teams\.microsoft\.com|teams\.live\.com|webex\.com)/[^\s"<>]*"#)

/// Video-call link from the event's URL, location or notes.
func meetingLink(_ e: EKEvent) -> URL? {
    let text = [e.url?.absoluteString, e.location, e.notes].compactMap { $0 }.joined(separator: " ")
    return text.firstMatch(of: meetingPattern).flatMap { URL(string: String($0.output[0].substring ?? "")) }
}
