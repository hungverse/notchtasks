import Foundation

// Read-only probe: compile with `swiftc -parse-as-library Sources/SMC.swift Tools/read-fans.swift -o /tmp/read-fans`.
@main enum FanProbe {
    static func display(_ key: String, smcAvailable: Bool) {
        guard let value = SMC.read(key) else { print("\(key): \(smcAvailable ? "key not found" : "SMC unavailable")"); return }
        let number: String
        if value.type == "flt ", value.bytes.count == 4 {
            let bits = value.bytes.enumerated().reduce(UInt32(0)) { $0 | UInt32($1.element) << (8 * UInt32($1.offset)) }
            number = String(Float(bitPattern: bits))
        } else if let rpm = SMC.rpm(value) {
            number = String(rpm)
        } else if let integer = SMC.integer(value) {
            number = String(integer)
        } else {
            number = value.bytes.map { String(format: "%02x", $0) }.joined(separator: " ")
        }
        print("\(key) type '\(value.type)' size \(value.bytes.count) = \(number)")
    }

    static func main() {
        SMC.selfTest()
        let countValue = SMC.read("FNum")
        let smcAvailable = countValue != nil
        display("FNum", smcAvailable: smcAvailable)
        let count = countValue.flatMap(SMC.integer) ?? 0
        for index in 0..<min(count, 16) {
            let prefix = "F\(index)"
            for suffix in ["Ac", "Mn", "Mx", "Tg", "Md", "St"] { display("\(prefix)\(suffix)", smcAvailable: smcAvailable) }
        }
        display("Ftst", smcAvailable: smcAvailable)
    }
}
