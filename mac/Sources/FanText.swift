import Foundation

enum FanBubblePhase {
    case released, pressedThumb, moving, movingReleased, arrived, dragging
    var visible: Bool { self == .pressedThumb || self == .arrived || self == .dragging }

    static func selfTest() {
        precondition(pressedThumb.visible && !moving.visible && arrived.visible && dragging.visible && !released.visible
                     && !movingReleased.visible, "fan bubble visibility")
    }
}

enum FanNotch {
    enum Preset { case automatic, half, maximum }

    static func openWidth(left: CGFloat, right: CGFloat, camera: CGFloat, padding: CGFloat) -> CGFloat {
        2 * max(left, right) + camera + padding
    }
    static func thumbValue(_ fan: FanReading, choice: FanChoice) -> Double {
        if case let .fixed(rpm) = choice { return Double(rpm) }
        return Double(fan.actual == 0 && choice == .automatic ? fan.minimum : fan.actual)
    }

    static func name(count: Int, index: Int) -> String {
        count == 1 ? L("Fans") : count == 2 ? L(index == 0 ? "Left" : "Right") : L("Fan {0}", index + 1)
    }
    static func half(_ fan: FanReading) -> Int { fan.minimum + (fan.maximum - fan.minimum) / 2 }
    static func shouldSend(last: Date?, now: Date) -> Bool { last.map { now.timeIntervalSince($0) >= 0.3 } ?? true }
    static func refreshInterval(panelOpen: Bool, sinceCommand: TimeInterval?) -> TimeInterval {
        if let sinceCommand, sinceCommand < 0.6 { return .infinity }
        return panelOpen ? 1 : 5
    }
    static func valueText(actual: Int?, forced: Bool, parked: Bool) -> String {
        guard let actual else { return "—" }
        return actual == 0 && (parked || forced) ? L("Stop") : String(actual)
    }
    static func keepLastGood(previous: [FanReading], reads: [FanReading], failures: [Int: Int]) -> (fans: [FanReading], failures: [Int: Int]) {
        var next = failures
        let fresh = Dictionary(uniqueKeysWithValues: reads.map { ($0.index, $0) })
        let fans = previous.map { old -> FanReading in
            if let reading = fresh[old.index] { next[old.index] = 0; return reading }
            next[old.index, default: 0] += 1
            return old
        } + reads.filter { fan in !previous.contains(where: { $0.index == fan.index }) }
        return (fans, next)
    }
    static func turnsPerSecond(_ rpm: Int, minimum: Int, maximum: Int) -> Double {
        guard rpm > 0 else { return 0 }
        guard maximum > minimum else { return 0.5 }
        let fraction = min(max(Double(rpm - minimum) / Double(maximum - minimum), 0), 1)
        return 0.5 + 1.1 * min(fraction / 0.6, 1)
    }
    static func blur(_ rpm: Int, minimum: Int, maximum: Int) -> Double {
        guard rpm > 0, maximum > minimum else { return 0 }
        let fraction = min(max(Double(rpm - minimum) / Double(maximum - minimum), 0), 1)
        return min(max((fraction - 0.6) / 0.4, 0), 1)
    }
    static func frameDelta(_ last: Date?, _ now: Date) -> Double {
        last.map { min(max(now.timeIntervalSince($0), 0), 0.05) } ?? 0
    }
    static func snappedRPM(_ raw: Double, minimum: Int, maximum: Int) -> Int {
        guard maximum > minimum else { return minimum }
        let range = Double(maximum - minimum)
        if raw <= Double(minimum) + 0.03 * range { return minimum }
        if raw >= Double(maximum) - 0.03 * range { return maximum }
        return min(max(Int((raw / 50).rounded()) * 50, minimum), maximum)
    }
    static func sliderRPM(at x: CGFloat, width: CGFloat, minimum: Int, maximum: Int) -> Int {
        let fraction = min(max((x - 9) / max(width - 18, 1), 0), 1)
        return snappedRPM(Double(minimum) + Double(maximum - minimum) * fraction,
                          minimum: minimum, maximum: maximum)
    }
    static func choiceOnInput(_ rpm: Int) -> FanChoice { .fixed(rpm) }
    static func easedSpeed(_ current: Double, target: Double, dt: Double) -> Double {
        current + (target - current) * (1 - exp(-max(dt, 0) / 0.6))
    }
    static func preset(fans: [FanReading], choices: [Int: FanChoice]) -> Preset? {
        guard !fans.isEmpty else { return nil }
        let selected = fans.map { choices[$0.index] ?? .automatic }
        if selected.allSatisfy({ $0 == .automatic }) { return .automatic }
        if zip(fans, selected).allSatisfy({ $0.1 == .fixed(half($0.0)) }) { return .half }
        if zip(fans, selected).allSatisfy({ $0.1 == .fixed($0.0.maximum) }) { return .maximum }
        return nil
    }
    static func rowText(_ fan: FanReading, choice: FanChoice, hot: Bool) -> String {
        if hot { return L("Fans returned to automatic because the Mac is too hot") }
        let actual = fan.actual == 0 ? L("Stop") : "\(fan.actual) rpm"
        switch choice {
        case .automatic: return L("Automatic") + " · " + actual
        case .fixed: return actual
        case .sensor: return L("By sensor") + " · " + actual
        }
    }

    static func selfTest() {
        let fan = FanReading(index: 0, actual: 0, minimum: 1200, maximum: 6000, target: 0, forced: false)
        let now = Date()
        precondition(openWidth(left: 70, right: 82, camera: 200, padding: 32) == 396
                     && openWidth(left: 82, right: 70, camera: 200, padding: 32) == 396
                     && openWidth(left: 82, right: 30, camera: 200, padding: 32) == 396, "symmetric top groups")
        precondition(thumbValue(FanReading(index: 0, actual: 2300, minimum: 1200, maximum: 6000, target: 0, forced: false), choice: .automatic) == 2300
                     && thumbValue(fan, choice: .automatic) == 1200
                     && thumbValue(fan, choice: .fixed(3200)) == 3200, "fan thumb position")
        precondition(name(count: 1, index: 0) == L("Fans")
                     && name(count: 2, index: 0) == L("Left")
                     && name(count: 2, index: 1) == L("Right")
                     && name(count: 3, index: 2) == L("Fan {0}", 3), "notch fan names")
        precondition(half(fan) == 3600, "notch half speed")
        precondition(refreshInterval(panelOpen: false, sinceCommand: nil) == 5
                     && refreshInterval(panelOpen: true, sinceCommand: nil) == 1
                     && refreshInterval(panelOpen: true, sinceCommand: 0.3) == .infinity
                     && refreshInterval(panelOpen: true, sinceCommand: 0.6) == 1, "fan refresh cadence")
        precondition(valueText(actual: 0, forced: false, parked: true) == L("Stop")
                     && valueText(actual: 2400, forced: true, parked: false) == "2400"
                     && valueText(actual: nil, forced: false, parked: false) == "—", "fan value states")
        let kept = keepLastGood(previous: [fan], reads: [], failures: [:])
        let twice = keepLastGood(previous: kept.fans, reads: [], failures: kept.failures)
        let thrice = keepLastGood(previous: twice.fans, reads: [], failures: twice.failures)
        precondition(kept.fans == [fan] && kept.failures[0] == 1
                     && twice.failures[0] == 2 && thrice.failures[0] == 3
                     && keepLastGood(previous: thrice.fans, reads: [fan], failures: thrice.failures).failures[0] == 0,
                     "fan read keeps last good value")
        precondition(shouldSend(last: nil, now: now)
                     && !shouldSend(last: now, now: now.addingTimeInterval(0.299))
                     && shouldSend(last: now, now: now.addingTimeInterval(0.301)), "notch slider coalescing")
        precondition(turnsPerSecond(0, minimum: 1200, maximum: 6000) == 0
                     && turnsPerSecond(1200, minimum: 1200, maximum: 6000) == 0.5
                     && abs(turnsPerSecond(2640, minimum: 1200, maximum: 6000) - 1.05) < 0.0001
                     && turnsPerSecond(4080, minimum: 1200, maximum: 6000) == 1.6
                     && turnsPerSecond(6000, minimum: 1200, maximum: 6000) == 1.6
                     && blur(4080, minimum: 1200, maximum: 6000) == 0
                     && abs(blur(5040, minimum: 1200, maximum: 6000) - 0.5) < 0.0001
                     && blur(6000, minimum: 1200, maximum: 6000) == 1, "notch fan visual speed and blur")
        precondition(easedSpeed(0, target: 1.6, dt: 0) == 0
                     && abs(easedSpeed(0, target: 1.6, dt: 0.6) - 1.6 * (1 - exp(-1))) < 0.0001
                     && abs(easedSpeed(1.6, target: 0, dt: 0.6) - 1.6 * exp(-1)) < 0.0001,
                     "notch fan visual easing")
        precondition(frameDelta(nil, now) == 0
                     && frameDelta(now, now.addingTimeInterval(-1)) == 0
                     && abs(frameDelta(now, now.addingTimeInterval(0.02)) - 0.02) < 0.0001
                     && frameDelta(now, now.addingTimeInterval(1)) == 0.05, "notch fan frame delta")
        precondition(snappedRPM(1200, minimum: 1200, maximum: 6000) == 1200
                     && snappedRPM(1340, minimum: 1200, maximum: 6000) == 1200
                     && snappedRPM(1345, minimum: 1200, maximum: 6000) == 1350
                     && snappedRPM(3609, minimum: 1200, maximum: 6000) == 3600
                     && snappedRPM(5856, minimum: 1200, maximum: 6000) == 6000
                     && snappedRPM(9000, minimum: 1200, maximum: 6000) == 6000,
                     "notch fan snap and magnets")
        precondition(sliderRPM(at: 0, width: 118, minimum: 1200, maximum: 6000) == 1200
                     && sliderRPM(at: 59, width: 118, minimum: 1200, maximum: 6000) == 3600
                     && sliderRPM(at: 118, width: 118, minimum: 1200, maximum: 6000) == 6000,
                     "notch slider pointer mapping")
        precondition(choiceOnInput(3200) == .fixed(3200), "automatic input takes control")
        let other = FanReading(index: 1, actual: 0, minimum: 1200, maximum: 6200, target: 0, forced: false)
        precondition(preset(fans: [fan, other], choices: [:]) == .automatic
                     && preset(fans: [fan, other], choices: [0: .fixed(3600), 1: .fixed(3700)]) == .half
                     && preset(fans: [fan, other], choices: [0: .fixed(6000), 1: .fixed(6200)]) == .maximum
                     && preset(fans: [fan, other], choices: [0: .automatic, 1: .fixed(6200)]) == nil,
                     "notch fan preset selection")
        precondition(rowText(fan, choice: .automatic, hot: false) == L("Automatic") + " · " + L("Stop")
                     && rowText(FanReading(index: 0, actual: 2400, minimum: 1200, maximum: 6000, target: 0, forced: false), choice: .automatic, hot: false) == L("Automatic") + " · 2400 rpm"
                     && rowText(FanReading(index: 0, actual: 2400, minimum: 1200, maximum: 6000, target: 3200, forced: true), choice: .fixed(3200), hot: false) == "2400 rpm"
                     && rowText(fan, choice: .sensor(name: "CPU", startsAt: 50, maximumAt: 80), hot: false) == L("By sensor") + " · " + L("Stop")
                     && rowText(fan, choice: .automatic, hot: true) == L("Fans returned to automatic because the Mac is too hot"), "notch fan row states")
    }
}
