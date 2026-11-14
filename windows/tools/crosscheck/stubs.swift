import Foundation
import CryptoKit

/// The Mac's LinkFrames (Sources/Sync.swift), copied: the phone's copy lives in a file that needs UIKit and Firebase.
enum LinkFrames {
    static func serviceName(_ g: String) -> String {
        "NotchTasks " + SHA256.hash(data: Data(g.utf8)).prefix(6).map { String(format: "%02x", $0) }.joined()
    }
    static func size(_ head: Data) -> Int { head.reduce(0) { $0 << 8 | Int($1) } }
}
