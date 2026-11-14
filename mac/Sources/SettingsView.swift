import AppKit
import SwiftUI
import Darwin

// MARK: - Watch list editor (a pane of the control panel)

struct WatchListView: View {
    @ObservedObject var model: Model
    @State private var editing: WatchItem?
    @State private var isNew = false
    @State private var asksOn = Dictionary(uniqueKeysWithValues: AgentKind.allCases.map { ($0, AgentAsks.installed($0)) })
    @State private var asksError: String?
    @State private var setupCheck = 0

    var runningApps: [NSRunningApplication] {
        NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.bundleIdentifier != nil }
            .sorted { ($0.localizedName ?? "") < ($1.localizedName ?? "") }
    }

    func added(_ p: WatchItem) -> Bool {
        model.items.contains { $0.kind == p.kind && $0.process == p.process && $0.args == p.args && $0.bundleID == p.bundleID }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(L("Những gì notch theo dõi. Chỉ cái đang làm việc mới hiện lên notch."))
                .font(.callout).foregroundStyle(.secondary)
            List {
                ForEach($model.items) { $item in
                    HStack(spacing: 10) {
                        ItemIcon(item: item, size: 26)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.name).font(.body.weight(.medium))
                            Text((item.inBuild ? "Build & CI · " : "AI agent · ") + item.summary).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer()
                        Button(L("Sửa")) { isNew = false; editing = item }.controlSize(.small)
                        Toggle(L("Bật"), isOn: $item.enabled).labelsHidden().toggleStyle(.switch).controlSize(.small)
                        Button { model.items.removeAll { $0.id == item.id } } label: { Image(systemName: "trash") }
                            .buttonStyle(.borderless).help(L("Xoá"))
                    }
                    .frame(height: 44)
                }
                .onMove { model.items.move(fromOffsets: $0, toOffset: $1) }
            }
            .fittedList(rows: model.items.count)

            Block {
                VStack(alignment: .leading, spacing: 10) {
                    // one entry point: every way of adding lives in the same menu
                    Menu {
                            ForEach(presetGroups + [("Build & CI", buildPresets)], id: \.title) { group in
                                Section(group.title) {
                                    // installed tools first, each marked; ones already in the list can't be added twice
                                    ForEach(group.items.sorted { $0.isInstalled && !$1.isInstalled }) { p in
                                        Button((p.isInstalled ? "✓ " : "") + p.name + (added(p) ? L(" (đã thêm)") : "")) { model.add(p) }
                                            .disabled(added(p))
                                    }
                                }
                            }
                            Divider()
                            Menu(L("Ứng dụng đang mở")) {
                                ForEach(runningApps, id: \.processIdentifier) { app in
                                    Button(app.localizedName ?? app.bundleIdentifier!) {
                                        model.add(WatchItem(name: app.localizedName ?? "App", kind: .app, bundleID: app.bundleIdentifier))
                                    }
                                }
                            }
                            Button(L("Chọn ứng dụng khác…"), action: pickApp)
                            Button(L("Tự nhập tiến trình…")) {
                                isNew = true
                                editing = WatchItem(name: "", kind: .process)
                            }
                    } label: {
                        Label(L("Thêm mục theo dõi"), systemImage: "plus.circle.fill")
                    }
                    .fixedSize()
                    Text(L("Chọn agent, ứng dụng đang mở, hoặc nhập tên tiến trình."))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .padding(6)
            }
            Block {
                VStack(alignment: .leading, spacing: 0) {
                    HStack(alignment: .top, spacing: 10) {
                        Image(systemName: "hand.raised.fill").foregroundStyle(.orange).frame(width: 26).padding(.top, 1)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(L("Trả lời agent trên notch")).font(.body.weight(.medium))
                            Text(L("Khi agent hỏi, notch hiện sẵn nút trả lời."))
                                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    .padding(.bottom, 8)
                    ForEach(AgentKind.allCases.filter { $0.present || asksOn[$0] == true }) { k in
                        Divider()
                        HStack(spacing: 10) {
                            Group {
                                if let item = k.item(in: model.items) { ItemIcon(item: item, size: 26) }
                            }
                            .frame(width: 26)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(k.name).font(.body.weight(.medium))
                                Text(note(k)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                            Spacer(minLength: 8)
                            // what's left, with the one button that does it; else that it works
                            if let step = k.setupStep, asksOn[k] == true || k == .gemini {
                                Text(step.missing).font(.caption).foregroundStyle(.orange)
                                Button(step.button) { AgentAsks.openInTerminal(step.command) }.controlSize(.small)
                            } else if asksOn[k] == true {
                                Label(L("Sẵn sàng"), systemImage: "checkmark.circle.fill").font(.caption).foregroundStyle(.green)
                            }
                            Toggle("", isOn: Binding(get: { asksOn[k] ?? false }, set: { on in
                                do { try AgentAsks.setInstalled(on, k); asksError = nil }
                                catch { asksError = L("Không sửa được cấu hình {0}: {1}", k.name, error.localizedDescription) }
                                asksOn[k] = AgentAsks.installed(k)
                                // just switched on and one step is left: go straight to it
                                if on, asksOn[k] == true, let step = k.setupStep { AgentAsks.openInTerminal(step.command) }
                            }))
                            .toggleStyle(.switch).labelsHidden().controlSize(.small)
                        }
                        .frame(height: 44)
                    }
                    Divider()
                    Text(L("Có hiệu lực từ lần mở agent tiếp theo."))
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 8)
                    if let asksError { Text(asksError).font(.caption).foregroundStyle(.red).padding(.top, 4) }
                }
                .padding(6)
                .id(setupCheck)  // re-checks the steps when you come back from Terminal
            }
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in setupCheck += 1 }
            BuildToolsBox()
        }
        // screenshot checks: "edit-item" opens the editor on the first row
        .onReceive(DistributedNotificationCenter.default().publisher(for: .init("local.notchtasks.debug.expand"))) { n in
            if n.object as? String == "edit-item" { isNew = false; editing = model.items.first }
        }
        .sheet(item: $editing) { item in
            ItemEditor(item: item, isNew: isNew) { saved in
                if isNew { model.add(saved) } else if let i = model.items.firstIndex(where: { $0.id == saved.id }) { model.items[i] = saved }
                editing = nil
            } cancel: { editing = nil }
        }
    }

    /// What each agent can do from the notch, and the one step some need.
    func note(_ k: AgentKind) -> String {
        switch k {
        case .claude, .gemini, .antigravity: return L("Duyệt lệnh, sửa file, trả lời câu hỏi")
        case .codex: return L("Duyệt lệnh, sửa file")
        case .cursor: return L("Từ chối lệnh. Cho phép chỉ khi Cursor đang tự chạy lệnh")
        case .opencode: return L("Duyệt lệnh, sửa file, truy cập web")
        }
    }

    func pickApp() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.application]
        panel.directoryURL = URL(fileURLWithPath: "/Applications")
        guard panel.runModal() == .OK, let url = panel.url, let id = Bundle(url: url)?.bundleIdentifier else { return }
        model.add(WatchItem(name: FileManager.default.displayName(atPath: url.path).replacingOccurrences(of: ".app", with: ""), kind: .app, bundleID: id))
    }
}

/// Add/edit one watched item, with what it matches right now so the settings can be checked on the spot.
struct ItemEditor: View {
    @State var item: WatchItem
    let isNew: Bool
    let save: (WatchItem) -> Void
    let cancel: () -> Void
    @State private var matches: [(pid: pid_t, name: String, command: String)] = []
    @State private var runningNames: [String] = []
    @State private var editingIcon = false

    var configurable: Bool { item.kind == .process || item.kind == .app }
    var valid: Bool {
        !item.name.trimmingCharacters(in: .whitespaces).isEmpty
            && (item.kind != .process || !item.process.isEmpty || !(item.args ?? "").isEmpty)
    }

    var body: some View {
        VStack(spacing: 0) {
            // what is being edited, so the window never opens as a bare form
            HStack(spacing: 12) {
                ItemIcon(item: item, size: 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(isNew ? L("Thêm mục theo dõi") : (item.name.isEmpty ? L("Mục theo dõi") : item.name)).font(.headline)
                    Text(item.inBuild ? "Build & CI" : "AI agent").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
            }
            .padding(.horizontal, 20).padding(.top, 18).padding(.bottom, 4)
            Form {
                Section {
                    TextField(L("Tên hiển thị"), text: $item.name, prompt: Text("vd: My Agent"))
                    if configurable {
                        Picker(L("Hiện trong"), selection: Binding(get: { item.build == true }, set: { item.build = $0 ? true : nil })) {
                            Text("AI agent").tag(false)
                            Text("Build & CI").tag(true)
                        }
                        .pickerStyle(.segmented)
                    }
                    // one row: the icon window (the same one fixed icons use) opens on top
                    HStack(spacing: 12) {
                        Text(L("Biểu tượng"))
                        Spacer()
                        Button { editIcon() } label: { ItemIcon(item: item, size: 28) }.buttonStyle(.plain).help(L("Đổi biểu tượng"))
                        Button(L("Đổi…"), action: editIcon)
                    }
                    .sheet(isPresented: $editingIcon) {
                        BadgeEditor(badge: Binding(get: { item.icon ?? Badge() }, set: { item.icon = $0 }),
                                    title: L("Biểu tượng"), subtitle: item.name.isEmpty ? nil : item.name,
                                    reset: { item.icon = nil; editingIcon = false }, done: { editingIcon = false })
                    }
                } footer: {
                    if item.kind == .claude || item.kind == .codex {
                        Text(L("{0} được theo dõi theo từng phiên làm việc, không cần chỉnh gì thêm.", item.kind == .claude ? "Claude" : "Codex"))
                    }
                }
                if item.kind == .process {
                    Section {
                        HStack {
                            TextField(L("Tên tiến trình"), text: $item.process, prompt: Text("vd: node"))
                            Menu(L("Chọn")) {
                                ForEach(runningNames, id: \.self) { n in Button(n) { item.process = n; refresh() } }
                            }
                            .fixedSize()
                        }
                        TextField(L("Dòng lệnh có chứa"), text: Binding(get: { item.args ?? "" }, set: { item.args = $0.isEmpty ? nil : $0 }),
                                  prompt: Text(L("tuỳ chọn, vd: gemini-cli")))
                    } header: {
                        Text(L("Nhận diện"))
                    } footer: {
                        Text(L("Tên tiến trình, như trong Activity Monitor."))
                    }
                    Section(L("Mỗi dòng trên notch là")) {
                        Picker("", selection: $item.childTasks) {
                            Text(L("Mỗi lần chạy")).tag(false)
                            Text(L("Mỗi lệnh con (runner, worker)")).tag(true)
                        }
                        .pickerStyle(.radioGroup).labelsHidden()
                    }
                }
                if configurable {
                    Section {
                        HStack {
                            Slider(value: Binding(get: { item.cpuThreshold }, set: { item.threshold = ($0 * 2).rounded() / 2 }), in: 0.5...50)
                            Text("\(item.cpuThreshold.formatted(.number.precision(.fractionLength(0...1))))%").monospacedDigit().frame(width: 44, alignment: .trailing)
                        }
                    } header: {
                        Text(L("Đang làm việc khi CPU trên"))
                    } footer: {
                        Text(L("Tăng nếu hiện cả lúc rảnh, giảm nếu bỏ sót."))
                    }
                    Section {
                        if matches.isEmpty {
                            Text(item.kind == .app ? L("Ứng dụng chưa mở.") : L("Chưa thấy. Mở công cụ rồi bấm Kiểm tra lại."))
                                .foregroundStyle(.secondary)
                        }
                        ForEach(matches, id: \.pid) { m in
                            VStack(alignment: .leading, spacing: 1) {
                                Text("\(m.name) · pid \(m.pid)").font(.callout.weight(.medium))
                                Text(m.command).font(.caption.monospaced()).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            }
                        }
                    } header: {
                        HStack {
                            Text(L("Đang khớp: {0}", matches.count))
                            Spacer()
                            Button(L("Kiểm tra lại"), action: refresh).controlSize(.small)
                        }
                    }
                }
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button(L("Huỷ"), action: cancel).keyboardShortcut(.cancelAction)
                Button(isNew ? L("Thêm") : L("Lưu")) {
                    item.name = item.name.trimmingCharacters(in: .whitespaces)
                    item.process = item.process.trimmingCharacters(in: .whitespaces)
                    save(item)
                }
                .keyboardShortcut(.defaultAction).disabled(!valid)
            }
            .padding(16)
        }
        // as tall as what it holds: a bare Claude or Codex row needs only its name and icon
        .frame(width: 520, height: item.kind == .process ? 680 : item.kind == .app ? 540 : 270)
        .onAppear {
            refresh()
            runningNames = Self.userProcessNames()
        }
        .onChange(of: item.args) { refresh() }
    }

    /// Starts from the item's symbol; "Dùng biểu tượng mặc định" in the window brings the app's own icon back.
    func editIcon() {
        if item.icon == nil { item.icon = Badge(symbol: item.symbol, shape: .rounded) }
        editingIcon = true
    }

    func refresh() {
        if item.kind == .app {
            matches = NSWorkspace.shared.runningApplications.filter { $0.bundleIdentifier == item.bundleID }
                .map { ($0.processIdentifier, $0.localizedName ?? "", $0.bundleURL?.path ?? "") }
        } else {
            matches = item.roots(Proc.all(), argv: Proc.argv).prefix(20).map { p in (p.pid, p.name, Proc.argv(p.pid).joined(separator: " ")) }
        }
    }

    /// Names of processes that aren't part of macOS, for the picker.
    static func userProcessNames() -> [String] {
        var buf = [CChar](repeating: 0, count: 4096)
        let names = Proc.all().compactMap { p -> String? in
            guard proc_pidpath(p.pid, &buf, 4096) > 0 else { return nil }
            let path = String(cString: buf)
            return path.hasPrefix("/System/") || path.hasPrefix("/usr/libexec/") || path.hasPrefix("/usr/sbin/") || path.hasPrefix("/Library/Apple/") ? nil : p.name
        }
        return Array(Set(names)).sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }
}

/// Build & CI tools are detected, not added, so their icons are set here: the ones seen on this Mac first, then the rest.
struct BuildToolsBox: View {
    @ObservedObject var settings = AppSettings.shared
    @State private var editing: Tool?
    @State private var showAll = false
    struct Tool: Identifiable { let id: String }

    var tools: [String] {
        let seen = settings.seenTools
        return showAll ? seen + DevRules.knownTools.filter { !seen.contains($0) }.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending } : seen
    }

    var body: some View {
        Block {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Text("Build & CI").font(.headline)
                    Spacer()
                    Toggle(L("Hiện tất cả công cụ"), isOn: $showAll).toggleStyle(.checkbox)
                }
                Text(settings.seenTools.isEmpty && !showAll ? L("Chưa thấy lần build nào. Bật \"Hiện tất cả\" để đặt biểu tượng trước.")
                                                           : L("Không cần thêm: công cụ nào chạy là hiện. Bấm một công cụ để đổi biểu tượng."))
                    .font(.caption).foregroundStyle(.secondary)
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 128), spacing: 8)], spacing: 8) {
                    ForEach(tools, id: \.self) { t in
                        Button { open(t) } label: {
                            HStack(spacing: 8) {
                                JobIcon(job: Job(id: t, title: t, detail: "", working: true, icon: DevRules.appIcon(t), symbol: DevRules.defaultSymbol(t), tool: t),
                                        tint: .accentColor, size: 20)
                                Text(t).font(.callout).lineLimit(1)
                                Spacer(minLength: 0)
                                if settings.toolIcons[t] != nil { Circle().fill(Color.accentColor).frame(width: 5, height: 5).help(L("Đã đổi")) }
                            }
                            .padding(.horizontal, 8).padding(.vertical, 6)
                            .background(RoundedRectangle(cornerRadius: 8).fill(.quaternary.opacity(0.5)))
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .padding(6)
        }
        .sheet(item: $editing) { t in
            BadgeEditor(badge: Binding(get: { settings.toolIcons[t.id] ?? Badge() }, set: { settings.toolIcons[t.id] = $0 }),
                        title: L("Biểu tượng"), subtitle: t.id,
                        reset: { settings.toolIcons[t.id] = nil; editing = nil }, done: { editing = nil })
        }
    }

    func open(_ t: String) {
        if settings.toolIcons[t] == nil { settings.toolIcons[t] = Badge(symbol: DevRules.defaultSymbol(t), shape: .rounded) }
        editing = Tool(id: t)
    }
}

extension View {
    /// A List as tall as its rows (44 pt each) that never scrolls itself: the settings page around it already
    /// scrolls, and a second scroll bar inside it hid the last rows.
    func fittedList(rows: Int) -> some View {
        self.environment(\.defaultMinListRowHeight, 52)
            .scrollContentBackground(.hidden)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.quaternary.opacity(0.4)))
            .scrollDisabled(true)
            .scrollIndicators(.never)
            .frame(height: CGFloat(max(rows, 1)) * 52 + 12)
    }
}

/// The one block style of the control panel: a rounded, quiet fill, the same as a grouped form section.
extension View {
    func settingsCard(padding: CGFloat = 12) -> some View {
        self.padding(padding)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.quaternary.opacity(0.4)))
    }
}

struct Block<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View { VStack(alignment: .leading, spacing: 0) { content }.settingsCard(padding: 8) }
}
