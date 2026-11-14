import Foundation

// MARK: - The notch's look, shared with the iPhone (docs/mobile-v2-plan.md, đợt A)
// The Mac sends it inside the sealed snapshot; the phone sends back an edited copy inside a sealed answer. Field for field
// the iPhone's MacNotch (iOS/Shared/Wire.swift). The phone's word is checked here: unknown values are ignored, numbers are
// clamped, and pictures stay the Mac's own files (a phone cannot add or swap one).

struct SyncNotch: Codable, Equatable {
    var size: String
    var expandMode: String
    var accent: String
    var customAccent: String
    var background: String
    var gradientA: String
    var gradientB: String
    var toastStyle: String
    var motion: String
    var modules: [String]          // display order
    var disabled: [String]
    var placements: [String: String]
    var haptics: Bool
    var sounds: Bool
    var volume: Double
    var systemNotifications: Bool
    var doneAfter: Double
    var limitWarn: Double
    var lockScreen: Bool
    var lockShows: [String]
    var badges: [Badge]
    var lockOwnBadges: Bool
    var lockBadges: [Badge]
    var toolIcons: [String: Badge]
    var seenTools: [String]

    @MainActor static func of(_ s: AppSettings) -> SyncNotch {
        SyncNotch(size: s.size.rawValue, expandMode: s.expandMode.rawValue, accent: s.accent, customAccent: s.customAccent,
                  background: s.background.rawValue, gradientA: s.gradientA, gradientB: s.gradientB, toastStyle: s.toastStyle.rawValue,
                  motion: s.motion.rawValue, modules: s.modules.map(\.rawValue), disabled: s.disabled.map(\.rawValue).sorted(),
                  placements: s.placements, haptics: s.haptics, sounds: s.sounds, volume: (s.volume * 100).rounded() / 100,
                  systemNotifications: s.systemNotifications, doneAfter: s.doneAfter, limitWarn: s.limitWarn, lockScreen: s.lockScreen,
                  lockShows: s.lockShows.map(\.rawValue).sorted(), badges: s.badges, lockOwnBadges: s.lockOwnBadges, lockBadges: s.lockBadges,
                  toolIcons: s.toolIcons, seenTools: Array(s.seenTools.prefix(12)))
    }

    /// The phone's edit, checked field by field; only what changed is set (each setter writes UserDefaults).
    @MainActor func apply(to s: AppSettings) {
        func set<T: Equatable>(_ path: ReferenceWritableKeyPath<AppSettings, T>, _ v: T?) { if let v, s[keyPath: path] != v { s[keyPath: path] = v } }
        set(\.size, NotchSize(rawValue: size))
        set(\.expandMode, ExpandMode(rawValue: expandMode))
        set(\.accent, AppSettings.accents.contains { $0.name == accent } ? accent : nil)
        set(\.customAccent, Self.hex(customAccent))
        set(\.background, NotchBackground(rawValue: background))
        set(\.gradientA, Self.hex(gradientA))
        set(\.gradientB, Self.hex(gradientB))
        set(\.toastStyle, ToastStyle(rawValue: toastStyle))
        set(\.motion, Motion(rawValue: motion))
        let order = modules.compactMap(ModuleID.init(rawValue:))
        if !order.isEmpty { set(\.modules, order + ModuleID.allCases.filter { !order.contains($0) }) }
        set(\.disabled, Set(disabled.compactMap(ModuleID.init(rawValue:))))
        set(\.placements, placements.filter { ModuleID(rawValue: $0.key) != nil && Placement(rawValue: $0.value) != nil })
        set(\.haptics, haptics)
        set(\.sounds, sounds)
        set(\.volume, min(max(volume, 0), 1))
        set(\.systemNotifications, systemNotifications)
        set(\.doneAfter, min(max(doneAfter, 0), 3600))
        set(\.limitWarn, min(max(limitWarn, 50), 100))
        set(\.lockScreen, lockScreen)
        set(\.lockShows, Set(lockShows.compactMap(LockContent.init(rawValue:))))
        set(\.badges, Self.clean(badges, kept: s.badges))
        set(\.lockOwnBadges, lockOwnBadges)
        set(\.lockBadges, Self.clean(lockBadges, kept: s.lockBadges))
        // a build tool's icon only for a tool this Mac has seen
        let tools = Set(s.seenTools)
        set(\.toolIcons, toolIcons.filter { tools.contains($0.key) }.mapValues { Self.clean([$0], kept: Array(s.toolIcons.values))[0] })
    }

    /// As many as the notch holds (two a side); a picture (image, spritesheet) only where this Mac already had it under the same id.
    static func clean(_ new: [Badge], kept: [Badge]) -> [Badge] {
        var left = 0, right = 0
        let fits = new.filter { b in
            if b.right { right += 1; return right <= Badge.perSide }
            left += 1; return left <= Badge.perSide
        }
        return fits.map { b in
            var b = b
            let old = kept.first { $0.id == b.id }
            b.image = old?.image
            b.frames = old?.frames
            b.fps = old?.fps
            if let sprite = b.sprite, SpriteLibrary.entry(sprite) == nil { b.sprite = nil }
            b.symbol = String(b.symbol.prefix(60))
            b.text = b.text.map { String($0.prefix(4)) }.flatMap { $0.isEmpty ? nil : $0 }
            b.color = b.color.flatMap(hex)
            b.fg = b.fg.flatMap(hex)
            return b
        }
    }

    static func hex(_ s: String) -> String? { s.range(of: #"^#[0-9A-Fa-f]{6}$"#, options: .regularExpression) != nil ? s.uppercased() : nil }

    @MainActor static func selfTest() {
        let s = AppSettings.shared
        let now = of(s)
        precondition((try? JSONDecoder().decode(SyncNotch.self, from: JSONEncoder().encode(now))) == now, "notch round trip")
        // a phone cannot point a badge at a file, nor send junk
        let kept = Badge(image: "/Users/x/a.png")
        var forged = Badge(sprite: "no-such-sprite")
        forged.image = "/etc/passwd"
        forged.color = "red"
        let out = clean([kept, forged] + Array(repeating: Badge(), count: 10), kept: [kept])
        precondition(out.count == Badge.perSide && out[0].image == "/Users/x/a.png", "badges kept with their own files, two a side")
        precondition(out[1].image == nil && out[1].sprite == nil && out[1].color == nil, "no new files, sprites or colors from the phone")
        precondition(hex("#00f5d4") == "#00F5D4" && hex("javascript:") == nil, "hex colors only")
    }
}
