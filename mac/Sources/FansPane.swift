import SwiftUI
import ServiceManagement

struct FanPane: View {
    @ObservedObject var model: Model
    @ObservedObject private var control: FanControl
    @State private var sensorsOpen = false

    init(model: Model) { self.model = model; _control = ObservedObject(wrappedValue: model.fanControl) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("Quạt & nhiệt độ")).font(.title2.bold())
            HStack(spacing: 12) {
                if let hottest = Thermals.hottest(model.thermals) {
                    Label("\(Int(hottest.celsius.rounded()))°", systemImage: "thermometer.medium")
                        .foregroundStyle(hottest.celsius >= 85 ? .red : hottest.celsius >= 60 ? .orange : .green).help(hottest.name)
                } else { Label("—", systemImage: "thermometer.medium") }
                ForEach(model.fans) { fan in
                    Spacer(minLength: 8)
                    Label(FanNotch.name(count: model.fans.count, index: fan.index) + " " +
                          FanNotch.valueText(actual: model.fanUnavailable.contains(fan.index) ? nil : fan.actual,
                                             forced: fan.forced, parked: fan.actual == 0 && !fan.forced), systemImage: "fanblades.fill")
                }
            }.font(.system(size: 13, weight: .semibold, design: .rounded)).monospacedDigit()
            VStack(alignment: .leading, spacing: 8) {
                Text(L("Quạt")).font(.headline)
                if model.fans.isEmpty { Text(L("Máy này không báo quạt qua SMC; chỉ đọc nhiệt độ vẫn hoạt động.")).foregroundStyle(.secondary) }
                else { NotchFanPanel(model: model, pane: true) }
            }.settingsCard()
            VStack(alignment: .leading, spacing: 6) {
                Button { sensorsOpen.toggle() } label: {
                    Label(L("Cảm biến") + " (\(model.thermals.count))", systemImage: sensorsOpen ? "chevron.down" : "chevron.right")
                }.buttonStyle(.plain).font(.headline)
                if sensorsOpen {
                    if model.thermals.isEmpty { Text(L("Không đọc được cảm biến nhiệt độ.")).foregroundStyle(.secondary) }
                    ForEach(Thermals.grouped(model.thermals), id: \.name) { sensor in
                        HStack { Text(sensor.name).lineLimit(1); Spacer(); Text(sensor.value).monospacedDigit() }
                            .font(.caption).help(sensor.details)
                    }
                }
            }.settingsCard()
            FanEnableControl(control: control, compact: true, footer: true)
        }
    }
}

struct FanEnableControl: View {
    @ObservedObject var control: FanControl
    var compact = false
    var footer = false
    @State private var consent = FanConsentStep.idle

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 4 : 6) {
            if footer {
                HStack(spacing: 8) {
                    Circle().fill(control.enabled && control.available ? Color.green : Color.gray).frame(width: 6, height: 6)
                    Text(control.enabled ? control.available ? L("Trợ lý quạt đang bật") : control.needsApproval ? L("Trợ lý quạt cần được cho phép") : L("Trợ lý quạt chưa sẵn sàng") : L("Trợ lý quạt đang tắt"))
                        .foregroundStyle(.secondary)
                    if !control.status.isEmpty { Text(control.status).foregroundStyle(.secondary).lineLimit(2).help(control.status) }
                    Spacer()
                    if control.enabled && control.available { Button(L("Tắt")) { control.disable() } }
                    else if control.enabled && control.needsApproval { Button(L("Mở Cài đặt hệ thống")) { SMAppService.openSystemSettingsLoginItems() } }
                    else if control.enabled { Button(L("Bật trợ lý quạt")) { control.register() } }
                    else if consent != .confirming { Button(L("Bật trợ lý quạt")) { consent = consent.advanced() } }
                }
            }
            if !control.enabled {
                if !footer || consent == .confirming {
                    Text(L("Điều khiển quạt có thể làm hỏng phần cứng nếu dùng sai.")).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                }
                if consent == .confirming {
                    Text(L("Trợ lý luôn giới hạn tốc độ theo mức tối thiểu và tối đa máy báo, và tự về tự động khi mất kết nối hoặc máy quá nóng."))
                        .fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button(L("Huỷ")) { consent = consent.cancelled() }
                        Button(L("Tôi hiểu, bật")) { consent = consent.advanced(); control.enable() }
                            .buttonStyle(.borderedProminent)
                    }
                } else if !footer {
                    Button(L("Bật trợ lý quạt")) { consent = consent.advanced() }
                }
            } else if !control.available && !footer {
                if control.needsApproval {
                    Text(L("Hãy cho phép trợ lý trong Mục đăng nhập của Cài đặt hệ thống.")).foregroundStyle(.orange)
                    Button(L("Mở Cài đặt hệ thống")) { SMAppService.openSystemSettingsLoginItems() }
                } else {
                    Button(L("Bật trợ lý quạt")) { control.register() }
                }
            }
        }
        .font(compact ? .system(size: 11) : .body)
        .controlSize(compact ? .mini : .regular)
        .frame(maxWidth: .infinity, alignment: .leading)
        .task {
            while !Task.isCancelled {
                control.refreshStatus()
                try? await Task.sleep(nanoseconds: 3_000_000_000)
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in control.refreshStatus() }
    }
}

enum FanConsentStep {
    case idle, confirming, consented

    func advanced() -> Self { self == .idle ? .confirming : .consented }
    func cancelled() -> Self { .idle }

    static func selfTest() {
        precondition(FanConsentStep.idle.advanced() == .confirming
                     && FanConsentStep.idle.advanced().advanced() == .consented
                     && FanConsentStep.idle.advanced().cancelled() == .idle, "fan consent steps")
    }
}
