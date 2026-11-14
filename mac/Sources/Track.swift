import Foundation

/// Usage analytics, sent to Google Analytics (the notchtasks Firebase project's web stream) by its Measurement
/// Protocol: plain HTTPS, so no Firebase SDK and none of the keychain entitlements it needs on the Mac (see Sync.swift).
/// Names lowercase_snake ≤ 40 chars; params are enums or counts, never free text (titles, paths, names).
@MainActor enum Track {
    static let optOutKey = "analytics.off"
    /// From Google Analytics › Admin › Data streams › the web stream (measurement ID) › Measurement Protocol API secrets.
    /// Empty: nothing is sent.
    static let measurementID = ""
    static let apiSecret = ""

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

    static func event(_ name: String, _ params: [String: Any] = [:]) {
        guard enabled, !measurementID.isEmpty, ProcessInfo.processInfo.environment["CI_SELFTEST"] == nil else { return }
        queue.append(["name": name, "params": params.merging(["session_id": session, "engagement_time_msec": 1]) { a, _ in a }])
        // one request a minute at most (the protocol takes 25 events per request)
        if queue.count >= 25 { flush() }
        else if queue.count == 1 { DispatchQueue.main.asyncAfter(deadline: .now() + 60) { flush() } }
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
