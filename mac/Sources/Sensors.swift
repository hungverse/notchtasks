import AppKit
import CoreAudio
import CoreMediaIO
import AudioToolbox  // the "virtual main volume" the menu bar slider shows

// MARK: - Who is using the microphone and the camera, and whether that looks like a call

/// One sample of the privacy sensors. Read off the main thread: CoreAudio and CoreMediaIO calls can block briefly.
struct SensorState: Equatable {
    var micApps: [String] = []  // bundle IDs recording right now
    var camera = false          // CoreMediaIO can't say which app has it
    var micMuted = false        // the default input's mute, which the call card toggles
    var mic: Bool { !micApps.isEmpty }
}

enum Sensors {
    static func read() -> SensorState {
        let me = Bundle.main.bundleIdentifier
        let mic = Array(Set(AudioOut.bundles(input: true).compactMap(appID).filter { $0 != me })).sorted()
        return SensorState(micApps: mic, camera: cameraInUse(), micMuted: inputMuted() ?? false)
    }

    /// The app behind a recording process: helpers ("com.google.Chrome.helper") count as their app. Background
    /// listeners with no app ("Hey Siri" in com.apple.CoreSpeech, dictation) return nil: they listen all day.
    static func appID(_ id: String) -> String? {
        var parts = id.split(separator: ".").map(String.init)
        while parts.count >= 2 {
            let candidate = parts.joined(separator: ".")
            if callApps[candidate] != nil { return candidate }
            // a helper is an app too, but one nested inside its parent's bundle
            if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: candidate), !url.path.contains(".app/") { return candidate }
            parts.removeLast()
        }
        return nil
    }

    // MARK: Microphone mute

    static func defaultInput() -> AudioObjectID? {
        AudioOut.prop(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultInputDevice, AudioObjectID(0)).flatMap { $0 == 0 ? nil : $0 }
    }

    private static var muteAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyMute, mScope: kAudioObjectPropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
    }

    static func inputMuted() -> Bool? {
        guard let d = defaultInput() else { return nil }
        var a = muteAddress
        var m: UInt32 = 0
        var sz = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(d, &a, 0, nil, &sz, &m) == noErr ? m != 0 : nil
    }

    /// Mutes the default microphone for every app, like the call's own mute button. False if the device can't.
    @discardableResult static func setInputMuted(_ on: Bool) -> Bool {
        guard let d = defaultInput() else { return false }
        var a = muteAddress
        var settable: DarwinBoolean = false
        guard AudioObjectIsPropertySettable(d, &a, &settable) == noErr, settable.boolValue else { return false }
        var v: UInt32 = on ? 1 : 0
        return AudioObjectSetPropertyData(d, &a, 0, nil, UInt32(MemoryLayout<UInt32>.size), &v) == noErr
    }

    // MARK: Camera

    static func cameraInUse() -> Bool {
        let system = CMIOObjectID(kCMIOObjectSystemObject)
        var addr = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
                                             mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
                                             mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementMain))
        var size: UInt32 = 0
        guard CMIOObjectGetPropertyDataSize(system, &addr, 0, nil, &size) == noErr, size > 0 else { return false }
        var ids = [CMIOObjectID](repeating: 0, count: Int(size) / MemoryLayout<CMIOObjectID>.size)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(system, &addr, 0, nil, size, &used, &ids) == noErr else { return false }
        return ids.contains { dev in
            var a = CMIOObjectPropertyAddress(mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyDeviceIsRunningSomewhere),
                                              mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeWildcard),
                                              mElement: CMIOObjectPropertyElement(kCMIOObjectPropertyElementWildcard))
            var on: UInt32 = 0, got: UInt32 = 0
            return CMIOObjectGetPropertyData(dev, &a, 0, nil, UInt32(MemoryLayout<UInt32>.size), &got, &on) == noErr && on != 0
        }
    }

    // MARK: Calls

    /// Apps whose microphone use means a call. A browser tab using the mic is nearly always a meeting, so browsers count.
    static let callApps: [String: String] = [
        "com.apple.FaceTime": "FaceTime", "com.apple.TelephonyUtilities": "iPhone", "com.apple.avconferenced": "FaceTime",
        "us.zoom.xos": "Zoom", "com.microsoft.teams2": "Teams", "com.microsoft.teams": "Teams",
        "com.tinyspeck.slackmacgap": "Slack", "com.hnc.Discord": "Discord", "com.skype.skype": "Skype",
        "net.whatsapp.WhatsApp": "WhatsApp", "ru.keepcoder.Telegram": "Telegram", "com.facebook.archon": "Messenger",
        "com.vng.zalo": "Zalo", "Cisco-Systems.Spark": "Webex", "com.cisco.webexmeetingsapp": "Webex",
        "com.google.Chrome": "Chrome", "com.apple.Safari": "Safari", "company.thebrowser.Browser": "Arc",
        "com.microsoft.edgemac": "Edge", "com.brave.Browser": "Brave", "org.mozilla.firefox": "Firefox",
    ]

    /// The calling app among those using the mic: a dedicated call app wins over a browser.
    /// Helpers ("com.google.Chrome.helper") count as their app.
    static func callApp(_ micApps: [String]) -> String? {
        let known = micApps.filter { callApps[$0] != nil }
        return known.first { !NowPlaying.browsers.contains($0) && $0 != "org.mozilla.firefox" } ?? known.first
    }

    static func name(_ bundleID: String) -> String {
        if let n = callApps[bundleID] { return n }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) else { return bundleID }
        return FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: "")
    }
}

/// A call in progress: the app, when it began, and whether the camera is on (a video call).
struct CallInfo: Equatable {
    var app: String  // bundle ID
    var since: Date
    var video: Bool
    var muted: Bool
    var name: String { Sensors.name(app) }
}

// MARK: - Volume and brightness, for a level bar on the notch while they change

enum LevelKind: Equatable { case volume, brightness }

struct LevelHUD: Equatable {
    var kind: LevelKind
    var value: Double  // 0…1
    var muted = false
    var at = Date()
    var symbol: String {
        switch kind {
        case .volume: return muted || value == 0 ? "speaker.slash.fill" : value < 0.34 ? "speaker.wave.1.fill" : value < 0.67 ? "speaker.wave.2.fill" : "speaker.wave.3.fill"
        case .brightness: return value < 0.5 ? "sun.min.fill" : "sun.max.fill"
        }
    }
}

enum Levels {
    static func output() -> AudioObjectID? {
        AudioOut.prop(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice, AudioObjectID(0)).flatMap { $0 == 0 ? nil : $0 }
    }

    /// The output's volume as the menu bar slider shows it (one value for all channels), and its mute.
    static func volume(_ dev: AudioObjectID) -> (value: Double, muted: Bool)? {
        func get<T>(_ sel: AudioObjectPropertySelector, _ v: T) -> T? {
            var a = AudioObjectPropertyAddress(mSelector: sel, mScope: kAudioObjectPropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
            var x = v
            var sz = UInt32(MemoryLayout<T>.size)
            return AudioObjectGetPropertyData(dev, &a, 0, nil, &sz, &x) == noErr ? x : nil
        }
        guard let v = get(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, Float32(0)) else { return nil }
        return (Double(v), (get(kAudioDevicePropertyMute, UInt32(0)) ?? 0) != 0)
    }

    // shortcut: DisplayServices is private (no public brightness API); if it goes away the brightness bar just never shows
    private typealias GetBrightness = @convention(c) (UInt32, UnsafeMutablePointer<Float>) -> Int32
    private static let getBrightness: GetBrightness? = {
        #if APPSTORE
        return nil  // App Review rejects private frameworks
        #else
        guard let lib = dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_NOW),
              let f = dlsym(lib, "DisplayServicesGetBrightness") else { return nil }
        return unsafeBitCast(f, to: GetBrightness.self)
        #endif
    }()

    static func brightness() -> Double? {
        var v: Float = -1
        guard let f = getBrightness, f(CGMainDisplayID(), &v) == 0, v >= 0 else { return nil }
        return Double(v)
    }
}

// MARK: - Network and VPN

enum NetInfo {
    /// A VPN shows up as a tunnel interface with an IPv4 address. macOS keeps several idle utun interfaces
    /// (iCloud, Handoff) with only IPv6 link-local addresses; those don't count.
    static func vpnUp() -> Bool {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return false }
        defer { freeifaddrs(list) }
        var p: UnsafeMutablePointer<ifaddrs>? = first
        while let i = p {
            let name = String(cString: i.pointee.ifa_name)
            let up = (Int32(i.pointee.ifa_flags) & IFF_UP) != 0
            if up, ["utun", "ipsec", "ppp", "tun", "wg"].contains(where: name.hasPrefix),
               i.pointee.ifa_addr?.pointee.sa_family == sa_family_t(AF_INET) { return true }
            p = i.pointee.ifa_next
        }
        return false
    }

    /// Free space for the user's files, as Finder counts it (purgeable space included).
    static func freeDisk() -> Int64? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return (try? home.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage
    }
}

// MARK: - Finished downloads

enum Downloads {
    static let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads")
    /// Files a browser is still writing; when done they are renamed to the real name.
    static let partial: Set<String> = ["crdownload", "download", "part", "partial", "tmp", "opdownload", "aria2"]

    static func finished(in names: [String]) -> Set<String> {
        Set(names.filter { !$0.hasPrefix(".") && !partial.contains(($0 as NSString).pathExtension.lowercased()) })
    }
}
