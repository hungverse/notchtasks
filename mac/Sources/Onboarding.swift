import AppKit
import SwiftUI

enum OnboardingExit { case close, phone, notch }

enum OnboardingGuide {
    enum NextStep: Equatable { case permission(String), agent(AgentKind), phone }
    static let pages = ["welcome", "modules", "agents", "permissions", "phone", "ready"]

    static func needs(_ item: PermItem, modules: Set<ModuleID>, notifications: Bool) -> Bool {
        if item.id == "notifications" { return notifications && !modules.isDisjoint(with: [.agents, .ci]) }
        return item.module.map(modules.contains) ?? false
    }

    static func nextSteps(items: [PermItem], modules: Set<ModuleID>, notifications: Bool,
                          agents: [AgentKind], installed: Set<AgentKind>, paired: Bool) -> [NextStep] {
        items.filter { needs($0, modules: modules, notifications: notifications) &&
            $0.state != .granted && $0.state != .checking }.map { .permission($0.id) }
        + agents.filter { !installed.contains($0) }.map { .agent($0) }
        + (paired ? [] : [.phone])
    }

    static func selfTest() {
        precondition(pages == ["welcome", "modules", "agents", "permissions", "phone", "ready"], "guide order without agents")
        let calendar = PermItem(id: "calendar", title: "", detail: "", symbol: "", module: .calendar,
                                state: .notAsked, pane: "", tccService: nil)
        let camera = PermItem(id: "camera", title: "", detail: "", symbol: "", module: .mirror,
                              state: .notAsked, pane: "", tccService: nil)
        precondition(needs(calendar, modules: [.calendar], notifications: false)
                     && !needs(camera, modules: [.calendar], notifications: false), "permissions follow choices")
        precondition(nextSteps(items: [calendar, camera], modules: [.calendar], notifications: false,
                               agents: [.claude], installed: [], paired: false) ==
                     [.permission("calendar"), .agent(.claude), .phone], "next steps")
        precondition(nextSteps(items: [calendar], modules: [], notifications: false,
                               agents: [], installed: [], paired: true).isEmpty, "completed next steps")
    }
}

struct OnboardingView: View {
    @ObservedObject var model: Model
    @ObservedObject var permissions: Permissions
    @ObservedObject var settings = AppSettings.shared
    let finish: (OnboardingExit) -> Void
    @State private var page = 0
    @State private var asksOn = Dictionary(uniqueKeysWithValues: AgentKind.allCases.map { ($0, AgentAsks.installed($0)) })
    @State private var asksError: String?
    private let lastPage = OnboardingGuide.pages.count - 1
    private let columns = Array(repeating: GridItem(.flexible(), spacing: 8), count: 3)

    var foundAgents: [AgentKind] { AgentKind.allCases.filter(\.present) }
    var chosenModules: Set<ModuleID> { Set(settings.activeModules) }

    var body: some View {
        VStack(spacing: 0) {
            Group {
                switch page {
                case 0: welcome
                case 1: modules
                case 2: agents
                case 3: perms
                case 4: phone
                default: ready
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .padding(.horizontal, 32).padding(.top, 40).padding(.bottom, 10)
            .id(page)
            .transition(.asymmetric(insertion: .move(edge: .trailing).combined(with: .opacity), removal: .opacity))

            Divider()
            HStack {
                HStack(spacing: 6) {
                    ForEach(0...lastPage, id: \.self) { i in
                        Capsule().fill(i == page ? Color.accentColor : .secondary.opacity(0.3))
                            .frame(width: i == page ? 18 : 7, height: 7)
                    }
                }
                Text(L("Bước {0}/{1}", page + 1, lastPage + 1)).font(.caption).foregroundStyle(.secondary).padding(.leading, 6)
                Spacer()
                Button(L("Bỏ qua")) { done() }.buttonStyle(.borderless).foregroundStyle(.secondary)
                    .keyboardShortcut(.cancelAction)
                if page > 0 { Button(L("Quay lại")) { withAnimation(.snappy) { page -= 1 } } }
                Button(page == lastPage ? L("Cho tôi xem") : page == 4 ? L("Để sau") : L("Tiếp")) {
                    if page == lastPage { done(.notch) } else { withAnimation(.snappy) { page += 1 } }
                }
                .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
            .padding(16)
        }
        .frame(width: 740, height: 640)
        .onAppear { permissions.refresh() }
        .onReceive(DistributedNotificationCenter.default().publisher(for: .init("local.notchtasks.debug.expand"))) { n in
            if let o = n.object as? String, o.hasPrefix("guide-"), let p = Int(o.dropFirst(6)) { page = min(max(p, 0), lastPage) }
        }
    }

    func done(_ action: OnboardingExit = .close) {
        settings.onboardingDone = true
        Track.event("onboarding_done")
        finish(action)
    }

    func header(_ title: String, _ subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.title2.bold())
            Text(subtitle).font(.callout).foregroundStyle(.secondary)
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    var welcome: some View {
        VStack(spacing: 20) {
            NotchPreview(model: model).frame(width: 360)
            Text(L("Chào mừng đến NotchTasks")).font(.title2.bold())
            Text(L("Notch hiện những gì đang chạy, và im lặng khi không có gì."))
                .multilineTextAlignment(.center).foregroundStyle(.secondary)
            Label(L("Nhìn lên mép trên màn hình: đó là notch của bạn."), systemImage: "arrow.up")
                .font(.callout).settingsCard()
        }.frame(maxWidth: .infinity)
    }

    var modules: some View {
        VStack(alignment: .leading, spacing: 10) {
            header(L("Hiện gì trên notch"), L("Bấm để bật hoặc tắt. Đổi lại bất cứ lúc nào trong Bảng điều khiển."))
            ForEach(ModuleID.groups, id: \.title) { group in
                VStack(alignment: .leading, spacing: 4) {
                    Text(group.title.uppercased()).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    LazyVGrid(columns: columns, spacing: 6) {
                        ForEach(group.modules) { module in moduleTile(module) }
                    }
                }
            }
            HStack(spacing: 8) {
                Image(systemName: "fan.fill").frame(width: 22)
                Text(L("Quạt & nhiệt độ")).font(.caption.weight(.semibold))
                Spacer()
                if !Requirement.fan.met(on: Requirement.current) {
                    Text(Requirement.fan.label).font(.system(size: 9)).foregroundStyle(.secondary)
                        .padding(4).background(Capsule().fill(.quaternary))
                }
                Toggle("", isOn: Binding(get: { Requirement.fan.met(on: Requirement.current) && model.fanControl.enabled },
                                         set: { enabled in
                                             if enabled {
                                                 model.showSettings()
                                                 NotificationCenter.default.post(name: .openPane, object: "fans")
                                             } else { model.fanControl.disable() }
                                         }))
                    .labelsHidden().toggleStyle(.switch).controlSize(.mini)
                    .disabled(!Requirement.fan.met(on: Requirement.current))
                    .accessibilityLabel(L("Quạt & nhiệt độ"))
            }.settingsCard(padding: 8)
        }
    }

    func moduleTile(_ module: ModuleID) -> some View {
        HStack(spacing: 8) {
            Image(systemName: module.symbol).foregroundStyle(Color.accentColor).frame(width: 22)
            VStack(alignment: .leading, spacing: 1) {
                Text(module.title).font(.caption.weight(.semibold)).lineLimit(1)
                Text(module.blurb).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(2)
                if let requirement = module.requirement, !requirement.met(on: Requirement.current) {
                    Text(requirement.label).font(.system(size: 9)).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 0)
            Toggle("", isOn: Binding(get: { settings.on(module) }, set: { settings.set(module, $0) }))
                .labelsHidden().toggleStyle(.switch).controlSize(.mini)
                .accessibilityLabel(module.title)
                .disabled(module.requirement.map { !$0.met(on: Requirement.current) } ?? false)
        }
        .padding(.horizontal, 8).frame(height: 45).settingsCard(padding: 0)
    }

    var agents: some View {
        VStack(alignment: .leading, spacing: 12) {
            header(L("Trả lời agent trên notch"), L("Mỗi công tắc thêm một mục vào cấu hình agent và sao lưu bản cũ. Có hiệu lực từ phiên mở sau."))
            if foundAgents.isEmpty {
                Text(L("Chưa thấy AI agent nào. Thêm sau ở trang Theo dõi.")).foregroundStyle(.secondary).settingsCard()
            } else {
                ForEach(foundAgents) { kind in
                    HStack(spacing: 12) {
                        if let item = kind.item(in: model.items) { ItemIcon(item: item, size: 26) }
                        Text(kind.name).font(.body.weight(.medium))
                        Spacer()
                        Toggle(L("Trả lời agent trên notch"), isOn: Binding(get: { asksOn[kind] ?? false }, set: { on in
                            do { try AgentAsks.setInstalled(on, kind); asksError = nil }
                            catch { asksError = L("Không sửa được cấu hình {0}: {1}", kind.name, error.localizedDescription) }
                            asksOn[kind] = AgentAsks.installed(kind)
                            if on, asksOn[kind] == true, let setup = kind.setupStep { AgentAsks.openInTerminal(setup.command) }
                        })).toggleStyle(.switch).controlSize(.small)
                    }.settingsCard(padding: 10)
                }
            }
            if let asksError { Text(asksError).font(.caption).foregroundStyle(.red) }
        }
        .onAppear { asksOn = Dictionary(uniqueKeysWithValues: AgentKind.allCases.map { ($0, AgentAsks.installed($0)) }) }
    }

    var perms: some View {
        VStack(alignment: .leading, spacing: 12) {
            header(L("Quyền"), L("Chỉ những gì mô-đun đã chọn cần. Có thể bỏ qua."))
            let shown = permissions.items.filter { OnboardingGuide.needs($0, modules: chosenModules, notifications: settings.systemNotifications) }
            if permissions.items.isEmpty { Text(L("Đang kiểm tra…")).foregroundStyle(.secondary).settingsCard() }
            else if shown.isEmpty { Text(L("Chưa cần cấp quyền nào.")).foregroundStyle(.secondary).settingsCard() }
            LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 8) {
                ForEach(shown) { item in
                    HStack(spacing: 8) {
                        Image(systemName: item.symbol).foregroundStyle(item.state.color).frame(width: 22)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(item.title).font(.caption.weight(.semibold)).lineLimit(1)
                            Text(item.detail).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer(minLength: 0)
                        if item.state == .checking { Text(item.state.label).font(.caption).foregroundStyle(.secondary) }
                        else if item.state == .granted { Image(systemName: "checkmark.circle.fill").foregroundStyle(.green) }
                        else { Button(item.state == .denied ? L("Mở Cài đặt") : item.state == .needsApp ? L("Mở ứng dụng") : L("Cho phép")) {
                            permissions.request(item)
                        }.controlSize(.mini) }
                    }.settingsCard(padding: 8)
                }
            }
        }.onAppear { permissions.refresh() }
    }

    var phone: some View {
        VStack(alignment: .leading, spacing: 16) {
            header(L("Điện thoại (tuỳ chọn)"), L("Ghép điện thoại để trả lời agent từ màn hình khoá khi rời máy tính."))
            Button(L("Mở Ghép nối")) { done(.phone) }.buttonStyle(.bordered)
        }
    }

    var ready: some View {
        VStack(alignment: .leading, spacing: 12) {
            header(L("Sẵn sàng rồi"), L("Ba điều cần nhớ:"))
            step(["cursorarrow.rays"], L("Mở notch"), L("Trỏ chuột vào notch ở mép trên màn hình."))
            step(["gearshape", "power"], L("Bảng điều khiển"), L("Nút Bảng điều khiển và Thoát ở góc dưới bên phải khi notch mở."))
            step(["menubar.rectangle"], L("Biểu tượng trên thanh menu"), L("Biểu tượng trên thanh menu cũng mở Bảng điều khiển khi được bật."))
        }
    }

    func step(_ symbols: [String], _ title: String, _ text: String) -> some View {
        HStack(spacing: 12) {
            HStack(spacing: 4) {
                ForEach(symbols, id: \.self) { symbol in
                    Image(systemName: symbol).font(.system(size: 15)).foregroundStyle(Color.accentColor)
                        .frame(width: 32, height: 32).background(Circle().fill(Color.accentColor.opacity(0.15)))
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.subheadline.weight(.semibold))
                Text(text).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
        }.settingsCard()
    }
}
