import AppKit
import SwiftUI
import Darwin

struct Requirement {
    let minOS: OSVersion?
    let appleSilicon: Bool

    init(minOS: OSVersion? = nil, appleSilicon: Bool = false) {
        self.minOS = minOS; self.appleSilicon = appleSilicon
    }

    func met(on system: (os: OSVersion, appleSilicon: Bool)) -> Bool {
        (minOS.map { system.os >= $0 } ?? true) && (!appleSilicon || system.appleSilicon)
    }

    var label: String {
        [minOS.map { L("macOS {0}+", $0.values.map(String.init).joined(separator: ".")) },
         appleSilicon ? L("Apple silicon") : nil].compactMap { $0 }.joined(separator: " · ")
    }

    static let fan = Requirement(appleSilicon: true)
    static var current: (os: OSVersion, appleSilicon: Bool) {
        var value: Int32 = 0, size = MemoryLayout<Int32>.size
        let silicon = sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0 && value == 1
        return (OSVersion(ProcessInfo.processInfo.operatingSystemVersion), silicon)
    }

    static func selfTest() {
        let os = Requirement(minOS: OSVersion("15")!)
        precondition(!os.met(on: (OSVersion("14")!, true)) && os.met(on: (OSVersion("15")!, true))
                     && os.met(on: (OSVersion("16")!, true)), "minimum OS requirement")
        precondition(fan.met(on: (OSVersion("14")!, true)) && !fan.met(on: (OSVersion("14")!, false)), "Apple silicon requirement")
        precondition(os.label == L("macOS {0}+", "15") && fan.label == L("Apple silicon")
                     && Requirement(minOS: OSVersion("15")!, appleSilicon: true).label == L("macOS {0}+", "15") + " · " + L("Apple silicon"), "requirement labels")
    }
}

// MARK: - Modules & user settings (UserDefaults-backed)

enum ModuleID: String, CaseIterable, Identifiable {
    // default order on the notch
    case agents, ci, media, calendar, timer, shelf, limits, battery, devices, calls, privacy, levels, network, downloads, health, mirror
    var id: String { rawValue }
    var requirement: Requirement? { nil }

    var title: String {
        switch self {
        case .agents: return "AI agent"
        case .ci: return "Build & CI"
        case .media: return L("Now Playing")
        case .limits: return L("AI limits")
        case .battery: return L("Battery")
        case .calendar: return L("Calendar")
        case .shelf: return L("File shelf")
        case .timer: return L("Timer")
        case .devices: return L("Devices")
        case .calls: return L("Calls")
        case .privacy: return L("Mic & camera")
        case .levels: return L("Volume & brightness")
        case .network: return L("Network")
        case .downloads: return L("Downloads")
        case .health: return L("Computer health")
        case .mirror: return L("Camera mirror")
        }
    }

    var symbol: String {
        switch self {
        case .agents: return "sparkles"
        case .ci: return "hammer.fill"
        case .media: return "music.note"
        case .limits: return "gauge.with.dots.needle.67percent"
        case .battery: return "battery.75percent"
        case .calendar: return "calendar"
        case .shelf: return "tray.full.fill"
        case .timer: return "timer"
        case .devices: return "airpods"
        case .calls: return "phone.fill"
        case .privacy: return "mic.fill"
        case .levels: return "speaker.wave.2.fill"
        case .network: return "wifi"
        case .downloads: return "arrow.down.circle.fill"
        case .health: return "thermometer.medium"
        case .mirror: return "person.crop.square.fill"
        }
    }

    var blurb: String {
        switch self {
        case .agents: return L("Agents and AI apps at work")
        case .ci: return L("Build, test, install, render, CI")
        case .media: return L("What's playing, with controls")
        case .limits: return L("Agent usage, alerts before running out")
        case .battery: return L("Plugged in, unplugged, low battery")
        case .calendar: return L("Upcoming events, with meeting links")
        case .shelf: return L("Drop files on the notch to keep them")
        case .timer: return L("Quick timers")
        case .devices: return L("Headphones, keyboards, mice and their batteries")
        case .calls: return L("Call time, mute button (FaceTime, Zoom…)")
        case .privacy: return L("Alerts when an app uses the mic or camera")
        case .levels: return L("Volume and brightness as you change them")
        case .network: return L("Offline, back online, VPN")
        case .downloads: return L("Download finished, show in folder")
        case .health: return L("Computer hot, disk almost full")
        case .mirror: return L("Check the camera before joining a meeting")
        }
    }

    /// Where it sits on the Overview page: a few short groups read faster than one long grid.
    static var groups: [(title: String, modules: [ModuleID])] { [
        (L("Work"), [.agents, .ci, .limits, .calendar, .timer]),
        (L("Music & devices"), [.media, .devices, .battery, .levels]),
        (L("Calls & privacy"), [.calls, .privacy, .mirror]),
        (L("System"), [.network, .downloads, .health, .shelf]),
    ] }

    /// Permissions this module may ask for (shown in the module list).
    var needs: String? {
        switch self {
        case .media: return L("App control")
        case .calendar: return L("Calendar")
        case .downloads: return L("Downloads folder")
        case .mirror: return "Camera"
        default: return nil
        }
    }
}

enum ExpandMode: String, CaseIterable { case hover, click }
enum NotchSize: String, CaseIterable { case compact, regular }


@MainActor final class AppSettings: ObservableObject {
    static let shared = AppSettings()

    @Published var modules: [ModuleID] { didSet { store(modules.map(\.rawValue), "modules") } }  // display order
    @Published var disabled: Set<ModuleID> { didSet {
        store(disabled.map(\.rawValue), "disabledModules")
        for m in disabled.symmetricDifference(oldValue) { Track.event("module_toggle", ["module": m.rawValue, "on": disabled.contains(m) ? 0 : 1]) }
    } }
    @Published var expandMode: ExpandMode { didSet { store(expandMode.rawValue, "expandMode") } }
    @Published var hoverDelay: Double { didSet { store(hoverDelay, "hoverDelay") } }
    @Published var haptics: Bool { didSet { store(haptics, "haptics") } }
    @Published var systemLoad: Bool { didSet { store(systemLoad, "systemLoad") } }  // CPU and RAM on the open notch
    @Published var sounds: Bool { didSet { store(sounds, "sounds") } }
    @Published var systemNotifications: Bool { didSet { store(systemNotifications, "systemNotifications") } }
    @Published var size: NotchSize { didSet { store(size.rawValue, "size") } }
    @Published var accent: String { didSet { store(accent, "accent"); Icons.tints = [:] } }  // "auto" or a palette name
    @Published var menuBarIcon: Bool { didSet { store(menuBarIcon, "menuBarIcon") } }
    @Published var onboardingDone: Bool { didSet { store(onboardingDone, "onboardingDone") } }
    @Published var doneAfter: Double { didSet { store(doneAfter, "doneAfter") } }  // a task must run this long to be announced
    @Published var limitWarn: Double { didSet { store(limitWarn, "limitWarn") } }  // % used that triggers a warning

    @Published var customAccent: String { didSet { store(customAccent, "customAccent"); Icons.tints = [:] } }
    @Published var background: NotchBackground { didSet { store(background.rawValue, "background") } }
    @Published var gradientA: String { didSet { store(gradientA, "gradientA") } }
    @Published var gradientB: String { didSet { store(gradientB, "gradientB") } }
    @Published var toastStyle: ToastStyle { didSet { store(toastStyle.rawValue, "toastStyle") } }
    @Published var motion: Motion { didSet { store(motion.rawValue, "motion") } }
    @Published var moduleColors: [String: String] { didSet { store(moduleColors, "moduleColors") } }  // module → hex
    @Published var placements: [String: String] { didSet { store(placements, "placements") } }       // module → Placement
    @Published var soundMap: [String: String] { didSet { store(soundMap, "soundMap") } }             // event → sound name/path
    @Published var volume: Double { didSet { store(volume, "volume") } }
    @Published var lockScreen: Bool { didSet { store(lockScreen, "lockScreen") } }
    @Published var lockShows: Set<LockContent> { didSet { store(lockShows.map(\.rawValue), "lockShows") } }
    @Published var badges: [Badge] { didSet { store(json(badges), "badges") } }
    @Published var lockOwnBadges: Bool { didSet { store(lockOwnBadges, "lockOwnBadges") } }  // false: same as the main screen
    @Published var lockBadges: [Badge] { didSet { store(json(lockBadges), "lockBadges") } }
    @Published var toolIcons: [String: Badge] { didSet { store(json(toolIcons), "toolIcons") } }  // detected build tool → icon
    @Published var seenTools: [String] { didSet { store(seenTools, "seenTools") } }  // tools detected on this Mac, newest first
    @Published var lockIconRight: Bool { didSet { store(lockIconRight, "lockIconRight") } }
    @Published var lockBatteryLeft: Bool { didSet { store(lockBatteryLeft, "lockBatteryLeft") } }
    @Published var lockAuth: Bool { didSet { store(lockAuth, "lockAuth") } }  // react to wrong passwords / fingers

    static var accents: [(name: String, title: String, color: Color?)] { [
        ("auto", L("Match app color"), nil), ("blue", L("Blue"), .blue), ("green", L("Green"), .green),
        ("orange", L("Orange"), .orange), ("pink", L("Pink"), .pink), ("purple", L("Purple"), .purple), ("white", L("White"), .white),
        ("custom", L("Custom…"), nil),
    ] }
    var accentColor: Color? { accent == "custom" ? Color(hex: customAccent) : Self.accents.first { $0.name == accent }?.color }

    /// The user's own color for a module, nil = default (app icon / semantic color).
    func color(for m: ModuleID) -> Color? { moduleColors[m.rawValue].flatMap { Color(hex: $0) } }
    func placement(_ m: ModuleID) -> Placement { placements[m.rawValue].flatMap(Placement.init(rawValue:)) ?? .auto }

    func apply(_ t: Theme) {
        background = t.background
        gradientA = t.gradient.0
        gradientB = t.gradient.1
        accent = t.accent
        customAccent = t.customAccent
        toastStyle = t.toast
        motion = t.motion
    }

    /// Uses a `.notchsounds` file's sounds in place of the current ones. Returns why it couldn't, or nil.
    func importSounds(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url), data.count < 100_000_000,
              let pack = try? JSONDecoder().decode(SoundPackFile.self, from: data), pack.format == "notchtasks.sounds" else {
            return L("This file isn't a NotchTasks sound pack.")
        }
        let map = pack.resolved(system: Sounds.system)
        guard !map.isEmpty else { return L("No usable sounds in this file.") }
        soundMap.merge(map) { _, new in new }
        if let v = pack.volume { volume = min(max(v, 0), 1) }
        return nil
    }

    func applyPack(_ name: String) {
        guard let pack = SoundPack.all.first(where: { $0.name == name }) else { return }
        soundMap = Dictionary(uniqueKeysWithValues: pack.map.map { ($0.key.rawValue, $0.value) })
    }

    var profile: Profile {
        get {
            Profile(modules: modules.map(\.rawValue), disabled: disabled.map(\.rawValue), expandMode: expandMode.rawValue, hoverDelay: hoverDelay,
                    haptics: haptics, sounds: sounds, systemNotifications: systemNotifications, size: size.rawValue, accent: accent,
                    customAccent: customAccent, menuBarIcon: menuBarIcon, doneAfter: doneAfter, limitWarn: limitWarn, background: background,
                    gradientA: gradientA, gradientB: gradientB, toastStyle: toastStyle, motion: motion, moduleColors: moduleColors,
                    placements: placements, soundMap: soundMap, volume: volume)
        }
        set {
            let saved = newValue.modules.compactMap(ModuleID.init(rawValue:))
            modules = saved + ModuleID.allCases.filter { !saved.contains($0) }
            disabled = Set(newValue.disabled.compactMap(ModuleID.init(rawValue:)))
            expandMode = ExpandMode(rawValue: newValue.expandMode) ?? .hover
            hoverDelay = newValue.hoverDelay
            haptics = newValue.haptics
            sounds = newValue.sounds
            systemNotifications = newValue.systemNotifications
            size = NotchSize(rawValue: newValue.size) ?? .compact
            accent = newValue.accent
            customAccent = newValue.customAccent
            menuBarIcon = newValue.menuBarIcon
            doneAfter = newValue.doneAfter
            limitWarn = newValue.limitWarn
            background = newValue.background
            gradientA = newValue.gradientA
            gradientB = newValue.gradientB
            toastStyle = newValue.toastStyle
            motion = newValue.motion
            moduleColors = newValue.moduleColors
            placements = newValue.placements
            soundMap = newValue.soundMap
            volume = newValue.volume
        }
    }
    var iconSize: CGFloat { size == .compact ? 16 : 20 }

    var activeModules: [ModuleID] { modules.filter(on) }
    func on(_ m: ModuleID) -> Bool { !disabled.contains(m) && (m.requirement?.met(on: Requirement.current) ?? true) }
    func set(_ m: ModuleID, _ enabled: Bool) { if enabled && (m.requirement?.met(on: Requirement.current) ?? true) { disabled.remove(m) } else { disabled.insert(m) } }

    private init() {
        let d = UserDefaults.standard
        func load<T>(_ key: String, _ fallback: T) -> T { d.object(forKey: key) as? T ?? fallback }
        let saved = load("modules", [String]()).compactMap(ModuleID.init(rawValue:))
        modules = saved + ModuleID.allCases.filter { !saved.contains($0) }  // new modules join at the end
        disabled = Set(load("disabledModules", saved.isEmpty ? ["timer"] : [String]()).compactMap(ModuleID.init(rawValue:)))
        expandMode = ExpandMode(rawValue: load("expandMode", "hover")) ?? .hover
        hoverDelay = load("hoverDelay", 0.15)
        haptics = load("haptics", true)
        systemLoad = load("systemLoad", true)
        sounds = load("sounds", true)
        systemNotifications = load("systemNotifications", true)
        size = NotchSize(rawValue: load("size", "compact")) ?? .compact
        accent = load("accent", "auto")
        menuBarIcon = load("menuBarIcon", true)
        onboardingDone = load("onboardingDone", false)
        doneAfter = load("doneAfter", 30.0)
        limitWarn = load("limitWarn", 80.0)
        customAccent = load("customAccent", "#FF9F0A")
        background = NotchBackground(rawValue: load("background", "black")) ?? .black
        gradientA = load("gradientA", "#1E1B4B")
        gradientB = load("gradientB", "#000000")
        toastStyle = ToastStyle(rawValue: load("toastStyle", "island")) ?? .island
        motion = Motion(rawValue: load("motion", "bouncy")) ?? .bouncy
        moduleColors = load("moduleColors", [String: String]())
        placements = load("placements", [String: String]())
        var sounds = load("soundMap", Dictionary(uniqueKeysWithValues: SoundPack.all[0].map.map { ($0.key.rawValue, $0.value) }))
        // sounds picked before they were kept in the app's folder: copy them in, so deleting the original can't silence them
        let kept = SoundPackFile.folder.path
        for (event, path) in sounds where path.hasPrefix("/") && !path.hasPrefix(kept) {
            if let copy = SoundPackFile.keep(URL(fileURLWithPath: path)) { sounds[event] = copy }
        }
        soundMap = sounds
        volume = load("volume", 0.8)
        lockScreen = load("lockScreen", true)
        lockShows = Set(load("lockShows", ["lock"]).compactMap(LockContent.init(rawValue:)))
        lockAuth = load("lockAuth", true)
        func decode(_ key: String) -> [Badge] { d.data(forKey: key).flatMap { try? JSONDecoder().decode([Badge].self, from: $0) } ?? [] }
        badges = decode("badges")
        if d.data(forKey: "badges") == nil, d.bool(forKey: "badgeOn") {  // the single badge of the previous version
            badges = [Badge(symbol: d.string(forKey: "badgeSymbol") ?? "star.fill", image: d.string(forKey: "badgeImage").flatMap { $0.isEmpty ? nil : $0 },
                            shape: BadgeShape(rawValue: d.string(forKey: "badgeShape") ?? "") ?? .circle,
                            border: d.bool(forKey: "badgeBorder"), right: d.bool(forKey: "badgeRight"))]
        }
        lockOwnBadges = load("lockOwnBadges", false)
        lockBadges = decode("lockBadges")
        toolIcons = d.data(forKey: "toolIcons").flatMap { try? JSONDecoder().decode([String: Badge].self, from: $0) } ?? [:]
        seenTools = load("seenTools", [String]())
        lockIconRight = load("lockIconRight", false)
        lockBatteryLeft = load("lockBatteryLeft", false)
    }

    private func json(_ value: some Encodable) -> Data { (try? JSONEncoder().encode(value)) ?? Data() }
    private func store(_ value: Any, _ key: String) { UserDefaults.standard.set(value, forKey: key) }
}
