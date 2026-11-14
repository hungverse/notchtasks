import Foundation

struct FanReading: Codable, Equatable, Identifiable, Sendable {
    let index: Int
    let actual: Int
    let minimum: Int
    let maximum: Int
    let target: Int
    let forced: Bool
    var id: Int { index }
}

enum FanCurve {
    static func clamp(_ rpm: Int, minimum: Int, maximum: Int) -> Int { min(max(rpm, minimum), maximum) }

    static func target(celsius: Double, startsAt: Double, maximumAt: Double, minimum: Int, maximum: Int) -> Int {
        guard maximumAt > startsAt else { return celsius >= maximumAt ? maximum : minimum }
        let progress = min(max((celsius - startsAt) / (maximumAt - startsAt), 0), 1)
        return Int((Double(minimum) + Double(maximum - minimum) * progress).rounded())
    }

    /// Limit one control tick to 10% of the hardware range so a noisy sensor cannot hunt the fan.
    static func limited(_ target: Int, previous: Int?, minimum: Int, maximum: Int) -> Int {
        guard let previous else { return clamp(target, minimum: minimum, maximum: maximum) }
        let step = max(1, Int((Double(maximum - minimum) * 0.1).rounded(.down)))
        return clamp(min(max(target, previous - step), previous + step), minimum: minimum, maximum: maximum)
    }
}

enum FanWatchdog {
    static func shouldAuto(lastHeartbeat: Date?, now: Date, hottestCelsius: Double?) -> Bool {
        hottestCelsius.map { $0 >= 95 } == true || lastHeartbeat.map { now.timeIntervalSince($0) > 30 } ?? true
    }
}

enum FanThermalGuard {
    static func tripped(_ wasTripped: Bool, hottest: Double) -> Bool { wasTripped ? hottest >= 85 : hottest >= 95 }
}

enum Fans {
    static func read() -> [FanReading] {
        guard let count = SMC.read("FNum").flatMap(SMC.integer), count > 0 else { return [] }
        return (0..<min(count, 16)).compactMap { index in
            let prefix = "F\(index)"
            guard let actual = SMC.read("\(prefix)Ac").flatMap(SMC.rpm),
                  let minimum = SMC.read("\(prefix)Mn").flatMap(SMC.rpm),
                  let maximum = SMC.read("\(prefix)Mx").flatMap(SMC.rpm),
                  minimum <= maximum else { return nil }
            let target = SMC.read("\(prefix)Tg").flatMap(SMC.rpm) ?? actual
            let forced = SMC.read("\(prefix)Md").flatMap(SMC.integer).map { $0 != 0 } ?? false
            return FanReading(index: index, actual: actual, minimum: minimum, maximum: maximum, target: target, forced: forced)
        }
    }

    static func selfTest() {
        let littleFloat = SMC.Value(type: "flt ", bytes: [0, 0, 72, 69]) // 3200 as a little-endian Float
        let fixed = SMC.Value(type: "fpe2", bytes: [0x32, 0x00]) // 12,800 / 4
        precondition(SMC.rpm(littleFloat) == 3200 && SMC.rpm(fixed) == 3200, "fan flt/fpe2 decode")
        precondition(FanCurve.clamp(9000, minimum: 1200, maximum: 6100) == 6100, "fan clamp")
        precondition(FanCurve.target(celsius: 40, startsAt: 50, maximumAt: 80, minimum: 1200, maximum: 4200) == 1200
                     && FanCurve.target(celsius: 80, startsAt: 50, maximumAt: 80, minimum: 1200, maximum: 4200) == 4200
                     && FanCurve.target(celsius: 65, startsAt: 50, maximumAt: 80, minimum: 1200, maximum: 4200) == 2700, "fan curve")
        precondition(FanCurve.limited(4200, previous: 1200, minimum: 1200, maximum: 4200) == 1500, "fan curve step")
        let now = Date()
        precondition(FanWatchdog.shouldAuto(lastHeartbeat: now.addingTimeInterval(-31), now: now, hottestCelsius: nil)
                     && FanWatchdog.shouldAuto(lastHeartbeat: now, now: now, hottestCelsius: 95)
                     && !FanWatchdog.shouldAuto(lastHeartbeat: now, now: now, hottestCelsius: 60), "fan watchdog")
        precondition(!FanThermalGuard.tripped(false, hottest: 94.9)
                     && FanThermalGuard.tripped(false, hottest: 95)
                     && FanThermalGuard.tripped(true, hottest: 90)
                     && !FanThermalGuard.tripped(true, hottest: 84.9), "thermal guard hysteresis")
    }
}
