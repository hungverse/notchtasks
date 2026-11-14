import AppKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Look & feel options

enum NotchBackground: String, CaseIterable, Codable {
    case black, glass, gradient, glow
    var title: String {
        switch self {
        case .black: return L("Black")
        case .glass: return L("Frosted glass")
        case .gradient: return "Gradient"
        case .glow: return L("Glowing edge")
        }
    }
}

enum ToastStyle: String, CaseIterable, Codable {
    case island, compact, card, banner
    var title: String {
        switch self {
        case .island: return L("Island")
        case .compact: return L("Compact")
        case .card: return L("Floating card")
        case .banner: return L("Banner")
        }
    }
    var blurb: String {
        switch self {
        case .island: return L("Slides down from the notch.")
        case .compact: return L("Fits inside the notch.")
        case .card: return L("A card floating below the notch.")
        case .banner: return L("Wide, large text.")
        }
    }
}

enum Motion: String, CaseIterable, Codable {
    case smooth, bouncy, snappy, reduced
    var title: String {
        switch self {
        case .smooth: return L("Smooth")
        case .bouncy: return L("Bouncy")
        case .snappy: return L("Snappy")
        case .reduced: return L("Minimal")
        }
    }
    var animation: Animation {
        switch self {
        // a little overshoot reads as liquid; too much reads as rubber
        case .smooth: return .spring(response: 0.5, dampingFraction: 0.78)
        case .bouncy: return .spring(response: 0.55, dampingFraction: 0.64)
        case .snappy: return .spring(response: 0.3, dampingFraction: 0.86)
        case .reduced: return .easeInOut(duration: 0.15)
        }
    }
    /// For shrinking: no overshoot, so the shape never dips inside the camera housing and opens a gap at the top.
    var settle: Animation {
        switch self {
        case .smooth: return .spring(response: 0.45, dampingFraction: 1)
        case .bouncy: return .spring(response: 0.5, dampingFraction: 1)
        case .snappy: return .spring(response: 0.3, dampingFraction: 1)
        case .reduced: return .easeInOut(duration: 0.15)
        }
    }
}

enum BadgeShape: String, CaseIterable, Codable {
    case circle, rounded
    var title: String { self == .circle ? L("Circle") : L("Square") }
}

/// A fixed icon or picture on the collapsed notch.
struct Badge: Codable, Identifiable, Hashable {
    var id = UUID()
    var symbol = "star.fill"
    var image: String?  // a copy under Application Support; wins over text and symbol
    var text: String?   // a few letters or an emoji; wins over the symbol
    var color: String?  // background, hex; nil = accent color
    var fg: String?     // icon or text color, hex; nil = white (or the background color when there is no fill)
    var plain: Bool?    // no fill: the symbol or text itself takes the color
    var shape = BadgeShape.circle
    var border = false
    var right = false
    var sprite: String?  // a built-in animation (SpriteLibrary); wins over everything else
    var frames: Int?     // `image` is a spritesheet: this many frames side by side
    var fps: Double?     // speed of that spritesheet
    var animated: Bool { sprite != nil || frames != nil || image?.lowercased().hasSuffix(".gif") == true }
    /// The paired group's NotchTasks Pro (SyncHub keeps it): four icons a side instead of two.
    nonisolated(unsafe) static var pro = false
    static var perSide: Int { pro ? 4 : 2 }  // the wings also carry the live activities
    static var limit: Int { perSide * 2 }
}

extension [Badge] {
    /// Swaps two icons' places, across sides too: each takes the other's position and side.
    mutating func swapBadges(_ a: UUID, _ b: UUID) {
        guard a != b, let i = firstIndex(where: { $0.id == a }), let j = firstIndex(where: { $0.id == b }) else { return }
        let (ri, rj) = (self[i].right, self[j].right)
        swapAt(i, j)
        self[i].right = ri
        self[j].right = rj
    }

    /// Moves an icon to the outer end of the other side, if that side has room.
    mutating func moveBadge(_ id: UUID, right: Bool) {
        guard let i = firstIndex(where: { $0.id == id }), self[i].right != right,
              filter({ $0.right == right }).count < Badge.perSide else { return }
        var b = remove(at: i)
        b.right = right
        // the left side lists outside-in, the right inside-out: "outer end" is the front on the left, the back on the right
        if right { append(b) } else { insert(b, at: firstIndex { !$0.right } ?? endIndex) }
    }
}

/// What the display-only notch above the lock screen may show.
enum LockContent: String, CaseIterable {
    case lock, badge, activity, battery
    var title: String {
        switch self {
        case .lock: return L("Lock icon")
        case .badge: return L("Pinned icon")
        case .activity: return L("Tasks, music, timers")
        case .battery: return L("Battery")
        }
    }
    var symbol: String {
        switch self {
        case .lock: return "lock.fill"
        case .badge: return "star.square.on.square"
        case .activity: return "waveform"
        case .battery: return "battery.75percent"
        }
    }
}

enum Placement: String, CaseIterable, Codable {
    case auto, left, right
    var title: String {
        switch self {
        case .auto: return L("Automatic")
        case .left: return L("Left side")
        case .right: return L("Right side")
        }
    }
}

// MARK: - Sounds

enum SoundEvent: String, CaseIterable, Codable, Identifiable {
    case taskDone, agentAsk, ciPassed, ciFailed, limitWarn, meeting, timerDone, charging, unplugged, lowBattery, deviceOn, deviceOff,
         callStart, callEnd, sensorOn, authFail, netDown, netUp, downloadDone, warning, open, close
    var id: String { rawValue }
    var title: String {
        switch self {
        case .taskDone: return L("AI / task done")
        case .agentAsk: return L("Agent asks for approval")
        case .ciPassed: return L("CI passed")
        case .ciFailed: return L("CI failed")
        case .limitWarn: return L("AI limit running out")
        case .meeting: return L("Upcoming event")
        case .timerDone: return L("Timer done")
        case .charging: return L("Plugged in")
        case .unplugged: return L("Unplugged")
        case .lowBattery: return L("Low battery")
        case .deviceOn: return L("Device connected")
        case .deviceOff: return L("Device disconnected")
        case .callStart: return L("Call started")
        case .callEnd: return L("Call ended")
        case .sensorOn: return L("Mic or camera on")
        case .authFail: return L("Failed unlock")
        case .netDown: return L("Offline")
        case .netUp: return L("Back online, VPN")
        case .downloadDone: return L("Download done")
        case .warning: return L("Hot computer, disk full")
        case .open: return L("Opening the notch")
        case .close: return L("Notch collapses")
        }
    }
    var symbol: String {
        switch self {
        case .taskDone: return "checkmark.circle.fill"
        case .agentAsk: return "hand.raised.fill"
        case .ciPassed: return "checkmark.seal.fill"
        case .ciFailed: return "xmark.octagon.fill"
        case .limitWarn: return "gauge.with.dots.needle.100percent"
        case .meeting: return "calendar"
        case .timerDone: return "timer"
        case .charging: return "bolt.fill"
        case .unplugged: return "powerplug.portrait"
        case .lowBattery: return "battery.25percent"
        case .deviceOn: return "airpods"
        case .deviceOff: return "airpods"
        case .callStart: return "phone.fill"
        case .callEnd: return "phone.down.fill"
        case .sensorOn: return "mic.fill"
        case .authFail: return "lock.trianglebadge.exclamationmark.fill"
        case .netDown: return "wifi.slash"
        case .netUp: return "wifi"
        case .downloadDone: return "arrow.down.circle.fill"
        case .warning: return "exclamationmark.triangle.fill"
        case .open: return "chevron.down.circle"
        case .close: return "chevron.up.circle"
        }
    }
}

struct SoundPack {
    let name: String
    let map: [SoundEvent: String]  // "" = silent

    static var all: [SoundPack] { [
        SoundPack(name: L("Default"), map: [.taskDone: "Glass", .agentAsk: "Ping", .ciPassed: "Hero", .ciFailed: "Basso", .limitWarn: "Funk", .meeting: "Ping",
                                          .timerDone: "Glass", .charging: "Pop", .unplugged: "", .lowBattery: "Funk", .deviceOn: "Pop", .deviceOff: "Bottle",
                                          .callStart: "Tink", .callEnd: "Bottle", .sensorOn: "", .authFail: "Basso",
                                          .netDown: "Basso", .netUp: "Tink", .downloadDone: "Glass", .warning: "Funk", .open: "", .close: ""]),
        SoundPack(name: L("Minimal"), map: [.taskDone: "Tink", .agentAsk: "Tink", .ciPassed: "Tink", .ciFailed: "Pop", .limitWarn: "Pop", .meeting: "Tink",
                                          .timerDone: "Tink", .charging: "Tink", .unplugged: "", .lowBattery: "Pop", .deviceOn: "Tink", .deviceOff: "",
                                          .callStart: "Tink", .callEnd: "", .sensorOn: "", .authFail: "Pop",
                                          .netDown: "Pop", .netUp: "", .downloadDone: "Tink", .warning: "Pop", .open: "", .close: ""]),
        SoundPack(name: L("Lively"), map: [.taskDone: "Hero", .agentAsk: "Submarine", .ciPassed: "Hero", .ciFailed: "Sosumi", .limitWarn: "Funk", .meeting: "Glass",
                                          .timerDone: "Submarine", .charging: "Bottle", .unplugged: "Blow", .lowBattery: "Funk", .deviceOn: "Hero", .deviceOff: "Blow",
                                          .callStart: "Glass", .callEnd: "Blow", .sensorOn: "Pop", .authFail: "Sosumi",
                                          .netDown: "Sosumi", .netUp: "Hero", .downloadDone: "Hero", .warning: "Funk", .open: "Pop", .close: "Tink"]),
        SoundPack(name: "Retro", map: [.taskDone: "Morse", .agentAsk: "Ping", .ciPassed: "Frog", .ciFailed: "Basso", .limitWarn: "Purr", .meeting: "Ping",
                                       .timerDone: "Submarine", .charging: "Bottle", .unplugged: "Blow", .lowBattery: "Sosumi", .deviceOn: "Morse", .deviceOff: "Purr",
                                       .callStart: "Morse", .callEnd: "Purr", .sensorOn: "", .authFail: "Basso",
                                       .netDown: "Basso", .netUp: "Morse", .downloadDone: "Frog", .warning: "Sosumi", .open: "", .close: ""]),
        SoundPack(name: L("Silent"), map: Dictionary(uniqueKeysWithValues: SoundEvent.allCases.map { ($0, "") })),
    ] }
}

enum Sounds {
    /// Built-in macOS alert sounds (Basso, Glass, Hero…).
    static let system: [String] = {
        let dir = "/System/Library/Sounds"
        return ((try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [])
            .map { ($0 as NSString).deletingPathExtension }.sorted()
    }()

    @MainActor static func play(_ event: SoundEvent) {
        let s = AppSettings.shared
        guard s.sounds else { return }
        play(name: s.soundMap[event.rawValue] ?? SoundPack.all[0].map[event] ?? "", volume: s.volume)
    }

    /// A system sound name, or an absolute path to the user's own file.
    @MainActor static func play(name: String, volume: Double) {
        guard !name.isEmpty else { return }
        let sound = name.hasPrefix("/") ? NSSound(contentsOfFile: name, byReference: true) : NSSound(named: name)
        sound?.volume = Float(volume)
        sound?.play()
    }

    static func displayName(_ name: String) -> String {
        name.isEmpty ? L("No") : name.hasPrefix("/") ? "♪ " + ((name as NSString).lastPathComponent) : name
    }
}

// MARK: - Sound pack files: share or back up your sounds, custom audio files included

/// A `.notchsounds` file: JSON with each event's sound and the custom audio files themselves (base64),
/// so a pack works on another Mac. Built-in sounds travel by name.
struct SoundPackFile: Codable {
    static let type = UTType(exportedAs: "com.notchtasks.sounds")
    var format = "notchtasks.sounds"
    var version = 1
    var volume: Double?
    var sounds: [String: String]   // event → built-in name, "" (silent) or "file:<name>"
    var files: [String: String] = [:]  // name → base64 audio

    static let audioTypes: Set<String> = ["aiff", "aif", "aifc", "wav", "mp3", "m4a", "caf", "aac"]
    static let maxFile = 8_000_000  // a notification sound is a few hundred KB; refuse anything huge

    /// Where custom sounds live: inside the app's own folder, so they survive the original being moved or deleted.
    static var folder: URL {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/NotchTasks/Sounds")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Copies a chosen audio file into `folder` (same name, numbered if taken by a different file) and returns its path.
    static func keep(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url), data.count <= maxFile else { return nil }
        return store(data, name: url.lastPathComponent)
    }

    static func store(_ data: Data, name: String) -> String? {
        let clean = (name as NSString).lastPathComponent.replacingOccurrences(of: "/", with: "_")
        let ext = (clean as NSString).pathExtension.lowercased()
        guard audioTypes.contains(ext), !clean.hasPrefix("."), data.count <= maxFile else { return nil }
        let base = (clean as NSString).deletingPathExtension
        var dest = folder.appendingPathComponent(clean)
        var n = 2
        while let existing = try? Data(contentsOf: dest), existing != data {
            dest = folder.appendingPathComponent("\(base) \(n).\(ext)")
            n += 1
        }
        if !FileManager.default.fileExists(atPath: dest.path) { guard (try? data.write(to: dest)) != nil else { return nil } }
        return dest.path
    }

    init(map: [String: String], volume: Double) {
        self.volume = volume
        var sounds: [String: String] = [:]
        for (event, value) in map where SoundEvent(rawValue: event) != nil {
            guard value.hasPrefix("/") else { sounds[event] = value; continue }
            let name = (value as NSString).lastPathComponent
            guard let data = try? Data(contentsOf: URL(fileURLWithPath: value)), data.count <= Self.maxFile else { continue }
            files[name] = data.base64EncodedString()
            sounds[event] = "file:" + name
        }
        self.sounds = sounds
    }

    /// The events this pack sets, ready for `soundMap`; unknown events, unknown built-in names and bad files are dropped.
    func resolved(system: [String]) -> [String: String] {
        var out: [String: String] = [:]
        for (event, value) in sounds where SoundEvent(rawValue: event) != nil {
            if value.isEmpty || system.contains(value) { out[event] = value; continue }
            guard value.hasPrefix("file:"), let b64 = files[String(value.dropFirst(5))], let data = Data(base64Encoded: b64),
                  let path = Self.store(data, name: String(value.dropFirst(5))) else { continue }
            out[event] = path
        }
        return out
    }
}

// MARK: - Themes: one-click templates

struct Theme: Identifiable {
    let id: String
    let title: String
    let background: NotchBackground
    let gradient: (String, String)
    let accent: String         // palette name or "custom"
    let customAccent: String
    let toast: ToastStyle
    let motion: Motion  // no sounds: they are the user's own and survive a theme change

    static var all: [Theme] { [
        Theme(id: "classic", title: L("Classic"), background: .black, gradient: ("#1E1B4B", "#000000"), accent: "auto", customAccent: "#FF9F0A",
              toast: .island, motion: .bouncy),
        Theme(id: "neon", title: "Neon", background: .glow, gradient: ("#0F172A", "#000000"), accent: "custom", customAccent: "#00F5D4",
              toast: .card, motion: .bouncy),
        Theme(id: "glass", title: L("Glass"), background: .glass, gradient: ("#334155", "#0F172A"), accent: "white", customAccent: "#FFFFFF",
              toast: .banner, motion: .smooth),
        Theme(id: "minimal", title: L("Minimal"), background: .black, gradient: ("#111111", "#000000"), accent: "white", customAccent: "#FFFFFF",
              toast: .compact, motion: .snappy),
        Theme(id: "sunset", title: L("Sunset"), background: .gradient, gradient: ("#FF5F6D", "#5B247A"), accent: "custom", customAccent: "#FFC371",
              toast: .card, motion: .bouncy),
        Theme(id: "ocean", title: L("Ocean"), background: .gradient, gradient: ("#0EA5E9", "#1E1B4B"), accent: "custom", customAccent: "#7DD3FC",
              toast: .island, motion: .smooth),
    ] }
}

// MARK: - Colors as hex strings (stored in UserDefaults / profiles)

extension Color {
    init?(hex: String) {
        let s = hex.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "#", with: "")
        guard s.count == 6, let v = UInt32(s, radix: 16) else { return nil }
        self.init(red: Double((v >> 16) & 0xFF) / 255, green: Double((v >> 8) & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
    }

    var hex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .black
        return String(format: "#%02X%02X%02X", Int((c.redComponent * 255).rounded()), Int((c.greenComponent * 255).rounded()),
                      Int((c.blueComponent * 255).rounded()))
    }
}

// MARK: - Profile: everything customizable, as one shareable JSON file

struct Profile: Codable {
    var modules: [String]
    var disabled: [String]
    var expandMode: String
    var hoverDelay: Double
    var haptics: Bool
    var sounds: Bool
    var systemNotifications: Bool
    var size: String
    var accent: String
    var customAccent: String
    var menuBarIcon: Bool
    var doneAfter: Double
    var limitWarn: Double
    var background: NotchBackground
    var gradientA: String
    var gradientB: String
    var toastStyle: ToastStyle
    var motion: Motion
    var moduleColors: [String: String]
    var placements: [String: String]
    var soundMap: [String: String]
    var volume: Double
}
