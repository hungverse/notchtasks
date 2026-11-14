import AppKit
import SwiftUI
import ServiceManagement

// MARK: - Control panel (System Settings style)

extension Notification.Name { static let openPane = Notification.Name("notchtasks.openPane") }

enum Pane: String, CaseIterable, Identifiable {
    case overview, modules, permissions, agents, phone, fans, appearance, badges, store, sounds, behavior
    var id: String { rawValue }
    var title: String {
        switch self {
        case .overview: return L("Overview")
        case .modules: return L("Modules")
        case .agents: return L("Watching")
        case .phone: return L("Pairing")
        case .fans: return L("Fans & temperature")
        case .appearance: return L("Appearance")
        case .badges: return L("Icons & lock")
        case .store: return L("Icon Library")
        case .sounds: return L("Sound")
        case .behavior: return L("Behavior")
        case .permissions: return L("Permissions")
        }
    }
    var symbol: String {
        switch self {
        case .overview: return "square.grid.2x2.fill"
        case .modules: return "puzzlepiece.extension.fill"
        case .agents: return "eye.fill"
        case .phone: return "link"
        case .fans: return "fan.fill"
        case .appearance: return "paintbrush.fill"
        case .badges: return "star.square.on.square.fill"
        case .store: return "sparkles.rectangle.stack.fill"
        case .sounds: return "speaker.wave.2.fill"
        case .behavior: return "hand.tap.fill"
        case .permissions: return "lock.shield.fill"
        }
    }
}

struct SettingsRoot: View {
    @ObservedObject var model: Model
    @ObservedObject var permissions: Permissions
    @ObservedObject var updater: Updater
    @ObservedObject var settings = AppSettings.shared
    let showOnboarding: () -> Void
    @State private var pane: Pane? = .overview
    @State private var showReport = false
    @AppStorage(Track.optOutKey) private var analyticsOff = false

    var body: some View {
        NavigationSplitView {
            List(Pane.allCases.filter { $0 != .fans || Requirement.fan.met(on: Requirement.current) }, selection: $pane) { p in
                Label(p.title, systemImage: p.symbol).tag(p)
            }
            .navigationSplitViewColumnWidth(180)
            .safeAreaInset(edge: .bottom) { UpdateFooter(updater: updater) }
        } detail: {
            ScrollView {
                detail.padding(24).frame(maxWidth: 640, alignment: .topLeading).frame(maxWidth: .infinity)
            }
            .navigationTitle(pane?.title ?? "")
        }
        .frame(minWidth: 760, minHeight: 560)
        .onChange(of: pane) { _, p in Track.screen("settings_" + (p ?? .overview).rawValue) }
        .onAppear { permissions.refresh(); updater.checkIfDue() }
        .sheet(isPresented: $showReport) { MacReportSheet(sync: model.sync) }
        // grants are made in System Settings: re-read them when coming back from there
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in permissions.refresh() }
        // another page asks to show this one ("Thêm từ Kho biểu tượng")
        .onReceive(NotificationCenter.default.publisher(for: .openPane)) { n in
            if let p = (n.object as? String).flatMap(Pane.init(rawValue:)) { pane = p }
        }
        // debug: "pane-<name>" switches pages, for checking every page by screenshot
        .onReceive(DistributedNotificationCenter.default().publisher(for: .init("local.notchtasks.debug.expand"))) { n in
            if let o = n.object as? String, o.hasPrefix("pane-"), let p = Pane(rawValue: String(o.dropFirst(5))) { pane = p }
        }
    }

    @ViewBuilder var detail: some View {
        switch pane ?? .overview {
        case .overview: overview
        case .modules: modules
        case .agents: WatchListView(model: model)
        case .phone: PhonePane(sync: model.sync)
        case .fans:
            if Requirement.fan.met(on: Requirement.current) { FanPane(model: model) }
            else { Text(Requirement.fan.label).foregroundStyle(.secondary) }
        case .appearance: appearance
        case .badges: BadgesPane(model: model)
        case .store: IconStore(model: model)
        case .sounds: sounds
        case .behavior: behavior
        case .permissions: PermissionList(permissions: permissions, fanControl: model.fanControl)
        }
    }

    // MARK: Overview

    var nextSteps: [OnboardingGuide.NextStep] {
        OnboardingGuide.nextSteps(items: permissions.items, modules: Set(settings.activeModules),
                                  notifications: settings.systemNotifications,
                                  agents: AgentKind.allCases.filter { $0.present },
                                  installed: Set(AgentKind.allCases.filter { AgentAsks.installed($0) }),
                                  paired: !model.sync.phones.isEmpty)
    }

    var overview: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(spacing: 20) {
                NotchPreview().frame(width: 280)
                VStack(alignment: .leading, spacing: 4) {
                    Text("NotchTasks").font(.title.bold())
                    Text(L("The notch shows what's running, and stays quiet when nothing is.")).foregroundStyle(.secondary)
                }
            }
            HStack(spacing: 12) {
                stat("bolt.fill", "\(model.states.reduce(0) { $0 + $1.working })", L("Running"), .blue)
                stat("xmark.octagon.fill", "\(model.alerts.count)", L("CI errors"), .red)
                stat("lock.shield.fill", "\(permissions.grantedCount)/\(permissions.items.count)", L("Permissions"), .green)
            }
            if settings.onboardingDone && !nextSteps.isEmpty {
                VStack(alignment: .leading, spacing: 8) {
                    Text(L("Next steps")).font(.headline)
                    ForEach(nextSteps.indices, id: \.self) { index in
                        HStack(spacing: 10) {
                            switch nextSteps[index] {
                            case .permission(let id):
                                if let item = permissions.items.first(where: { $0.id == id }) {
                                    Image(systemName: item.symbol).foregroundStyle(.secondary)
                                    Text(item.title).font(.callout)
                                    Spacer()
                                    Button(item.state == .denied ? L("Open Settings") : item.state == .needsApp ? L("Open app") : L("Allow")) {
                                        permissions.request(item)
                                    }
                                }
                            case .agent(let kind):
                                Image(systemName: "sparkles").foregroundStyle(.secondary)
                                Text(kind.name + " · " + L("Answer agents on the notch")).font(.callout)
                                Spacer()
                                Button(L("On")) { pane = .agents }
                            case .phone:
                                Image(systemName: "iphone").foregroundStyle(.secondary)
                                Text(L("Pairing")).font(.callout)
                                Spacer()
                                Button(L("Pairing")) { pane = .phone }
                            }
                        }.controlSize(.small).settingsCard(padding: 10)
                    }
                }
            }
            Text(L("Modules")).font(.headline)
            ForEach(ModuleID.groups, id: \.title) { g in
                VStack(alignment: .leading, spacing: 6) {
                    Text(g.title.uppercased()).font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    // two even columns, switches lined up on the right edge of every cell
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 8), GridItem(.flexible())], spacing: 8) {
                        ForEach(g.modules) { m in moduleCell(m) }
                    }
                }
            }
            Button(L("See the guide"), action: showOnboarding)
            Button(L("Report a problem")) { showReport = true }
        }
    }

    func moduleCell(_ m: ModuleID) -> some View {
        let on = settings.on(m)
        return HStack(spacing: 10) {
            Image(systemName: m.symbol).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                .frame(width: 24, height: 24).background(RoundedRectangle(cornerRadius: 6).fill(on ? Color.accentColor : .gray.opacity(0.5)))
            Text(m.title).lineLimit(1).foregroundStyle(on ? .primary : .secondary)
            if let requirement = m.requirement, !requirement.met(on: Requirement.current) {
                Text(requirement.label).font(.caption2).foregroundStyle(.secondary).padding(4)
                    .background(Capsule().fill(.quaternary))
            }
            Spacer(minLength: 4)
            Toggle("", isOn: moduleBinding(m)).labelsHidden().toggleStyle(.switch).controlSize(.mini)
                .disabled(m.requirement.map { !$0.met(on: Requirement.current) } ?? false)
        }
        .padding(.horizontal, 10).frame(height: 40)
        .background(RoundedRectangle(cornerRadius: 10).fill(.quaternary.opacity(0.4)))
        .help(m.blurb)
    }

    func stat(_ symbol: String, _ value: String, _ label: String, _ tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Image(systemName: symbol).foregroundStyle(tint)
            Text(value).font(.title2.bold()).lineLimit(1)
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(.quaternary.opacity(0.4)))
    }

    func moduleBinding(_ m: ModuleID) -> Binding<Bool> {
        Binding(get: { settings.on(m) }, set: { settings.set(m, $0) })
    }

    func placementBinding(_ m: ModuleID) -> Binding<Placement> {
        Binding(get: { settings.placement(m) }, set: { settings.placements[m.rawValue] = $0 == .auto ? nil : $0.rawValue })
    }

    func colorBinding(_ m: ModuleID) -> Binding<Color> {
        Binding(get: { settings.color(for: m) ?? .gray }, set: { settings.moduleColors[m.rawValue] = $0.hex })
    }

    // MARK: Modules

    let listRow: CGFloat = 44

    var modules: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(L("Drag to reorder on the notch.")).font(.callout).foregroundStyle(.secondary)
                Spacer()
                // names for the controls on every row, which are icons or bare pickers otherwise
                Text(L("Position when collapsed")).frame(width: 92)
                Text(L("Color")).frame(width: 44)
                Text(L("On")).frame(width: 40)
            }
            .font(.caption).foregroundStyle(.secondary).padding(.horizontal, 16)
            List {
                ForEach(settings.modules) { m in
                    HStack(spacing: 12) {
                        Image(systemName: m.symbol).frame(width: 28, height: 28).foregroundStyle(.white)
                            .background(RoundedRectangle(cornerRadius: 7).fill(settings.on(m) ? Color.accentColor : .gray))
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 5) {
                                Text(m.title).font(.body.weight(.medium))
                                // the permission it needs, as a small lock instead of a sentence that won't fit
                                if let need = m.needs {
                                    Image(systemName: "lock.fill").font(.system(size: 9)).foregroundStyle(.secondary).help(L("Needs {0} permission", need))
                                }
                            }
                            Text(m.blurb).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        Picker("", selection: placementBinding(m)) {
                            ForEach(Placement.allCases, id: \.self) { Text($0.title).tag($0) }
                        }
                        .labelsHidden().frame(width: 92).help(L("Position when collapsed"))
                        ColorPicker("", selection: colorBinding(m), supportsOpacity: false).labelsHidden().help(L("Color"))
                            .contextMenu { Button(L("Back to default color")) { settings.moduleColors[m.rawValue] = nil } }
                        if let requirement = m.requirement, !requirement.met(on: Requirement.current) {
                            Text(requirement.label).font(.caption2).foregroundStyle(.secondary)
                                .padding(4).background(Capsule().fill(.quaternary))
                        }
                        Toggle("", isOn: moduleBinding(m)).labelsHidden().toggleStyle(.switch).controlSize(.small)
                            .disabled(m.requirement.map { !$0.met(on: Requirement.current) } ?? false)
                    }
                    .frame(height: listRow)
                }
                .onMove { settings.modules.move(fromOffsets: $0, toOffset: $1) }
            }
            .fittedList(rows: settings.modules.count)
            HStack(spacing: 12) {
                Image(systemName: "fan.fill").frame(width: 28)
                Text(L("Fans & temperature"))
                Spacer()
                if !Requirement.fan.met(on: Requirement.current) {
                    Text(Requirement.fan.label).font(.caption2).foregroundStyle(.secondary)
                        .padding(4).background(Capsule().fill(.quaternary))
                }
                Toggle("", isOn: Binding(get: { Requirement.fan.met(on: Requirement.current) && model.fanControl.enabled },
                                         set: { if $0 { pane = .fans } else { model.fanControl.disable() } }))
                    .labelsHidden().toggleStyle(.switch).controlSize(.small)
                    .disabled(!Requirement.fan.met(on: Requirement.current))
                    .accessibilityLabel(L("Fans & temperature"))
            }.settingsCard()
        }
    }

    // MARK: Appearance

    var appearance: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 16) {
                NotchPreview().frame(width: 300)
                Spacer()
                Button { testToast() } label: { Label(L("Test alert"), systemImage: "bell.and.waves.left.and.right") }
                    .buttonStyle(.borderedProminent)
            }
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12), GridItem(.flexible())], spacing: 12) {
                ForEach(Theme.all) { t in ThemeTile(theme: t) { settings.apply(t) } }
            }
            Form {
                Section {
                    Picker(L("Background"), selection: $settings.background) {
                        ForEach(NotchBackground.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    if settings.background == .gradient {
                        ColorPicker(L("Top color"), selection: hexBinding(\.gradientA), supportsOpacity: false)
                        ColorPicker(L("Bottom color"), selection: hexBinding(\.gradientB), supportsOpacity: false)
                    }
                    Picker(L("Size"), selection: $settings.size) {
                        Text(L("Compact")).tag(NotchSize.compact)
                        Text(L("Large")).tag(NotchSize.regular)
                    }
                    .pickerStyle(.segmented)
                    Picker(L("Motion"), selection: $settings.motion) {
                        ForEach(Motion.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                } header: { Text("Notch") } footer: { Text(lookBlurb) }
                Section {
                    Picker(L("Style"), selection: $settings.toastStyle) {
                        ForEach(ToastStyle.allCases, id: \.self) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                } header: { Text(L("Notifications")) } footer: { Text(settings.toastStyle.blurb) }
                Section(L("Color")) {
                    Picker(L("Accent color"), selection: $settings.accent) {
                        ForEach(AppSettings.accents, id: \.name) { a in
                            HStack {
                                Circle().fill(a.name == "custom" ? (Color(hex: settings.customAccent) ?? .gray) : (a.color ?? .gray)).frame(width: 10, height: 10)
                                Text(a.title)
                            }
                            .tag(a.name)
                        }
                    }
                    if settings.accent == "custom" {
                        ColorPicker(L("Custom color"), selection: hexBinding(\.customAccent), supportsOpacity: false)
                    }
                }
                Section {
                    HStack {
                        Button(L("Export…"), action: exportProfile)
                        Button(L("Import…"), action: importProfile)
                    }
                } header: { Text(L("Back up settings")) } footer: { Text(L("All your customization in one .json file.")) }
            }
            .formStyle(.grouped)
            .padding(.horizontal, -20)
        }
    }

    func hexBinding(_ key: ReferenceWritableKeyPath<AppSettings, String>) -> Binding<Color> {
        Binding(get: { Color(hex: settings[keyPath: key]) ?? .gray }, set: { settings[keyPath: key] = $0.hex })
    }

    var lookBlurb: String {
        model.hasNotch ? L("This screen has a notch: the notch hugs the camera.") : L("This screen has no notch: shown as a floating Dynamic Island.")
    }

    func testToast() {
        model.announce(Toast(symbol: "checkmark.circle.fill", tint: settings.accentColor ?? .green, title: L("Task finished"),
                             subtitle: L("Test alert"), event: .taskDone), notify: false)
    }

    func exportProfile() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "NotchTasks.json"
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? enc.encode(settings.profile).write(to: url)
    }

    func importProfile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url,
              let data = try? Data(contentsOf: url), let p = try? JSONDecoder().decode(Profile.self, from: data) else { return }
        settings.profile = p
    }

    // MARK: Sounds

    var sounds: some View {
        Form {
            Section {
                Toggle(L("Sound"), isOn: $settings.sounds)
                LabeledContent(L("Volume")) { Slider(value: $settings.volume, in: 0...1) }
                Picker(L("Sound pack"), selection: Binding(get: { currentPack }, set: { if !$0.isEmpty { settings.applyPack($0) } })) {
                    if currentPack.isEmpty { Text(L("Yours")).tag("") }
                    ForEach(SoundPack.all, id: \.name) { Text($0.name).tag($0.name) }
                }
                LabeledContent(L("Back up, share")) {
                    HStack {
                        Button(L("Export pack…"), action: exportSounds)
                        Button(L("Import pack…"), action: importSounds)
                    }
                }
            } footer: {
                Text(L("Sounds are kept separately, changing the look won't lose them. Export a pack to take it to another computer."))
            }
            Section(L("Events")) {
                ForEach(SoundEvent.allCases) { e in SoundRow(event: e) }
            }
            .disabled(!settings.sounds)
        }
        .formStyle(.grouped)
        .padding(.horizontal, -20).padding(.top, -20)
    }

    /// The built-in pack the current sounds match exactly, or "" once the user changed any of them.
    var currentPack: String {
        SoundPack.all.first { p in SoundEvent.allCases.allSatisfy { (settings.soundMap[$0.rawValue] ?? SoundPack.all[0].map[$0] ?? "") == (p.map[$0] ?? "") } }?.name ?? ""
    }

    func exportSounds() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = L("NotchTasks Sounds.notchsounds")
        panel.allowedContentTypes = [SoundPackFile.type]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let pack = SoundPackFile(map: settings.soundMap, volume: settings.volume)
        do {
            try JSONEncoder().encode(pack).write(to: url)
        } catch { alert(L("Couldn't export the sound pack"), error.localizedDescription) }
    }

    func importSounds() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [SoundPackFile.type, .json]
        panel.message = L("Choose a sound pack file (.notchsounds)")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        if let why = settings.importSounds(url) { alert(L("Couldn't import the sound pack"), why) }
    }

    func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.runModal()
    }

    // MARK: Behavior

    var behavior: some View {
        Form {
            Section(L("Opening the notch")) {
                Picker(L("How to open"), selection: $settings.expandMode) {
                    Text(L("Hover")).tag(ExpandMode.hover)
                    Text(L("Click")).tag(ExpandMode.click)
                }
                .pickerStyle(.segmented)
                if settings.expandMode == .hover {
                    LabeledContent(L("Delay {0} ms", Int(settings.hoverDelay * 1000))) {
                        Slider(value: $settings.hoverDelay, in: 0...1, step: 0.05)
                    }
                }
                Toggle(L("Haptic feedback"), isOn: $settings.haptics)
                Toggle(L("CPU and RAM when expanded"), isOn: $settings.systemLoad)
            }
            Section(L("Notifications")) {
                Toggle(L("System notifications"), isOn: $settings.systemNotifications)
                Stepper(L("Say done when it ran {0} sec or more", Int(settings.doneAfter)), value: $settings.doneAfter, in: 5...600, step: 5)
                LabeledContent(L("Warn about limits at {0}%", Int(settings.limitWarn))) {
                    Slider(value: $settings.limitWarn, in: 50...100, step: 5)
                }
            }
            Section(L("Other")) {
                LanguagePicker()
                Toggle(L("Menu bar icon"), isOn: $settings.menuBarIcon)
                Toggle(L("Send anonymous usage statistics"), isOn: Binding(get: { !analyticsOff }, set: { Track.enabled = $0 }))
            }
        }
        .formStyle(.grouped)
        .padding(.horizontal, -20).padding(.top, -20)
    }
}

struct UpdateFooter: View {
    @ObservedObject var updater: Updater

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            switch updater.footerState {
            case .checking: Text(L("Checking…")).foregroundStyle(.secondary)
            case .current:
                Button(updater.shortVersion) { updater.check(manual: true) }
                    .buttonStyle(.plain).foregroundStyle(.secondary)
                    .help(L("Check now"))
            case .upToDate: Text(L("Already up to date")).foregroundStyle(.secondary)
            case .offered, .failed:
                if updater.installing {
                    if let progress = updater.progress {
                        ProgressView(L("Downloading… {0}%", Int((progress * 100).rounded())), value: progress)
                    } else { ProgressView(L("Verifying…")) }
                } else {
                    Button {
                        updater.install()
                    } label: {
                        Label(updater.error == nil ? L("Update {0}", updater.offered?.version ?? "") : L("Try again"), systemImage: "arrow.down")
                    }.buttonStyle(.borderedProminent)
                }
                if let offered = updater.offered {
                    Text(offered.note(for: L10n.code)).lineLimit(2).foregroundStyle(.secondary)
                        .help(offered.note(for: L10n.code))
                }
                if let error = updater.error { Text(error).lineLimit(1).foregroundStyle(.red).help(error) }
            }
        }
        .font(.caption).padding(10).frame(maxWidth: .infinity, alignment: .leading)
        .background(.regularMaterial)
    }
}

// MARK: - Permission list (control panel + onboarding)

struct PermissionList: View {
    @ObservedObject var permissions: Permissions
    @ObservedObject var settings = AppSettings.shared
    @ObservedObject var fanControl: FanControl
    var compact = false
    var activeOnly = false  // onboarding: only what the chosen modules need

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if !compact {
                Text(L("Only grant permissions for features you use. Turn a feature off completely on the Modules page."))
                    .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            let shown = permissions.items.filter { !activeOnly || ($0.module.map(settings.on) ?? true) }
            if shown.isEmpty { Text(L("Checking…")).settingsCard() }
            ForEach([PermItem.Group.needed, .granted, .unused], id: \.self) { group in
                let rows = shown.filter { $0.group(featureOn: $0.module.map(settings.on) ?? true) == group }
                let showsFan = Requirement.fan.met(on: Requirement.current) && !activeOnly && fanControl.enabled &&
                    (group == (fanControl.available ? .granted : .needed))
                if !rows.isEmpty || showsFan {
                    VStack(alignment: .leading, spacing: 0) {
                        Text(group == .needed ? L("Needed now") : group == .granted ? L("Granted") : L("Not in use"))
                            .font(.headline).padding(.horizontal, 6).padding(.bottom, 4)
                        ForEach(rows) { p in
                            if p.id != rows.first?.id { Divider() }
                            row(p)
                        }
                        if showsFan {
                            if !rows.isEmpty { Divider() }
                            fanHelperRow(fanControl)
                        }
                    }.settingsCard(padding: 6)
                }
            }
            HStack {
                Button(L("Refresh")) { permissions.refresh() }
                if !compact {
                    Text(L("Some permissions can only be checked while the related app is open.")).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .onAppear { permissions.refresh() }
        // grants are made in System Settings: re-read them when coming back from there
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in permissions.refresh() }
    }

    func fanHelperRow(_ control: FanControl) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "fan.fill").frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 2) {
                Text(L("Fan helper")).font(.body.weight(.medium))
                Text(L("Fan control can damage hardware when used incorrectly.")).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Text(control.available ? L("Allowed") : control.needsApproval ? L("Not granted") : L("Not checked"))
                .font(.caption).foregroundStyle(.secondary)
            if control.needsApproval { Button(L("Open System Settings")) { SMAppService.openSystemSettingsLoginItems() }.controlSize(.small) }
            else if !control.available { Button(L("Enable fan helper")) { control.register() }.controlSize(.small) }
            else { Button(L("Open System Settings")) { SMAppService.openSystemSettingsLoginItems() }.controlSize(.small) }
        }.padding(.horizontal, 6).padding(.vertical, 8)
    }

    func row(_ p: PermItem) -> some View {
        HStack(spacing: 12) {
            Image(systemName: p.symbol).foregroundStyle(.white).frame(width: 30, height: 30)
                .background(RoundedRectangle(cornerRadius: 8).fill(p.state.color.gradient))
            VStack(alignment: .leading, spacing: 2) {
                Text(p.title).font(.body.weight(.medium))
                Text(p.detail).font(.caption).foregroundStyle(.secondary).lineLimit(compact ? 1 : 2)
            }
            Spacer()
            Text(p.state.label).font(.caption.weight(.semibold)).foregroundStyle(p.state.color)
                .padding(.horizontal, 8).padding(.vertical, 3).background(Capsule().fill(p.state.color.opacity(0.15)))
            if p.state == .granted {
                Button(L("Revoke")) { permissions.revoke(p) }.controlSize(.small)
            } else {
                if p.id == "accessibility" {
                    // an entry left by an older build looks switched on in Settings but no longer matches this app
                    Button(L("Reset")) { permissions.resetAndAsk(p) }.controlSize(.small)
                        .help(L("Remove NotchTasks's old entry in Settings, then ask again"))
                }
                if p.state != .checking {
                    Button(p.state == .denied ? L("Open Settings") : p.state == .needsApp ? L("Open app") : L("Allow")) { permissions.request(p) }
                        .controlSize(.small).buttonStyle(.borderedProminent)
                }
            }
        }
        .padding(.horizontal, 6).padding(.vertical, 8)
    }
}

// MARK: - Notch preview (live on the guide, sample in settings)

struct NotchPreview: View {
    @ObservedObject var settings = AppSettings.shared
    var model: Model? = nil
    var body: some View {
        let size = settings.iconSize
        let tint = settings.accentColor ?? .orange
        let working = model?.states.reduce(0) { $0 + $1.working } ?? 2
        ZStack(alignment: .top) {
            RoundedRectangle(cornerRadius: 10).fill(LinearGradient(colors: [.indigo.opacity(0.7), .purple.opacity(0.5)], startPoint: .top, endPoint: .bottom))
                .frame(height: 70)
            HStack(spacing: 0) {
                HStack(spacing: 6) {
                    if working > 0 {
                        Image(systemName: "sparkle").font(.system(size: size * 0.7, weight: .bold)).foregroundStyle(tint)
                            .frame(width: size, height: size).background(RoundedRectangle(cornerRadius: 4).fill(.white.opacity(0.1)))
                        Text("\(working)").font(.system(size: 11, weight: .bold, design: .rounded)).foregroundStyle(tint)
                        Bars(color: settings.accentColor ?? .green)
                    }
                }
                .frame(width: 80, alignment: .leading)
                Color.clear.frame(width: 110)
                HStack(spacing: 4) {
                    if model == nil || model?.alerts.isEmpty == false {
                        Image(systemName: "hammer.fill").font(.system(size: size * 0.6, weight: .bold)).foregroundStyle(tint)
                        ProgressRing(value: 0.6, tint: tint)
                    }
                }
                .frame(width: 80, alignment: .trailing)
            }
            .padding(.horizontal, 14)
            .frame(height: 30)
            .background(.black)
            .clipShape(NotchShape(top: 8, bottom: 10))
        }
        .environment(\.colorScheme, .dark)
    }
}

// MARK: - Theme tile & sound row

struct ThemeTile: View {
    let theme: Theme
    let apply: () -> Void
    @ObservedObject var settings = AppSettings.shared

    var selected: Bool {
        settings.background == theme.background && settings.toastStyle == theme.toast && settings.accent == theme.accent
            && settings.motion == theme.motion && (theme.accent != "custom" || settings.customAccent == theme.customAccent)
    }

    var body: some View {
        let accent = theme.accent == "custom" ? (Color(hex: theme.customAccent) ?? .orange)
            : AppSettings.accents.first { $0.name == theme.accent }?.color ?? .orange
        Button(action: apply) {
            VStack(alignment: .leading, spacing: 8) {
                ZStack(alignment: .top) {
                    RoundedRectangle(cornerRadius: 10).fill(
                        theme.background == .gradient
                            ? AnyShapeStyle(LinearGradient(colors: [Color(hex: theme.gradient.0) ?? .indigo, Color(hex: theme.gradient.1) ?? .black],
                                                           startPoint: .topLeading, endPoint: .bottomTrailing))
                            : AnyShapeStyle(Color(white: theme.background == .glass ? 0.35 : 0.12)))
                    Capsule().fill(.black).frame(width: 90, height: 18)
                        .overlay(HStack(spacing: 4) {
                            Circle().fill(accent).frame(width: 7)
                            Spacer()
                            Capsule().fill(accent).frame(width: 14, height: 4)
                        }.padding(.horizontal, 8))
                        .overlay(theme.background == .glow ? Capsule().stroke(accent, lineWidth: 1).shadow(color: accent, radius: 4) : nil)
                        .padding(.top, 6)
                }
                .frame(height: 62)
                HStack {
                    Text(theme.title).font(.subheadline.weight(.semibold))
                    Spacer()
                    if selected { Image(systemName: "checkmark.circle.fill").foregroundStyle(Color.accentColor) }
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 14).fill(.quaternary.opacity(selected ? 0.7 : 0.3)))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(selected ? Color.accentColor : .clear, lineWidth: 2))
        }
        .buttonStyle(.plain)
    }
}

struct SoundRow: View {
    let event: SoundEvent
    @ObservedObject var settings = AppSettings.shared

    var current: String { settings.soundMap[event.rawValue] ?? "" }

    var body: some View {
        HStack {
            Label(event.title, systemImage: event.symbol)
            Spacer()
            Menu(Sounds.displayName(current)) {
                Button(L("No")) { settings.soundMap[event.rawValue] = "" }
                Divider()
                ForEach(Sounds.system, id: \.self) { name in
                    Button(name) {
                        settings.soundMap[event.rawValue] = name
                        Sounds.play(name: name, volume: settings.volume)
                    }
                }
                Divider()
                Button(L("Choose your file…"), action: pickFile)
            }
            .frame(width: 170)
            Button { Sounds.play(name: current, volume: settings.volume) } label: { Image(systemName: "play.circle.fill") }
                .buttonStyle(.borderless).disabled(current.isEmpty).help(L("Preview"))
        }
    }

    func pickFile() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.audio]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        // a copy in the app's folder: the original may be moved or deleted later
        let path = SoundPackFile.keep(url) ?? url.path
        settings.soundMap[event.rawValue] = path
        Sounds.play(name: path, volume: settings.volume)
    }
}
