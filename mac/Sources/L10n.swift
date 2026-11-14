import Foundation

/// The app's languages. English is the default; until you pick one, the device's own language is followed.
/// Texts are written in Vietnamese in the code and those are the keys: each language is a table "l10n-<code>.json"
/// next to the code (copied into the app, its widgets and extensions). A missing text falls back to English.
enum L10n {
    static let languages: [(code: String, name: String)] = [
        ("en", "English"), ("vi", "Tiếng Việt"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"), ("ja", "日本語"),
        ("ko", "한국어"), ("es", "Español"), ("fr", "Français"), ("de", "Deutsch"), ("pt-BR", "Português (Brasil)"),
        ("it", "Italiano"), ("ru", "Русский"), ("id", "Bahasa Indonesia"), ("th", "ไทย"), ("tr", "Türkçe"), ("hi", "हिन्दी"),
    ]
    static let changed = Notification.Name("L10n.changed")
    static let storeKey = "app.language"
    #if os(iOS)
    /// Shared with the widgets, Live Activities and the notification extension, so they speak the same language.
    static var store: UserDefaults { UserDefaults(suiteName: "group.com.notchtasks.shared") ?? .standard }
    #else
    static var store: UserDefaults { .standard }
    #endif

    /// The language picked in the app, or "" to follow the device.
    static var choice: String {
        get { store.string(forKey: storeKey) ?? "" }
        set {
            store.set(newValue, forKey: storeKey)
            NotificationCenter.default.post(name: changed, object: nil)
        }
    }
    /// Self-tests check the texts as written (the keys).
    nonisolated(unsafe) static var testing = false
    static var code: String { testing ? "vi" : choice.isEmpty ? device : choice }
    static var locale: Locale { Locale(identifier: code) }
    static var device: String { match(Locale.preferredLanguages) }

    /// The first of the device's languages the app speaks; English when none.
    static func match(_ preferred: [String]) -> String {
        for p in preferred {
            let l = Locale(identifier: p)
            guard let lang = l.language.languageCode?.identifier else { continue }
            if lang == "zh" {
                let hant = l.language.script?.identifier == "Hant" || ["TW", "HK", "MO"].contains(l.region?.identifier ?? "")
                return hant ? "zh-Hant" : "zh-Hans"
            }
            if lang == "pt" { return "pt-BR" }
            if let hit = languages.first(where: { $0.code == lang }) { return hit.code }
        }
        return "en"
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var tables: [String: [String: String]] = [:]

    static func table(_ code: String) -> [String: String] {
        lock.lock(); defer { lock.unlock() }
        if let t = tables[code] { return t }
        let t = Bundle.main.url(forResource: "l10n-\(code)", withExtension: "json")
            .flatMap { try? Data(contentsOf: $0) }
            .flatMap { try? JSONDecoder().decode([String: String].self, from: $0) } ?? [:]
        tables[code] = t
        return t
    }

    static func text(_ key: String, in code: String) -> String {
        if code == "vi" { return key }
        return table(code)[key] ?? table("en")[key] ?? key
    }

    static func selfTest() {
        assert(match(["fr-CA"]) == "fr" && match(["zh-Hant-TW"]) == "zh-Hant" && match(["zh-CN"]) == "zh-Hans", "device language")
        assert(match(["pt-PT"]) == "pt-BR" && match(["sw"]) == "en" && match(["sw", "vi-VN"]) == "vi", "fallbacks")
        assert(fill("{1} và {0}", ["a", 2]) == "2 và a", "placeholders")
    }

    static func fill(_ s: String, _ args: [Any]) -> String {
        var s = s
        for (i, a) in args.enumerated() { s = s.replacingOccurrences(of: "{\(i)}", with: "\(a)") }
        return s
    }
}

/// A text in the app's language. Placeholders {0}, {1}… take the arguments in order (a translation may reorder them).
func L(_ key: String, _ args: Any...) -> String {
    L10n.fill(L10n.text(key, in: L10n.code), args)
}

import SwiftUI

/// A window's root: drawn again in the new language the moment it changes (state inside starts over).
struct LocalizedRoot<Content: View>: View {
    @State private var lang = L10n.code
    /// Built again on each change: texts made when the root was first built (tab names…) would stay in the old language.
    @ViewBuilder var content: () -> Content
    var body: some View {
        content().id(lang).environment(\.locale, Locale(identifier: lang))
            .onReceive(NotificationCenter.default.publisher(for: L10n.changed)) { _ in lang = L10n.code }
    }
}

/// "Ngôn ngữ": follow the device, or one of the app's languages (each shown in its own words).
struct LanguagePicker: View {
    @State private var lang = L10n.choice
    var body: some View {
        Picker(L("Ngôn ngữ"), selection: Binding(get: { lang }, set: { lang = $0; L10n.choice = $0 })) {
            Text(L("Theo máy") + " · " + (L10n.languages.first { $0.code == L10n.device }?.name ?? "English")).tag("")
            Divider()
            ForEach(L10n.languages, id: \.code) { Text($0.name).tag($0.code) }
        }
    }
}
