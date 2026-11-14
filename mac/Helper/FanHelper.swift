import Foundation
import IOKit
import IOKit.hidsystem
import IOKit.pwr_mgt
import Security
import Darwin

@objc private protocol FanHelperProtocol {
    func fans(_ reply: @escaping (Data) -> Void)
    func setFan(_ index: Int, rpm: Int, reply: @escaping (String?) -> Void)
    func setAuto(_ index: Int, reply: @escaping (String?) -> Void)
    func heartbeat(_ reply: @escaping (String?) -> Void)
}

private final class Helper: NSObject, NSXPCListenerDelegate, FanHelperProtocol {
    private var clients = Set<ObjectIdentifier>()
    private var forced = Set<Int>()
    private var lastHeartbeat: Date?
    private var noClientSince = Date()
    private var heatTripped = false
    private var sensorsUnavailable = false
    private var powerPort: IONotificationPortRef?
    private var powerNotifier: io_object_t = 0
    private var powerConnection: io_connect_t = 0
    private var timer: Timer?

    override init() {
        super.init()
        forced = Set(Fans.read().filter(\.forced).map(\.index))
        automaticAll()
        var port: IONotificationPortRef?
        powerConnection = IORegisterForSystemPower(Unmanaged.passUnretained(self).toOpaque(), &port, { context, connection, message, argument in
            guard let context else { return }
            let helper = Unmanaged<Helper>.fromOpaque(context).takeUnretainedValue()
            if message == 0xe0000280 { helper.automaticAll() }
            if message == 0xe0000270 || message == 0xe0000280 {
                IOAllowPowerChange(helper.powerConnection, Int(bitPattern: argument))
            }
        }, &powerNotifier)
        powerPort = port
        guard powerConnection != 0, let port,
              let source = IONotificationPortGetRunLoopSource(port)?.takeUnretainedValue() else {
            fatalError("Could not observe system sleep")
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .defaultMode)
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.watchdog() }
    }

    deinit {
        timer?.invalidate()
        if powerConnection != 0 {
            IODeregisterForSystemPower(&powerNotifier)
            if let powerPort { IONotificationPortDestroy(powerPort) }
            IOServiceClose(powerConnection)
        }
    }

    func listener(_ listener: NSXPCListener, shouldAcceptNewConnection connection: NSXPCConnection) -> Bool {
        connection.exportedInterface = NSXPCInterface(with: FanHelperProtocol.self)
        connection.exportedObject = self
        let id = ObjectIdentifier(connection)
        clients.insert(id)
        connection.invalidationHandler = { [weak self] in DispatchQueue.main.async {
            guard let self else { return }
            self.clients.remove(id)
            self.noClientSince = Date()
            self.lastHeartbeat = nil
            self.automaticAll()
        } }
        connection.resume()
        return true
    }

    func fans(_ reply: @escaping (Data) -> Void) { reply((try? JSONEncoder().encode(Fans.read())) ?? Data()) }
    func heartbeat(_ reply: @escaping (String?) -> Void) { lastHeartbeat = Date(); reply(nil) }

    func setFan(_ index: Int, rpm: Int, reply: @escaping (String?) -> Void) {
        guard let lastHeartbeat, Date().timeIntervalSince(lastHeartbeat) <= 30 else { reply("Fan heartbeat expired"); return }
        updateHeatGuard()
        guard !sensorsUnavailable else { reply("Temperature sensors unavailable"); return }
        guard !heatTripped else { reply("Fan control paused until every sensor is below 85°C"); return }
        guard let fan = Fans.read().first(where: { $0.index == index }) else { reply("Fan unavailable"); return }
        let target = FanCurve.clamp(rpm, minimum: fan.minimum, maximum: fan.maximum)
        guard SMC.unlockIfPresent(), SMC.writeMode(index, forced: true) else { reply("SMC refused fan control"); return }
        forced.insert(index)
        guard SMC.writeRPM(index, rpm: target) else {
            automatic(index)
            reply("SMC refused fan target")
            return
        }
        reply(nil)
    }

    func setAuto(_ index: Int, reply: @escaping (String?) -> Void) {
        guard Fans.read().contains(where: { $0.index == index }) else { reply("Fan unavailable"); return }
        reply(automatic(index) ? nil : "SMC refused automatic mode")
    }

    @discardableResult private func automatic(_ index: Int) -> Bool {
        guard forced.contains(index) else { return true }
        guard SMC.writeMode(index, forced: false) else { return false }
        forced.remove(index)
        return true
    }
    private func automaticAll() { for index in forced { _ = automatic(index) } }

    private func updateHeatGuard() {
        // Every plausible sensor is checked, including die/SoC sensors; the hottest one wins.
        guard let hottest = ThermalGuard.hottest() else { sensorsUnavailable = true; heatTripped = true; automaticAll(); return }
        sensorsUnavailable = false
        heatTripped = FanThermalGuard.tripped(heatTripped, hottest: hottest)
        if heatTripped { automaticAll() }
    }

    private func watchdog() {
        updateHeatGuard()
        if FanWatchdog.shouldAuto(lastHeartbeat: lastHeartbeat, now: Date(), hottestCelsius: heatTripped ? 95 : nil) { automaticAll() }
        if clients.isEmpty && forced.isEmpty && Date().timeIntervalSince(noClientSince) >= 60 { exit(0) }
    }

    func shutdown() { automaticAll(); exit(0) }
}

private enum ThermalGuard {
    private static let temperatureEvent = Int64(15), temperatureField = temperatureEvent << 16
    @_silgen_name("IOHIDEventSystemClientSetMatching") private static func setMatching(_ client: IOHIDEventSystemClient, _ matching: CFDictionary)
    @_silgen_name("IOHIDEventSystemClientCreate") private static func createClient(_ allocator: CFAllocator?) -> IOHIDEventSystemClient
    @_silgen_name("IOHIDServiceClientCopyEvent") private static func copyEvent(_ service: IOHIDServiceClient, _ type: Int64, _ options: Int32, _ timestamp: Int64) -> CFTypeRef?
    @_silgen_name("IOHIDEventGetFloatValue") private static func floatValue(_ event: CFTypeRef, _ field: Int64) -> Double
    private static let client: IOHIDEventSystemClient = { let client = createClient(kCFAllocatorDefault); setMatching(client, ["PrimaryUsagePage": 0xff00, "PrimaryUsage": 5] as CFDictionary); return client }()
    static func hottest() -> Double? {
        (IOHIDEventSystemClientCopyServices(client) as NSArray? ?? []).compactMap { service -> Double? in
            guard let event = copyEvent(service as! IOHIDServiceClient, temperatureEvent, 0, 0) else { return nil }
            let value = floatValue(event, temperatureField); return (5...125).contains(value) ? value : nil
        }.max()
    }
}

private func appRequirement() -> String? {
    var code: SecCode?, staticCode: SecStaticCode?, info: CFDictionary?
    guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
          SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
          SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
          let team = (info as? [CFString: Any])?[kSecCodeInfoTeamIdentifier] as? String else { return nil }
    return "identifier \"com.notchtasks.mac\" and anchor apple generic and certificate leaf[subject.OU] = \"\(team)\""
}

@main private enum FanHelperMain {
    static func main() {
        SMC.selfTest()
        Fans.selfTest()
        let helper = Helper()
        let listener = NSXPCListener(machServiceName: "com.notchtasks.fanhelper")
        guard let requirement = appRequirement() else { fatalError("Could not create the app signing requirement") }
        // NSXPC checks each incoming peer's audit identity before the delegate sees it.
        listener.setConnectionCodeSigningRequirement(requirement)
        listener.delegate = helper
        listener.resume()
        signal(SIGTERM, SIG_IGN)
        signal(SIGINT, SIG_IGN)
        let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        let interruption = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
        termination.setEventHandler { helper.shutdown() }
        interruption.setEventHandler { helper.shutdown() }
        termination.resume(); interruption.resume()
        RunLoop.main.run()
    }
}
