import AppKit
import CryptoKit
import Foundation
import Security

struct UpdateManifest: Codable, Equatable {
    let version: String
    let build: Int
    let url: URL
    let sha256: String
    let minOS: String
    let notes: [String: String]

    enum CodingKeys: String, CodingKey { case version, build, url, sha256, minOS, notes }

    init(version: String, build: Int, url: URL, sha256: String, minOS: String, notes: [String: String]) throws {
        guard !version.isEmpty, build > 0, Updater.isAllowedURL(url),
              sha256.range(of: "^[0-9a-fA-F]{64}$", options: .regularExpression) != nil,
              OSVersion(minOS) != nil else { throw UpdateFailure.invalidManifest }
        self.version = version; self.build = build; self.url = url; self.sha256 = sha256.lowercased(); self.minOS = minOS; self.notes = notes
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(version: c.decode(String.self, forKey: .version), build: c.decode(Int.self, forKey: .build),
                      url: c.decode(URL.self, forKey: .url), sha256: c.decode(String.self, forKey: .sha256),
                      minOS: c.decode(String.self, forKey: .minOS), notes: c.decode([String: String].self, forKey: .notes))
    }

    func note(for language: String) -> String { notes[language] ?? notes["en"] ?? L("Không có ghi chú.") }
}

struct OSVersion: Comparable, Equatable {
    let values: [Int]

    init?(_ text: String) {
        let values = text.split(separator: ".").compactMap { Int($0) }
        guard !values.isEmpty, values.count <= 3, values.count == text.split(separator: ".").count else { return nil }
        self.values = values
    }

    init(_ version: OperatingSystemVersion) { values = [version.majorVersion, version.minorVersion, version.patchVersion] }

    static func < (lhs: OSVersion, rhs: OSVersion) -> Bool {
        for i in 0..<3 {
            let a = i < lhs.values.count ? lhs.values[i] : 0, b = i < rhs.values.count ? rhs.values[i] : 0
            if a != b { return a < b }
        }
        return false
    }
}

enum UpdateFailure: LocalizedError {
    case invalidManifest, download, hash, mount, image, signature, oldBuild, unwritable(URL), pending

    var errorDescription: String? {
        switch self {
        case .invalidManifest: return L("Thông tin bản cập nhật không hợp lệ.")
        case .download: return L("Không tải được bản cập nhật.")
        case .hash: return L("Tệp tải xuống không khớp mã kiểm tra.")
        case .mount: return L("Không thể mở ảnh đĩa của bản cập nhật.")
        case .image: return L("Không tìm thấy ứng dụng NotchTasks trong ảnh đĩa.")
        case .signature: return L("Chữ ký của bản cập nhật không khớp ứng dụng này.")
        case .oldBuild: return L("Bản cập nhật không mới hơn bản đang chạy.")
        case .unwritable(let folder): return L("Không thể ghi vào {0}. Hãy kéo ứng dụng từ ảnh đĩa vào thư mục Ứng dụng.", folder.path)
        case .pending: return L("Còn một bản cập nhật chưa hoàn tất. Hãy thử lại sau.")
        }
    }
}

@MainActor final class Updater: ObservableObject {
    enum FooterState: Equatable { case current, checking, upToDate, offered, failed }
    static let defaultFeed = URL(string: "https://notchtasks.web.app/version.json")!
    @Published private(set) var offered: UpdateManifest? { didSet { pendingChanged?(offered != nil) } }
    @Published private(set) var lastCheck: Date?
    @Published private(set) var progress: Double?
    @Published private(set) var installing = false
    @Published private(set) var checking = false
    @Published private(set) var error: String?
    @Published private(set) var checkedManually = false
    private var timer: Timer?
    private var announce: ((UpdateManifest) -> Void)?
    var pendingChanged: ((Bool) -> Void)?
    private var lastAttempt = Date.distantPast

    var currentBuild: Int { Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") ?? 0 }
    var shortVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?" }
    var footerState: FooterState {
        Self.footerState(checking: checking, installing: installing, offered: offered != nil,
                         error: error != nil, checkedManually: checkedManually)
    }
    nonisolated static func footerState(checking: Bool, installing: Bool, offered: Bool, error: Bool, checkedManually: Bool) -> FooterState {
        if installing { return .offered }
        if error && offered { return .failed }
        if offered { return .offered }
        if checking { return .checking }
        return checkedManually ? .upToDate : .current
    }
    var feed: URL {
        UserDefaults.standard.string(forKey: "updateFeed").flatMap(URL.init(string:)).flatMap { Self.isAllowedURL($0) ? $0 : nil } ?? Self.defaultFeed
    }

    func start(announce: @escaping (UpdateManifest) -> Void) {
        self.announce = announce
        Task { try? await Task.sleep(for: .seconds(30)); check() }
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 60 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.check() }
        }
    }

    func check(manual: Bool = false) {
        guard !installing, !checking else { return }
        lastAttempt = Date()
        checkedManually = false
        error = nil
        checking = true
        Task { [weak self] in
            guard let self else { return }
            do {
                _ = try await checkForUpdate()
                if manual && self.offered == nil { self.checkedManually = true }
            } catch {
                if manual { self.error = UpdateFailure.download.localizedDescription }
            }
            self.checking = false
        }
    }

    func checkIfDue() {
        if Date().timeIntervalSince(lastAttempt) >= 60 { check() }
    }

    func checkForUpdate() async throws -> UpdateManifest? {
        let manifest = try await Self.fetch(from: feed)
        lastCheck = Date()
        offered = Self.isNewer(manifest.build, than: currentBuild) && Self.currentOS >= OSVersion(manifest.minOS)! ? manifest : nil
        if let offered, Self.shouldAnnounce(build: offered.build, shown: UserDefaults.standard.integer(forKey: "updateAnnouncedBuild")) {
            UserDefaults.standard.set(offered.build, forKey: "updateAnnouncedBuild")
            announce?(offered)
        }
        return offered
    }

    nonisolated static func shouldAnnounce(build: Int, shown: Int) -> Bool { build != shown }

    func install() {
        guard let offered, !installing else { return }
        Track.event("update_install")
        Task { [weak self] in
            guard let self else { return }
            do {
                try await install(manifest: offered)
                NSApp.terminate(nil)
            } catch let failure as UpdateFailure {
                self.progress = nil; self.installing = false; self.error = failure.localizedDescription
            } catch {
                self.progress = nil; self.installing = false; self.error = UpdateFailure.download.localizedDescription
            }
        }
    }

    func install(manifest: UpdateManifest, report: ((String) -> Void)? = nil) async throws {
        guard !installing else { throw UpdateFailure.pending }
        installing = true; progress = 0; error = nil
        Self.removeNewBundle()
        let image = try await Self.download(manifest.url) { [weak self] value in
            Task { @MainActor in self?.progress = value }
        }
        defer { try? FileManager.default.removeItem(at: image) }
        report?("downloaded")
        self.progress = nil
        let staged = try Self.stage(image, manifest: manifest, currentBuild: self.currentBuild, report: report)
        report?("staged")
        try Self.launchSwap(staged: staged)
        report?("swap started")
    }

    private static var currentOS: OSVersion { OSVersion(ProcessInfo.processInfo.operatingSystemVersion) }

    nonisolated static func isNewer(_ build: Int, than current: Int) -> Bool { build > current }

    nonisolated static func isAllowedURL(_ url: URL) -> Bool {
        url.scheme == "https" || url.scheme == "http" && url.host == "127.0.0.1"
    }

    private static func fetch(from url: URL) async throws -> UpdateManifest {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 10)
        request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse).map({ 200..<300 ~= $0.statusCode }) == true else { throw UpdateFailure.download }
        return try JSONDecoder().decode(UpdateManifest.self, from: data)
    }

    private static func download(_ url: URL, progress: @escaping (Double) -> Void) async throws -> URL {
        try await UpdateDownload(progress: progress).run(url)
    }

    nonisolated static func hash(_ url: URL) throws -> String {
        let input = try FileHandle(forReadingFrom: url)
        defer { try? input.close() }
        var digest = SHA256()
        while let data = try input.read(upToCount: 1 << 20), !data.isEmpty { digest.update(data: data) }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
    }

    nonisolated static func hashMatches(_ url: URL, _ expected: String) -> Bool { (try? hash(url))?.caseInsensitiveCompare(expected) == .orderedSame }

    private static func stage(_ image: URL, manifest: UpdateManifest, currentBuild: Int, report: ((String) -> Void)? = nil) throws -> URL {
        guard hashMatches(image, manifest.sha256) else { throw UpdateFailure.hash }
        report?("hash ok")
        let mount = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: mount) }
        do { try run("/usr/bin/hdiutil", ["attach", image.path, "-readonly", "-nobrowse", "-noverify", "-mountpoint", mount.path]) }
        catch { throw UpdateFailure.mount }
        defer { _ = try? run("/usr/bin/hdiutil", ["detach", mount.path, "-quiet"]) }
        let app = mount.appendingPathComponent("NotchTasks.app")
        guard (try? app.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else { throw UpdateFailure.image }
        guard try signedLikeRunning(app) else { throw UpdateFailure.signature }
        guard let info = Bundle(url: app)?.infoDictionary,
              let build = Int(info["CFBundleVersion"] as? String ?? ""), build > currentBuild else { throw UpdateFailure.oldBuild }
        let folder = Bundle.main.bundleURL.deletingLastPathComponent()
        guard FileManager.default.isWritableFile(atPath: folder.path) else { throw UpdateFailure.unwritable(folder) }
        let staged = folder.appendingPathComponent("NotchTasks.app.new")
        do {
            try FileManager.default.copyItem(at: app, to: staged)
            guard try signedLikeRunning(staged) else { throw UpdateFailure.signature }
            guard let info = Bundle(url: staged)?.infoDictionary,
                  let build = Int(info["CFBundleVersion"] as? String ?? ""), build > currentBuild else { throw UpdateFailure.oldBuild }
            report?("signature ok")
            return staged
        } catch {
            try? FileManager.default.removeItem(at: staged)
            throw error
        }
    }

    private static func signedLikeRunning(_ candidate: URL) throws -> Bool {
        var running: SecStaticCode?, update: SecStaticCode?
        guard SecStaticCodeCreateWithPath(Bundle.main.bundleURL as CFURL, [], &running) == errSecSuccess,
              SecStaticCodeCreateWithPath(candidate as CFURL, [], &update) == errSecSuccess,
              let running, let update else { return false }
        var information: CFDictionary?
        guard SecCodeCopySigningInformation(running, SecCSFlags(rawValue: kSecCSSigningInformation), &information) == errSecSuccess,
              let info = information as? [String: Any], let team = info[kSecCodeInfoTeamIdentifier as String] as? String,
              let identifier = info[kSecCodeInfoIdentifier as String] as? String else { return false }
        var requirement: SecRequirement?
        let text = "anchor apple generic and certificate leaf[subject.OU] = \"\(team)\" and identifier \"\(identifier)\""
        guard SecRequirementCreateWithString(text as CFString, [], &requirement) == errSecSuccess, let requirement else { return false }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSCheckNestedCode | kSecCSStrictValidate)
        return SecStaticCodeCheckValidity(update, flags, requirement) == errSecSuccess
    }

    static func removeLeftoversAtLaunch() {
        removeNewBundle()
        try? FileManager.default.removeItem(at: Bundle.main.bundleURL.deletingLastPathComponent().appendingPathComponent("NotchTasks.app.old"))
    }

    private static func removeNewBundle() {
        try? FileManager.default.removeItem(at: Bundle.main.bundleURL.deletingLastPathComponent().appendingPathComponent("NotchTasks.app.new"))
    }

    private static func launchSwap(staged: URL) throws {
        let folder = Bundle.main.bundleURL.deletingLastPathComponent()
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-swap-\(UUID().uuidString).sh")
        let body = """
        #!/bin/sh
        set -u
        trap 'rm -f "$0"' EXIT
        old="${1:-}" new="${2:-}" backup="${3:-}" pid="${4:-}"
        [ -n "$old" ] && [ -n "$new" ] && [ -n "$backup" ] && [ -n "$pid" ] || exit 1
        case "$old" in */NotchTasks.app) ;; *) exit 1;; esac
        case "$new" in */NotchTasks.app.new) ;; *) exit 1;; esac
        case "$backup" in */NotchTasks.app.old) ;; *) exit 1;; esac
        while kill -0 "$pid" 2>/dev/null; do sleep 1; done
        mv "$old" "$backup" && mv "$new" "$old" || { [ -d "$backup" ] && mv "$backup" "$old"; exit 1; }
        open -n "$old"
        for i in $(seq 1 20); do
          pgrep -f "$old/Contents/MacOS/" >/dev/null && rm -rf "$backup" && exit 0
          sleep 1
        done
        rm -rf "$old"; mv "$backup" "$old"; open -n "$old"
        """
        try body.write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = [script.path, Bundle.main.bundleURL.path, staged.path, folder.appendingPathComponent("NotchTasks.app.old").path, "\(getpid())"]
        task.standardInput = FileHandle.nullDevice
        task.standardOutput = FileHandle.nullDevice
        task.standardError = FileHandle.nullDevice
        try task.run()
    }

    private static func run(_ executable: String, _ arguments: [String]) throws {
        let task = Process(), error = Pipe()
        task.executableURL = URL(fileURLWithPath: executable); task.arguments = arguments; task.standardError = error
        try task.run(); task.waitUntilExit()
        guard task.terminationStatus == 0 else { throw UpdateFailure.image }
    }

    nonisolated static func selfTest() {
        precondition(footerState(checking: false, installing: false, offered: false, error: false, checkedManually: false) == .current
                     && footerState(checking: true, installing: false, offered: false, error: false, checkedManually: false) == .checking
                     && footerState(checking: false, installing: false, offered: false, error: false, checkedManually: true) == .upToDate
                     && footerState(checking: false, installing: false, offered: true, error: false, checkedManually: false) == .offered
                     && footerState(checking: false, installing: false, offered: true, error: true, checkedManually: false) == .failed,
                     "update footer states")
        precondition(shouldAnnounce(build: 3, shown: 2) && !shouldAnnounce(build: 3, shown: 3), "update notice once per build")
        precondition(isNewer(2, than: 1) && !isNewer(1, than: 1), "newer update build")
        precondition(isAllowedURL(URL(string: "https://example.com/version.json")!) && isAllowedURL(URL(string: "http://127.0.0.1/version.json")!) && !isAllowedURL(URL(string: "http://example.com/version.json")!) && !isAllowedURL(URL(fileURLWithPath: "/tmp/version.json")) && !isAllowedURL(URL(string: "garbage")!), "allowed update URL")
        precondition(OSVersion("26.0")! <= OSVersion("26.0.1")! && OSVersion("27")! > OSVersion("26.9")!, "minimum macOS version")
        let good = #"{"version":"2.0","build":2,"url":"http://127.0.0.1/NotchTasks.dmg","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","minOS":"26.0","notes":{"en":"English","vi":"Tiếng Việt"}}"#
        precondition((try? JSONDecoder().decode(UpdateManifest.self, from: Data(good.utf8)))?.build == 2, "valid update manifest")
        for bad in [#"{"version":"2.0","build":2}"#, #"{"version":"2.0","build":"2","url":"http://127.0.0.1/a","sha256":"a","minOS":"26.0","notes":{}}"#] {
            precondition((try? JSONDecoder().decode(UpdateManifest.self, from: Data(bad.utf8))) == nil, "invalid update manifest")
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-update-selftest-\(UUID().uuidString)")
        try! Data("signed image".utf8).write(to: file)
        let original = try! hash(file)
        precondition(hashMatches(file, original), "update hash matches")
        try! Data("changed image".utf8).write(to: file)
        precondition(!hashMatches(file, original), "changed image hash refused")
        try? FileManager.default.removeItem(at: file)
        let manifest = try! JSONDecoder().decode(UpdateManifest.self, from: Data(good.utf8))
        precondition(manifest.note(for: "vi") == "Tiếng Việt" && manifest.note(for: "fr") == "English", "update notes language fallback")
    }
}

private final class UpdateDownload: NSObject, URLSessionDownloadDelegate {
    private let progress: (Double) -> Void
    private var continuation: CheckedContinuation<URL, Error>?
    private var session: URLSession?

    init(progress: @escaping (Double) -> Void) { self.progress = progress }

    func run(_ url: URL) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let config = URLSessionConfiguration.ephemeral
            config.requestCachePolicy = .reloadIgnoringLocalCacheData
            config.timeoutIntervalForRequest = 30
            config.timeoutIntervalForResource = 15 * 60
            session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
            var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
            request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
            session?.downloadTask(with: request).resume()
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        progress(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard (downloadTask.response as? HTTPURLResponse).map({ 200..<300 ~= $0.statusCode }) == true else { return finish(.failure(UpdateFailure.download)) }
        do {
            let image = FileManager.default.temporaryDirectory.appendingPathComponent("notchtasks-update-\(UUID().uuidString).dmg")
            try FileManager.default.copyItem(at: location, to: image)
            finish(.success(image))
        } catch { finish(.failure(error)) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(.failure(error)) }
    }

    private func finish(_ result: Result<URL, Error>) {
        guard let continuation else { return }
        self.continuation = nil
        session?.finishTasksAndInvalidate()
        continuation.resume(with: result)
    }
}
