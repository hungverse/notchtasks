import AppKit
import SwiftUI
import IOKit

// MARK: - Connected devices: Bluetooth (AirPods, keyboards, mice…) and USB, no Bluetooth permission needed

struct Device: Identifiable, Equatable {
    let id: String
    let name: String
    let symbol: String
    var battery: String?  // "L 100% · R 100% · Hộp 59%" / "80%"
    var lowest: Int?      // weakest battery, for the low-battery warning
    var art: String?      // DeviceArt key: the product's own picture, for Apple and Beats devices
}

/// Product pictures macOS already ships: the AirPods/Beats banners of its own connect pop-up, and the Magic accessory icons.
// shortcut: private resource names; when Apple renames them the notch falls back to SF Symbols
enum DeviceArt {
    static let bluetoothUI = Bundle(path: "/System/Library/CoreServices/BluetoothUIService.app")
    static let accessories = "/System/Library/Templates/Data/Library/Application Support/Apple/BezelServices/AppleTopCaseHIDEventDriver.plugin/Contents/Resources/"
    static let coreTypes = "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/"
    @MainActor private static var cache: [String: NSImage] = [:]

    /// Which picture fits: the exact AirPods/Beats model, else the closest look by name, else Apple accessories by kind.
    static func key(name: String, vendor: Int?, product: Int?, type: String?) -> String? {
        let n = name.lowercased(), t = type?.lowercased() ?? ""
        if let product, vendor == 0x004C || vendor == 0x0076 {  // Apple, and Beats under Apple's id
            for candidate in ["Banner-PID-\(product)-default", "Banner-PID-\(product)"] where bluetoothUI?.image(forResource: candidate) != nil {
                return "banner:" + candidate
            }
        }
        if n.contains("airpods max") { return "banner:Banner-PID-8202-default" }
        if n.contains("airpods pro") { return "banner:Banner-PID-8212" }
        if n.contains("airpod") { return "banner:Banner-PID-8211" }  // AirPods 3/4 share this look
        guard vendor == 0x004C || n.contains("magic") else { return nil }
        if t == "keyboard" { return "file:" + accessories + (n.contains("numeric") ? "ExtendedKeyboard2.icns" : "Keyboard2.icns") }
        if t == "mouse" { return "file:" + accessories + "Mouse.icns" }
        if t == "trackpad" { return "file:" + accessories + "MagicTrackpad.icns" }
        if t == "phone" || t == "smartphone" || n.contains("iphone") { return "file:" + coreTypes + "com.apple.iphone.icns" }
        if n.contains("ipad") { return "file:" + coreTypes + "com.apple.ipad.icns" }
        return nil
    }

    @MainActor static func image(_ key: String?) -> NSImage? {
        guard let key else { return nil }
        if let i = cache[key] { return i }
        let img = key.hasPrefix("banner:") ? bluetoothUI?.image(forResource: String(key.dropFirst(7)))
                                           : NSImage(contentsOfFile: String(key.dropFirst(5)))
        cache[key] = img
        return img
    }
}

enum Devices {
    /// `system_profiler SPBluetoothDataType -json` lists connected devices with battery levels (~0.1 s).
    static func bluetooth() -> [Device] {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/system_profiler")
        p.arguments = ["SPBluetoothDataType", "-json"]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return [] }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return parseBluetooth(data)
    }

    static func parseBluetooth(_ data: Data) -> [Device] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let controllers = root["SPBluetoothDataType"] as? [[String: Any]] else { return [] }
        var out: [Device] = []
        for c in controllers {
            for entry in c["device_connected"] as? [[String: Any]] ?? [] {
                for (name, value) in entry {
                    let info = value as? [String: Any] ?? [:]
                    let levels: [(String, String)] = [("device_batteryLevelLeft", "L"), ("device_batteryLevelRight", "R"),
                                                      ("device_batteryLevelCase", L("Case")), ("device_batteryLevelMain", "")]
                    var parts: [String] = []
                    var lowest: Int?
                    for (key, label) in levels {
                        guard let v = info[key] as? String, let n = Int(v.replacingOccurrences(of: "%", with: "")) else { continue }
                        parts.append(label.isEmpty ? "\(n)%" : "\(label) \(n)%")
                        if label != L("Case") { lowest = min(lowest ?? n, n) }  // the case running low isn't urgent
                    }
                    func hex(_ k: String) -> Int? { (info[k] as? String).flatMap { Int($0.replacingOccurrences(of: "0x", with: ""), radix: 16) } }
                    let type = info["device_minorType"] as? String
                    // while a device connects macOS can list it twice: one entry per Bluetooth address
                    let id = "bt:" + ((info["device_address"] as? String) ?? name)
                    guard !out.contains(where: { $0.id == id }) else { continue }
                    out.append(Device(id: id, name: name, symbol: symbol(name, type),
                                      battery: parts.isEmpty ? nil : parts.joined(separator: " · "), lowest: lowest,
                                      art: DeviceArt.key(name: name, vendor: hex("device_vendorID"), product: hex("device_productID"), type: type)))
                }
            }
        }
        return out
    }

    static func symbol(_ name: String, _ type: String?) -> String {
        let n = name.lowercased()
        if n.contains("airpods max") { return "airpodsmax" }
        if n.contains("airpods pro") { return "airpodspro" }
        if n.contains("airpod") { return "airpods" }
        switch type?.lowercased() ?? "" {
        case "headphones", "headset": return "headphones"
        case "keyboard": return "keyboard"
        case "mouse": return "computermouse"
        case "trackpad": return "rectangle.and.hand.point.up.left"
        case "speaker", "loudspeaker": return "hifispeaker"
        case "gamepad", "joystick": return "gamecontroller"
        case "phone", "smartphone": return "iphone"
        default: return n.contains("watch") ? "applewatch" : "dot.radiowaves.left.and.right"
        }
    }

    /// USB devices from the IORegistry (hubs skipped).
    static func usb() -> [Device] {
        var it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOUSBHostDevice"), &it) == KERN_SUCCESS else { return [] }
        defer { IOObjectRelease(it) }
        var out: [Device] = []
        while case let service = IOIteratorNext(it), service != 0 {
            defer { IOObjectRelease(service) }
            func prop(_ key: String) -> Any? { IORegistryEntryCreateCFProperty(service, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() }
            guard let name = prop("USB Product Name") as? String, !name.isEmpty, !name.lowercased().contains("hub") else { continue }
            let location = prop("locationID") as? Int ?? 0
            let lower = name.lowercased()
            let symbol = lower.contains("mouse") ? "computermouse" : lower.contains("keyboard") ? "keyboard"
                : lower.contains("audio") || lower.contains("headset") ? "headphones" : "cable.connector"
            out.append(Device(id: "usb:\(location):\(name)", name: name, symbol: symbol))
        }
        return out
    }
}

/// One slim row of chips: icon + weakest battery; full name and per-bud levels on hover.
struct DevicesCard: View {
    let devices: [Device]
    var body: some View {
        let tint = AppSettings.shared.color(for: .devices) ?? .blue
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(devices) { d in
                    HStack(spacing: 4) {
                        if let img = DeviceArt.image(d.art) {
                            Image(nsImage: img).resizable().interpolation(.high).aspectRatio(contentMode: .fit).frame(width: 16, height: 16)
                        } else {
                            Image(systemName: d.symbol).font(.system(size: 10, weight: .semibold)).foregroundStyle(tint)
                        }
                        if let n = d.lowest {
                            Text("\(n)%").foregroundStyle(n <= 20 ? .red : .white.opacity(0.85))
                        } else {
                            Text(d.name).foregroundStyle(.white.opacity(0.85)).lineLimit(1).frame(maxWidth: 70)
                        }
                    }
                    .font(.system(size: 10, weight: .medium, design: .rounded)).monospacedDigit()
                    .padding(.horizontal, 7).padding(.vertical, 3)
                    .background(Capsule().fill(.white.opacity(0.07)))
                    .help(d.name + (d.battery.map { " · " + $0 } ?? ""))
                }
            }
        }
    }
}
