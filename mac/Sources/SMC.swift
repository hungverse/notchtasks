import Foundation
import IOKit

/// Shared read-only AppleSMC protocol. Only the helper build includes the write methods.
enum SMC {
    struct Value { let type: String; let bytes: [UInt8] }
    private static let selector: UInt32 = 2
    private static let keyInfo: UInt8 = 9, readBytes: UInt8 = 5
    private struct Version { var major: UInt8 = 0; var minor: UInt8 = 0; var build: UInt8 = 0; var reserved: UInt8 = 0; var release: UInt16 = 0 }
    private struct PowerLimit { var version: UInt16 = 0; var length: UInt16 = 0; var cpu: UInt32 = 0; var gpu: UInt32 = 0; var memory: UInt32 = 0 }
    private struct KeyInfo { var size: UInt32 = 0; var type: UInt32 = 0; var attributes: UInt8 = 0; var padding: (UInt8, UInt8, UInt8) = (0, 0, 0) }
    private struct KeyData {
        var key: UInt32 = 0; var version = Version(); var limit = PowerLimit(); var info = KeyInfo()
        var result: UInt8 = 0; var status: UInt8 = 0; var command: UInt8 = 0; var padding: UInt8 = 0; var data32: UInt32 = 0
        var bytes: (UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8,
                    UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8, UInt8) =
            (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    }

    static func selfTest() {
        precondition(MemoryLayout<KeyData>.size == 80 && MemoryLayout<KeyData>.offset(of: \.key) == 0
            && MemoryLayout<KeyData>.offset(of: \.version) == 4 && MemoryLayout<KeyData>.offset(of: \.limit) == 12
            && MemoryLayout<KeyData>.offset(of: \.info) == 28 && MemoryLayout<KeyData>.offset(of: \.result) == 40
            && MemoryLayout<KeyData>.offset(of: \.status) == 41 && MemoryLayout<KeyData>.offset(of: \.command) == 42
            && MemoryLayout<KeyData>.offset(of: \.data32) == 44 && MemoryLayout<KeyData>.offset(of: \.bytes) == 48, "SMC 80-byte layout")
        precondition(type(0x666c7420) == "flt " && type(0x75693820) == "ui8 ", "SMC fourCC type decode")
        precondition(fourCC("F0Ac") == 0x46304163, "SMC key fourCC")
        var info = KeyData(), data = KeyData()
        info.info.size = 4; info.info.type = 0x666c7420
        data.bytes.0 = 0; data.bytes.1 = 0; data.bytes.2 = 72; data.bytes.3 = 69
        precondition(decode(info: info, data: data, dataSize: 80).flatMap(rpm) == 3200, "SMC read reply decoding")
        info.info.size = 1; info.info.type = 0x75693820; data.bytes.0 = 2
        precondition(decode(info: info, data: data, dataSize: 80).flatMap(integer) == 2, "SMC count reply decoding")
        data.result = 1
        precondition(decode(info: info, data: data, dataSize: 80) == nil, "SMC reply result")
        data.result = 0
        precondition(decode(info: info, data: data, dataSize: 48) == nil, "SMC reply length")
    }

    static func read(_ key: String) -> Value? { withConnection { connection in
        guard key.utf8.count == 4 else { return nil }
        var input = KeyData(), output = KeyData(), size = MemoryLayout<KeyData>.size
        input.key = fourCC(key); input.command = keyInfo
        guard IOConnectCallStructMethod(connection, selector, &input, MemoryLayout<KeyData>.size, &output, &size) == KERN_SUCCESS,
              size >= 41, output.result == 0, (1...32).contains(Int(output.info.size)) else { return nil }
        let infoReply = output
        input.info = infoReply.info; input.command = readBytes; output = KeyData(); size = MemoryLayout<KeyData>.size
        guard IOConnectCallStructMethod(connection, selector, &input, MemoryLayout<KeyData>.size, &output, &size) == KERN_SUCCESS,
              size >= 48 + Int(infoReply.info.size), output.result == 0 else { return nil }
        return decode(info: infoReply, data: output, dataSize: size)
    } }

    private static func decode(info: KeyData, data: KeyData, dataSize: Int) -> Value? {
        guard (1...32).contains(Int(info.info.size)), dataSize >= 48 + Int(info.info.size), data.result == 0 else { return nil }
        return Value(type: type(info.info.type), bytes: withUnsafeBytes(of: data.bytes) { Array($0.prefix(Int(info.info.size))) })
    }

    static func integer(_ value: Value) -> Int? {
        guard let first = value.bytes.first else { return nil }
        if value.type == "ui16", value.bytes.count >= 2 { return Int(UInt16(value.bytes[0]) << 8 | UInt16(value.bytes[1])) }
        return Int(first)
    }

    static func rpm(_ value: Value) -> Int? {
        switch value.type {
        case "flt ":
            guard value.bytes.count >= 4 else { return nil }
            let bits = value.bytes.prefix(4).enumerated().reduce(UInt32(0)) { $0 | UInt32($1.element) << (8 * UInt32($1.offset)) }
            let rpm = Float(bitPattern: bits)
            return rpm.isFinite && rpm >= 0 && rpm <= 100_000 ? Int(rpm.rounded()) : nil
        case "fpe2":
            guard value.bytes.count >= 2 else { return nil }
            return Int((Double(UInt16(value.bytes[0]) << 8 | UInt16(value.bytes[1])) / 4).rounded())
        default: return nil
        }
    }

    static func temperature(_ value: Value) -> Double? {
        guard value.type == "sp78", value.bytes.count >= 2 else { return nil }
        return Double(Int16(bitPattern: UInt16(value.bytes[0]) << 8 | UInt16(value.bytes[1]))) / 256
    }

    private static func withConnection<T>(_ body: (io_connect_t) -> T?) -> T? {
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSMC"))
        guard service != IO_OBJECT_NULL else { return nil }
        defer { IOObjectRelease(service) }
        var connection = io_connect_t()
        guard IOServiceOpen(service, mach_task_self_, 0, &connection) == KERN_SUCCESS else { return nil }
        defer { IOServiceClose(connection) }
        return body(connection)
    }
    private static func fourCC(_ key: String) -> UInt32 { key.utf8.reduce(0) { ($0 << 8) | UInt32($1) } }
    private static func type(_ value: UInt32) -> String {
        String(bytes: [UInt8(truncatingIfNeeded: value >> 24), UInt8(truncatingIfNeeded: value >> 16),
                       UInt8(truncatingIfNeeded: value >> 8), UInt8(truncatingIfNeeded: value)], encoding: .ascii) ?? "????"
    }

    #if FAN_HELPER
    static func writeMode(_ index: Int, forced: Bool) -> Bool {
        guard (0..<16).contains(index) else { return false }
        return write("F\(index)Md", data: { value in value.type == "ui8 " && value.bytes.count == 1 ? [forced ? 1 : 0] : nil })
    }
    static func writeRPM(_ index: Int, rpm: Int) -> Bool {
        guard (0..<16).contains(index) else { return false }
        return write("F\(index)Tg", data: { value in
            if value.type == "flt ", value.bytes.count == 4 {
                return withUnsafeBytes(of: Float(rpm).bitPattern.littleEndian) { Array($0) }
            }
            if value.type == "fpe2", value.bytes.count == 2, rpm >= 0, rpm <= 16383 {
                return [UInt8(truncatingIfNeeded: (rpm * 4) >> 8), UInt8(truncatingIfNeeded: rpm * 4)]
            }
            return nil
        })
    }
    static func unlockIfPresent() -> Bool {
        guard read("Ftst") != nil else { return true }
        return write("Ftst", data: { value in value.type == "ui8 " && value.bytes.count == 1 ? [1] : nil })
    }
    private static func write(_ key: String, data: (Value) -> [UInt8]?) -> Bool {
        guard let value = read(key), let bytes = data(value), bytes.count == value.bytes.count else { return false }
        return withConnection { connection in
            var input = KeyData(), output = KeyData(), size = MemoryLayout<KeyData>.size
            input.key = fourCC(key); input.info.size = UInt32(bytes.count); input.command = 6
            withUnsafeMutableBytes(of: &input.bytes) { $0.copyBytes(from: bytes) }
            return IOConnectCallStructMethod(connection, selector, &input, MemoryLayout<KeyData>.size, &output, &size) == KERN_SUCCESS
                && size >= 41 && output.result == 0
        } ?? false
    }
    #endif
}
