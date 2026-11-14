import AppKit
import SwiftUI

struct MacReport {
    static func payload(uid: String, g: String, phones: Int, proUntil: Double?, product: String, tx: String,
                        kind: String, text: String, contact: String) -> [String: Any] {
        let info = Bundle.main.infoDictionary ?? [:]
        return ["uid": uid, "g": String(g.prefix(100)), "role": "mac", "kind": "problem", "auto": false,
                "text": String(decoding: (kind + ": " + text).utf16.prefix(2000), as: UTF16.self),
                "contact": String(decoding: contact.utf16.prefix(200), as: UTF16.self),
                "app": ["version": String((info["CFBundleShortVersionString"] as? String ?? "").prefix(80)),
                        "build": String((info["CFBundleVersion"] as? String ?? "").prefix(80)),
                        "os": String(ProcessInfo.processInfo.operatingSystemVersionString.prefix(80)),
                        "model": "Mac", "lang": String(L10n.code.prefix(40))],
                "pro": ["own": false, "product": String(product.prefix(100)), "tx": String(tx.prefix(100)), "until": 0,
                        "group": (proUntil ?? 0) > Date().timeIntervalSince1970 * 1000, "groupUntil": proUntil ?? 0],
                "context": ["paired": phones > 0, "phones": phones], "at": [".sv": "timestamp"]]
    }

    static func selfTest() {
        let p = payload(uid: "test-uid", g: "AAAAAAAAAAAAAAAAAAAAAA", phones: 1, proUntil: 32503680000000,
                        product: "com.notchtasks.ios.pro.year", tx: "1000000000000001",
                        kind: "Ghép nối", text: "Pairing stopped", contact: "user@example.com")
        precondition(Set(p.keys) == ["uid", "g", "role", "kind", "auto", "text", "contact", "app", "pro", "context", "at"])
        precondition((p["pro"] as? [String: Any])?["group"] as? Bool == true)
        let clipped = payload(uid: "test-uid", g: "AAAAAAAAAAAAAAAAAAAAAA", phones: 1, proUntil: nil,
                              product: "", tx: "", kind: "Khác", text: String(repeating: "x", count: 2100),
                              contact: String(repeating: "a", count: 210))
        precondition((clipped["text"] as? String)?.utf16.count == 2000 && (clipped["contact"] as? String)?.utf16.count == 200)
    }
}

struct MacReportSheet: View {
    @ObservedObject var sync: SyncHub
    @Environment(\.dismiss) private var dismiss
    @State private var topic = L("Khác")
    @State private var text = ""
    @State private var contact = ""
    @State private var code: String?
    @State private var problem: String?
    @State private var busy = false

    var body: some View {
        Form {
            Picker(L("Loại vấn đề"), selection: $topic) {
                ForEach([L("Ghép nối"), L("Quyền"), L("Quạt & nhiệt độ"), L("Cập nhật"), L("Khác")], id: \.self) { Text($0) }
            }
            TextField(L("Chuyện gì đã xảy ra?"), text: $text, axis: .vertical).lineLimit(3...6)
            TextField(L("Email hoặc cách liên hệ (để chúng tôi trả lời)"), text: $contact)
            DisclosureGroup(L("Thông tin gửi kèm")) {
                Text(L("Nội dung và liên hệ bạn nhập; mã thiết bị ẩn danh, mã nhóm, tình trạng ghép nối, số điện thoại, phiên bản ứng dụng, hệ điều hành, mẫu thiết bị, ngôn ngữ, trạng thái Pro và giao dịch nếu có. Không tự động gửi lệnh, mã nguồn, đường dẫn, tên công việc hay tên dự án."))
            }
            if let code {
                HStack {
                    Text(L("Đã gửi. Mã báo lỗi: {0}", String(code.suffix(6))))
                    Button(L("Sao chép")) { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(String(code.suffix(6)), forType: .string) }
                }
            }
            if let problem { Text(problem).foregroundStyle(.orange) }
            HStack {
                Button(L("Đóng")) { dismiss() }
                Spacer()
                Button(L("Gửi")) {
                    busy = true
                    Task {
                        do { code = try await sync.sendReport(topic: topic, text: text, contact: contact) }
                        catch { problem = L("Không gửi được báo lỗi. Thử lại khi có mạng.") }
                        busy = false
                    }
                }.disabled(text.isEmpty || busy || code != nil)
            }
        }
        .padding(20)
        .frame(width: 500)
    }
}
