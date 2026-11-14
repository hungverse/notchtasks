import Foundation
import IOKit
import IOKit.hidsystem

struct ThermalReading: Equatable, Sendable {
    let name: String
    let celsius: Double
}

enum Thermals {
    struct GroupedSensor {
        let name: String
        let value: String
        let details: String
        let hottest: Double
    }

    static func grouped(_ readings: [ThermalReading]) -> [GroupedSensor] {
        let die = readings.filter { isDieSensor($0.name) }
        var result = readings.filter { !isDieSensor($0.name) }.map {
            GroupedSensor(name: $0.name, value: String(format: "%.1f°", $0.celsius), details: $0.name, hottest: $0.celsius)
        }
        if let low = die.map(\.celsius).min(), let high = die.map(\.celsius).max() {
            result.append(GroupedSensor(name: L("Chip"), value: "\(Int(low.rounded()))–\(Int(high.rounded()))°",
                                        details: die.map { "\($0.name): \(String(format: "%.1f", $0.celsius))°" }.joined(separator: "\n"), hottest: high))
        }
        return result.sorted { $0.hottest > $1.hottest }
    }
    private static let temperatureEvent = Int64(15)
    private static let temperatureField = temperatureEvent << 16

    @_silgen_name("IOHIDEventSystemClientSetMatching")
    private static func setMatching(_ client: IOHIDEventSystemClient, _ matching: CFDictionary)
    @_silgen_name("IOHIDEventSystemClientCreate")
    private static func createClient(_ allocator: CFAllocator?) -> IOHIDEventSystemClient
    @_silgen_name("IOHIDServiceClientCopyEvent")
    private static func copyEvent(_ service: IOHIDServiceClient, _ type: Int64, _ options: Int32, _ timestamp: Int64) -> CFTypeRef?
    @_silgen_name("IOHIDEventGetFloatValue")
    private static func floatValue(_ event: CFTypeRef, _ field: Int64) -> Double

    private static let client: IOHIDEventSystemClient = {
        let client = createClient(kCFAllocatorDefault)
        setMatching(client, ["PrimaryUsagePage": 0xff00, "PrimaryUsage": 5] as CFDictionary)
        return client
    }()

    /// CPU/SoC die temperatures for the computer-health UI.
    static func read() -> [ThermalReading] {
        all().filter { isDieSensor($0.name) }
    }

    /// Every plausible temperature exposed by the hardware, retained for the sensor view planned next.
    static func all() -> [ThermalReading] {
        #if arch(arm64)
        appleSilicon()
        #else
        intelSMC()
        #endif
    }

    /// The UI and wire both use the hottest plausible CPU/SoC die, never a battery or storage temperature.
    static func hottest(_ readings: [ThermalReading]) -> ThermalReading? {
        readings.filter { (5...125).contains($0.celsius) }.max { $0.celsius < $1.celsius }
    }

    private static func appleSilicon() -> [ThermalReading] {
        let services = (IOHIDEventSystemClientCopyServices(client) as NSArray? ?? []).map { $0 as! IOHIDServiceClient }
        let readings = services.compactMap { service -> ThermalReading? in
            let name = IOHIDServiceClientCopyProperty(service, "Product" as CFString) as? String ?? "SoC sensor"
            guard let event = copyEvent(service, temperatureEvent, 0, 0) else { return nil }
            let reading = ThermalReading(name: name, celsius: floatValue(event, temperatureField))
            return (5...125).contains(reading.celsius) ? reading : nil
        }
        return Dictionary(readings.map { ($0.name, $0) }, uniquingKeysWith: { $0.celsius >= $1.celsius ? $0 : $1 })
            .values.sorted { $0.name < $1.name }
    }

    private static func isDieSensor(_ name: String) -> Bool {
        name.localizedCaseInsensitiveContains("die") || name.hasPrefix("PMU tdie")
            || name.hasPrefix("SOC MTR") || name.hasPrefix("pACC MTR") || name.hasPrefix("eACC MTR")
    }

    private static func intelSMC() -> [ThermalReading] {
        let keys = [("TC0P", "CPU proximity"), ("TC0E", "CPU die"), ("TC0F", "CPU die"), ("TC0D", "CPU die")]
        return keys.compactMap { key, label in SMC.read(key).flatMap(SMC.temperature).map { ThermalReading(name: "\(label) (\(key))", celsius: $0) } }
            .filter { (5...125).contains($0.celsius) }
    }

    static func selfTest() {
        let readings = [ThermalReading(name: "CPU", celsius: 53.2), ThermalReading(name: "SoC", celsius: 68.8),
                        ThermalReading(name: "bad low", celsius: 4), ThermalReading(name: "bad high", celsius: 126)]
        precondition(hottest(readings) == ThermalReading(name: "SoC", celsius: 68.8), "hottest valid die reading")
        precondition(hottest([ThermalReading(name: "bad", celsius: 130)]) == nil, "bad readings dropped")
        let grouped = grouped([ThermalReading(name: "PMU tdie0", celsius: 46), ThermalReading(name: "PMU tdie10", celsius: 52), ThermalReading(name: "GPU", celsius: 49)])
        precondition(grouped.count == 2 && grouped[0].name == L("Chip") && grouped[0].value == "46–52°"
                     && grouped[0].details.contains("PMU tdie10") && grouped[1].name == "GPU", "sensor grouping")
    }
}
