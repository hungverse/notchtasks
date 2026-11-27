import Foundation

/// Usage analytics, sent to Google Analytics (the notchtasks Firebase project's web stream) by its Measurement
/// Protocol: plain HTTPS, so no Firebase SDK and none of the keychain entitlements it needs on the Mac (see Sync.swift).
/// Names lowercase_snake ≤ 40 chars; params are enums or counts, never free text (titles, paths, names).
@MainActor enum Track {
    static let optOutKey = "analytics.off"
    /// From Google Analytics › Admin › Data streams › the web stream (measurement ID) › Measurement Protocol API secrets.
    /// Empty: nothing is sent.
    static let measurementID = "G-H9RD7VRTDS"
    /// Not in the repo: build.sh and scripts/release.sh pass it as NT_GA_SECRET into Info.plist. Unset ($( left
    /// unexpanded) or empty: nothing is sent.
    private static let apiSecret: String = {
        let s = (Bundle.main.object(forInfoDictionaryKey: "NTAnalyticsSecret") as? String ?? "").trimmingCharacters(in: .whitespaces)
        return s.hasPrefix("$(") ? "" : s
    }()
    private static let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""

    static var enabled: Bool {
        get { !UserDefaults.standard.bool(forKey: optOutKey) }
        set { UserDefaults.standard.set(!newValue, forKey: optOutKey); if !newValue { queue.removeAll() } }
    }

    /// One random id per install, stored only here: it ties events to an install, never to a person.
    private static let clientID: String = {
        if let id = UserDefaults.standard.string(forKey: "analytics.cid") { return id }
        let id = UUID().uuidString
        UserDefaults.standard.set(id, forKey: "analytics.cid")
        return id
    }()
    private static let session = Int(Date().timeIntervalSince1970)
    private static var queue: [[String: Any]] = []

    /// false when nothing was queued (off, not configured, or a self-test run).
    @discardableResult static func event(_ name: String, _ params: [String: Any] = [:]) -> Bool {
        guard enabled, !measurementID.isEmpty, !apiSecret.isEmpty, ProcessInfo.processInfo.environment["CI_SELFTEST"] == nil else { return false }
        let all = params.merging(["session_id": session, "engagement_time_msec": 1, "platform": "mac", "app_version": appVersion]) { a, _ in a }
        queue.append(["name": name, "params": all])
        // one request a minute at most (the protocol takes 25 events per request)
        if queue.count >= 25 { flush() }
        else if queue.count == 1 { DispatchQueue.main.asyncAfter(deadline: .now() + 60) { flush() } }
        return true
    }

    /// Once per install: marked sent only when the event was queued, so a disabled Mac still gets it later.
    static func once(_ name: String, _ params: [String: Any] = [:]) {
        let key = "analytics.once.\(name)"
        guard !UserDefaults.standard.bool(forKey: key), event(name, params) else { return }
        UserDefaults.standard.set(true, forKey: key)
    }

    static func screen(_ name: String) { event("screen_view", ["screen_name": name]) }

    static func flush() {
        guard !queue.isEmpty, let url = URL(string: "https://www.google-analytics.com/mp/collect?measurement_id=\(measurementID)&api_secret=\(apiSecret)") else { return }
        let batch = Array(queue.prefix(25)); queue.removeFirst(batch.count)
        var r = URLRequest(url: url); r.httpMethod = "POST"
        r.httpBody = try? JSONSerialization.data(withJSONObject: ["client_id": clientID, "events": batch])
        URLSession.shared.dataTask(with: r).resume()  // lost on failure: statistics, not data
        if !queue.isEmpty { DispatchQueue.main.asyncAfter(deadline: .now() + 60) { flush() } }
    }
}
