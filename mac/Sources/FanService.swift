import Foundation
import ServiceManagement

@objc protocol FanHelperProtocol {
    func fans(_ reply: @escaping (Data) -> Void)
    func setFan(_ index: Int, rpm: Int, reply: @escaping (String?) -> Void)
    func setAuto(_ index: Int, reply: @escaping (String?) -> Void)
    func heartbeat(_ reply: @escaping (String?) -> Void)
}

enum FanChoice: Codable, Equatable {
    case automatic
    case fixed(Int)
    case sensor(name: String, startsAt: Double, maximumAt: Double)
}

@MainActor final class FanControl: ObservableObject {
    @Published private(set) var status = ""
    @Published private(set) var enabled: Bool
    @Published private(set) var needsApproval = false
    @Published private(set) var available = false
    @Published private(set) var hot = false
    @Published var choices: [Int: FanChoice] = [:] { didSet {
        save()
        if enabled && !Self.needsControl(oldValue) && needsControl { register() }
    } }
    var onOverheat: (() -> Void)?
    var onCommandAcknowledged: (() -> Void)?
    private let service = SMAppService.daemon(plistName: "com.notchtasks.fanhelper.plist")
    private var connection: NSXPCConnection?
    private var lastTarget: [Int: Int] = [:]
    private var activeSent = Set<Int>()
    private var helperAnswered = false
    private var pendingAuto = 0
    private var heartbeatID: UUID?
    private var repairedThisLaunch = false
    private var repairing = false
    private var lastFans: [FanReading] = []
    private var lastSensors: [ThermalReading] = []
    private let markerKey = "fanHelperRegisteredMarker"

    private static var marker: String {
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? ""
        let date = Bundle.main.executableURL.flatMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }
        return "\(build):\(date?.timeIntervalSince1970 ?? 0)"
    }

    init() {
        enabled = Requirement.fan.met(on: Requirement.current) && UserDefaults.standard.bool(forKey: "fanControlEnabled")
        if let data = UserDefaults.standard.data(forKey: "fanChoices"), let choices = try? JSONDecoder().decode([Int: FanChoice].self, from: data) { self.choices = choices }
        refreshStatus()
        if Self.shouldRepair(registeredMarker: UserDefaults.standard.string(forKey: markerKey), currentMarker: Self.marker,
                        serviceStatus: service.status, enabled: enabled, alreadyRepaired: repairedThisLaunch) { repair() }
    }

    static func needsControl(_ choices: [Int: FanChoice]) -> Bool {
        choices.values.contains { $0 != .automatic }
    }
    static func shouldRepair(registeredMarker: String?, currentMarker: String, serviceStatus: SMAppService.Status,
                             enabled: Bool, alreadyRepaired: Bool) -> Bool {
        enabled && serviceStatus == .enabled && !alreadyRepaired && registeredMarker != currentMarker
    }
    static func selfTest() {
        precondition(!needsControl([:]) && !needsControl([0: .automatic, 1: .automatic])
                     && needsControl([0: .automatic, 1: .fixed(1200)]), "no helper call with automatic choices")
        precondition(shouldRepair(registeredMarker: "old", currentMarker: "new", serviceStatus: .enabled, enabled: true, alreadyRepaired: false)
                     && shouldRepair(registeredMarker: nil, currentMarker: "new", serviceStatus: .enabled, enabled: true, alreadyRepaired: false)
                     && !shouldRepair(registeredMarker: "new", currentMarker: "new", serviceStatus: .enabled, enabled: true, alreadyRepaired: false)
                     && !shouldRepair(registeredMarker: "old", currentMarker: "new", serviceStatus: .requiresApproval, enabled: true, alreadyRepaired: false)
                     && !shouldRepair(registeredMarker: "old", currentMarker: "new", serviceStatus: .enabled, enabled: false, alreadyRepaired: false)
                     && !shouldRepair(registeredMarker: "old", currentMarker: "new", serviceStatus: .enabled, enabled: true, alreadyRepaired: true), "fan helper repair once")
    }

    func refreshStatus() {
        let current = service.status
        if needsApproval != (current == .requiresApproval) { needsApproval = current == .requiresApproval }
        if available != (current == .enabled) { available = current == .enabled }
    }
    var hasSensorControl: Bool { choices.values.contains { if case .sensor = $0 { return true }; return false } }
    var needsControl: Bool { Self.needsControl(choices) }

    func enable() {
        guard Requirement.fan.met(on: Requirement.current) else { return }
        enabled = true
        UserDefaults.standard.set(true, forKey: "fanControlEnabled")
        if repairedThisLaunch && service.status == .enabled { repair(manual: true) }
        else if Self.shouldRepair(registeredMarker: UserDefaults.standard.string(forKey: markerKey), currentMarker: Self.marker,
                        serviceStatus: service.status, enabled: enabled, alreadyRepaired: repairedThisLaunch) { repair() }
        else { register() }
    }

    func disable() {
        choices = [:]
        restoreAutomaticAndClose()
        enabled = false
        UserDefaults.standard.set(false, forKey: "fanControlEnabled")
        status = ""
        refreshStatus()
    }

    func setHot(_ value: Bool) { hot = value }

    func register() {
        guard enabled else { return }
        refreshStatus()
        guard !available else { return }
        do { try service.register(); UserDefaults.standard.set(Self.marker, forKey: markerKey); refreshStatus(); status = "" }
        catch { refreshStatus(); status = needsApproval ? "" : L("Could not register the fan helper: {0}", error.localizedDescription) }
    }

    private func repair(manual: Bool = false) {
        guard !repairing, enabled, service.status == .enabled,
              manual || Self.shouldRepair(registeredMarker: nil, currentMarker: Self.marker, serviceStatus: service.status,
                                          enabled: enabled, alreadyRepaired: repairedThisLaunch) else { return }
        repairedThisLaunch = true
        repairing = true
        close()
        Task { @MainActor in
            do {
                try await service.unregister()
                guard enabled else { repairing = false; return }
                try service.register()
                UserDefaults.standard.set(Self.marker, forKey: markerKey)
                refreshStatus()
                repairing = false
                if needsApproval { status = ""; return }
                connect()
                if !lastFans.isEmpty { tick(fans: lastFans, sensors: lastSensors) }
            } catch {
                refreshStatus()
                repairing = false
                status = needsApproval ? "" : L("Could not register the fan helper: {0}", error.localizedDescription) + "\n" + L("Turn the fan helper off and on again in the Fans pane.")
            }
        }
    }

    func tick(fans: [FanReading], sensors: [ThermalReading]) {
        lastFans = fans; lastSensors = sensors
        guard needsControl else { restoreAutomaticAndClose(); return }
        guard enabled && !hot else { return }
        guard !fans.isEmpty else { return }
        connect()
        guard let remote else { return }
        let connection = self.connection
        if heartbeatID == nil {
            let id = UUID()
            heartbeatID = id
            remote.heartbeat { [weak self] error in DispatchQueue.main.async {
                guard let self, self.connection === connection, self.heartbeatID == id else { return }
                self.heartbeatID = nil
                if let error { self.connectionFailed(error) }
                else { self.status = "" }
            } }
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self, self.connection === connection, self.heartbeatID == id else { return }
                self.heartbeatID = nil
                self.connectionFailed("no reply")
            }
        }
        guard helperAnswered else {
            remote.fans { [weak self] data in
                DispatchQueue.main.async {
                    guard let self, self.connection === connection else { return }
                    guard (try? JSONDecoder().decode([FanReading].self, from: data)) != nil else { self.connectionFailed("no reply"); return }
                    self.helperAnswered = true
                    self.apply(fans: fans, sensors: sensors)
                }
            }
            return
        }
        apply(fans: fans, sensors: sensors)
    }

    private func apply(fans: [FanReading], sensors: [ThermalReading]) {
        for fan in fans {
            switch choices[fan.index] ?? .automatic {
            case .automatic: automatic(fan.index)
            case let .fixed(rpm): set(fan, rpm: rpm)
            case let .sensor(name, start, end):
                guard let sensor = sensors.first(where: { $0.name == name }) else { continue }
                let target = FanCurve.target(celsius: sensor.celsius, startsAt: start, maximumAt: end, minimum: fan.minimum, maximum: fan.maximum)
                set(fan, rpm: FanCurve.limited(target, previous: lastTarget[fan.index], minimum: fan.minimum, maximum: fan.maximum))
            }
        }
    }

    private func set(_ fan: FanReading, rpm: Int) {
        let rpm = FanCurve.clamp(rpm, minimum: fan.minimum, maximum: fan.maximum)
        guard lastTarget[fan.index] != rpm || !activeSent.contains(fan.index) else { return }
        lastTarget[fan.index] = rpm
        activeSent.insert(fan.index)
        let connection = self.connection
        remote?.setFan(fan.index, rpm: rpm) { [weak self] error in DispatchQueue.main.async {
            guard let self, self.connection === connection else { return }
            if error != nil { self.lastTarget[fan.index] = nil; self.activeSent.remove(fan.index) }
            if error == "Fan control paused until every sensor is below 85°C" {
                self.hot = true
                self.status = L("Fans returned to automatic because the Mac is too hot")
                self.onOverheat?()
            } else { self.status = error ?? "" }
            if error == nil { self.onCommandAcknowledged?() }
        } }
    }

    private func automatic(_ index: Int) {
        guard activeSent.remove(index) != nil else { return }
        lastTarget[index] = nil
        guard let remote else { close(); return }
        let connection = self.connection
        pendingAuto += 1
        remote.setAuto(index) { [weak self] error in DispatchQueue.main.async {
            guard let self, self.connection === connection else { return }
            self.pendingAuto -= 1
            if let error { self.status = error }
            else { self.onCommandAcknowledged?() }
            if !self.needsControl && self.pendingAuto == 0 { self.close() }
        } }
    }

    private func restoreAutomaticAndClose() {
        for index in Array(activeSent) { automatic(index) }
        if pendingAuto == 0 { close() }
    }
    func stop(_ fans: [FanReading]) { restoreAutomaticAndClose() }

    private func close() {
        connection?.invalidate(); connection = nil; helperAnswered = false
        heartbeatID = nil; pendingAuto = 0
        lastTarget.removeAll(); activeSent.removeAll()
    }

    private var remote: FanHelperProtocol? {
        guard let connection else { return nil }
        return connection.remoteObjectProxyWithErrorHandler { [weak self] error in DispatchQueue.main.async {
            guard let self, self.connection === connection else { return }
            self.connectionFailed(error.localizedDescription)
        } } as? FanHelperProtocol
    }

    private func connectionFailed(_ reason: String) {
        refreshStatus()
        close()
        if needsApproval { status = ""; return }
        status = L("Could not connect to the fan helper: {0}", reason)
        if repairedThisLaunch { status += "\n" + L("Turn the fan helper off and on again in the Fans pane.") }
        else { repair() }
    }

    private func connect() {
        guard connection == nil, available, !repairing else { return }
        let connection = NSXPCConnection(machServiceName: "com.notchtasks.fanhelper", options: .privileged)
        connection.remoteObjectInterface = NSXPCInterface(with: FanHelperProtocol.self)
        connection.invalidationHandler = { [weak self] in DispatchQueue.main.async {
            guard let self, self.connection === connection else { return }
            self.close()
            if self.enabled { self.connectionFailed("connection invalidated") }
        } }
        connection.resume(); self.connection = connection
    }

    private func save() { UserDefaults.standard.set(try? JSONEncoder().encode(choices), forKey: "fanChoices") }
}
