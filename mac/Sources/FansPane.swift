import SwiftUI
import ServiceManagement

struct FanPane: View {
    @ObservedObject var model: Model
    @ObservedObject private var control: FanControl
    @State private var sensorsOpen = false

    init(model: Model) { self.model = model; _control = ObservedObject(wrappedValue: model.fanControl) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(L("Fans & temperature")).font(.title2.bold())
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
                Text(L("Fans")).font(.headline)
                if model.fans.isEmpty { Text(L("This Mac does not report fans through SMC; temperature reading still works.")).foregroundStyle(.secondary) }
                else { NotchFanPanel(model: model, pane: true) }
            }.settingsCard()
            VStack(alignment: .leading, spacing: 6) {
                Button { sensorsOpen.toggle() } label: {
                    Label(L("Sensors") + " (\(model.thermals.count))", systemImage: sensorsOpen ? "chevron.down" : "chevron.right")
                }.buttonStyle(.plain).font(.headline)
                if sensorsOpen {
                    if model.thermals.isEmpty { Text(L("Could not read temperature sensors.")).foregroundStyle(.secondary) }
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
                    Text(control.enabled ? control.available ? L("Fan helper is on") : control.needsApproval ? L("Fan helper needs approval") : L("Fan helper is not ready") : L("Fan helper is off"))
                        .foregroundStyle(.secondary)
                    if !control.status.isEmpty { Text(control.status).foregroundStyle(.secondary).lineLimit(2).help(control.status) }
                    Spacer()
                    if control.enabled && control.available { Button(L("Off")) { control.disable() } }
                    else if control.enabled && control.needsApproval { Button(L("Open System Settings")) { SMAppService.openSystemSettingsLoginItems() } }
                    else if control.enabled { Button(L("Enable fan helper")) { control.register() } }
                    else if consent != .confirming { Button(L("Enable fan helper")) { consent = consent.advanced() } }
                }
            }
            if !control.enabled {
                if !footer || consent == .confirming {
                    Text(L("Fan control can damage hardware when used incorrectly.")).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
                }
                if consent == .confirming {
                    Text(L("The helper always keeps speeds within the machine-reported minimum and maximum, and returns to automatic when it loses its connection or the Mac is too hot."))
                        .fixedSize(horizontal: false, vertical: true)
                    HStack {
                        Button(L("Cancel")) { consent = consent.cancelled() }
                        Button(L("I understand, enable")) { consent = consent.advanced(); control.enable() }
                            .buttonStyle(.borderedProminent)
                    }
                } else if !footer {
                    Button(L("Enable fan helper")) { consent = consent.advanced() }
                }
            } else if !control.available && !footer {
                if control.needsApproval {
                    Text(L("Allow the helper in Login Items in System Settings.")).foregroundStyle(.orange)
                    Button(L("Open System Settings")) { SMAppService.openSystemSettingsLoginItems() }
                } else {
                    Button(L("Enable fan helper")) { control.register() }
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
