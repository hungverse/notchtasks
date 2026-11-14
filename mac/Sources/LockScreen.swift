import AppKit
import Darwin

// MARK: - Showing the notch above the lock screen
// macOS has no public API for this. Windows placed in a SkyLight space with a high absolute level are drawn above
// the lock screen; the same private calls other notch apps use. Display only: the lock screen keeps all input.
// shortcut: private API, may stop working in a future macOS; everything else keeps working if it does (we just log).

/// Appends to ~/Library/Logs/NotchTasks/<file>: for what can't be watched from outside (lock screen, privacy prompts).
func appLog(_ file: String, _ line: String) {
    let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/NotchTasks")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let url = dir.appendingPathComponent(file)
    let text = Date().formatted(date: .omitted, time: .standard) + " " + line + "\n"
    if let h = try? FileHandle(forWritingTo: url) { h.seekToEndOfFile(); h.write(Data(text.utf8)); try? h.close() }
    else { try? text.write(to: url, atomically: true, encoding: .utf8) }
}

enum LockScreenSpace {
    /// lock.log: what happened at the last locks.
    static func log(_ line: String) { appLog("lock.log", line) }

    private typealias MainConnection = @convention(c) () -> Int32
    private typealias SpaceCreate = @convention(c) (Int32, Int32, Int32) -> Int32
    private typealias SetLevel = @convention(c) (Int32, Int32, Int32) -> Int32
    private typealias ShowSpaces = @convention(c) (Int32, CFArray) -> Int32
    private typealias AddWindows = @convention(c) (Int32, Int32, CFArray, Int32) -> Int32

    #if APPSTORE
    private static let lib: UnsafeMutableRawPointer? = nil  // App Review rejects private frameworks: no lock-screen notch there
    #else
    private static let lib = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/Versions/A/SkyLight", RTLD_NOW)
    #endif
    private static func fn<T>(_ name: String, _: T.Type) -> T? { lib.flatMap { dlsym($0, name) }.map { unsafeBitCast($0, to: T.self) } }

    /// (connection, space), created on first use and again if the window server loses it.
    private static var space: (Int32, Int32)? = makeSpace()

    private static func makeSpace() -> (Int32, Int32)? {
        guard let main = fn("SLSMainConnectionID", MainConnection.self), let create = fn("SLSSpaceCreate", SpaceCreate.self),
              let level = fn("SLSSpaceSetAbsoluteLevel", SetLevel.self), let show = fn("SLSShowSpaces", ShowSpaces.self) else {
            log("SkyLight unavailable")
            return nil
        }
        let cid = main()
        let sid = create(cid, 1, 0)
        guard sid != 0 else { log("SLSSpaceCreate failed"); return nil }
        _ = level(cid, sid, 400)  // above the lock screen and the layer it raises after a display wake
        _ = show(cid, [sid] as CFArray)
        return (cid, sid)
    }

    /// Puts the space back on top; true if it still exists.
    private static func raise(_ cid: Int32, _ sid: Int32) -> Bool {
        guard let level = fn("SLSSpaceSetAbsoluteLevel", SetLevel.self), let show = fn("SLSShowSpaces", ShowSpaces.self) else { return false }
        return level(cid, sid, 400) == 0 && show(cid, [sid] as CFArray) == 0
    }

    /// Moves the window into the lock screen space; false if this macOS doesn't allow it.
    @discardableResult static func adopt(_ window: NSWindow, quiet: Bool = false) -> Bool {
        guard let add = fn("SLSSpaceAddWindowsAndRemoveFromSpaces", AddWindows.self) else { return false }
        if let (cid, sid) = space, !raise(cid, sid) { log("space lost, recreating"); space = makeSpace() }
        guard let (cid, sid) = space else { return false }
        let err = add(cid, sid, [window.windowNumber] as CFArray, 7)
        if !quiet || err != 0 { log("adopt window \(window.windowNumber) → \(err == 0 ? "ok" : "error \(err)")") }
        return err == 0
    }
}

// MARK: - Unlock attempts
// macOS doesn't tell other apps about failed unlocks. loginwindow logs its own events ("authFailWithMessage:] | enter.
// INCORRECT password", "==== APEventTouchIDMatch ===="), and `log stream` can read them while the screen is locked.
// shortcut: log text isn't an API; if Apple rewords it the notch just stops reacting. lock.log keeps the lines for tuning.

enum AuthEvent: Equatable { case failed(touchID: Bool), success }

@MainActor final class AuthWatch {
    private var proc: Process?
    private var buffer = ""
    private var failedCount = 0
    var onEvent: ((AuthEvent) -> Void)?

    func start() {
        guard proc == nil else { return }
        failedCount = 0
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/log")
        p.arguments = ["stream", "--style", "compact", "--predicate",
                       #"process == "loginwindow" AND (eventMessage CONTAINS "SCREENLOCK ====" OR eventMessage CONTAINS "authFailWithMessage" OR eventMessage CONTAINS[c] "TouchID")"#]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        out.fileHandleForReading.readabilityHandler = { [weak self] h in
            let text = String(decoding: h.availableData, as: UTF8.self)
            DispatchQueue.main.async { MainActor.assumeIsolated { self?.feed(text) } }
        }
        do { try p.run(); proc = p } catch { LockScreenSpace.log("auth watch failed: \(error)") }
    }

    func stop() {
        proc?.terminate()
        proc = nil
        buffer = ""
    }

    private func feed(_ text: String) {
        buffer += text
        while let nl = buffer.firstIndex(of: "\n") {
            let line = String(buffer[..<nl])
            buffer.removeSubrange(...nl)
            guard line.contains("loginwindow") else { continue }
            LockScreenSpace.log("auth: " + String(line.suffix(160)))
            if let e = Self.classify(line, failedSoFar: &failedCount) { onEvent?(e) }
        }
    }

    /// One log line → what happened. `failedSoFar` tracks loginwindow's own counter, which only ever counts up while locked.
    static func classify(_ line: String, failedSoFar: inout Int) -> AuthEvent? {
        // one wrong try logs three authFailWithMessage lines; only the first says "enter." and why
        if line.contains("authFailWithMessage:]"), let r = line.range(of: "| enter.") {
            let why = line[r.upperBound...].lowercased()
            failedSoFar += 1
            return .failed(touchID: !why.contains("password") && (why.contains("touch") || why.contains("finger") || why.contains("biometric")))
        }
        if let r = line.range(of: "_numFailedAuthAttempts = ") {
            let n = Int(line[r.upperBound...].prefix { $0.isNumber }) ?? 0
            defer { failedSoFar = max(failedSoFar, n) }
            return n > failedSoFar ? .failed(touchID: false) : nil
        }
        guard let r = line.range(of: "SCREENLOCK ==== ") else { return nil }
        let event = line[r.upperBound...].prefix { !$0.isWhitespace && $0 != "=" }
        let touch = event.contains("TouchID") || event.contains("Biometric")
        if ["NoMatch", "Fail", "Mismatch", "Invalid", "Reject"].contains(where: event.contains) { return .failed(touchID: touch) }
        if event.hasSuffix("Match") || event.contains("Success") { return .success }
        return nil
    }
}
