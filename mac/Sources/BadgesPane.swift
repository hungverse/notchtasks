import AppKit
import SwiftUI
import UniformTypeIdentifiers

// MARK: - Fixed icons and the lock screen notch, in one pane, each with a live preview

struct BadgesPane: View {
    @ObservedObject var model: Model
    @ObservedObject var settings = AppSettings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            Card(title: L("Màn hình chính"), subtitle: L("Biểu tượng luôn hiện trên notch thu nhỏ.")) {
                ScreenPreview(model: model, lockScreen: false)
                BadgeBoard(badges: $settings.badges)
            }

            Card(title: L("Màn hình khoá"), subtitle: L("Chỉ để xem: không mở rộng, không bấm được."), isOn: $settings.lockScreen) {
                if settings.lockScreen {
                    ScreenPreview(model: model, lockScreen: true)
                    VStack(spacing: 0) {
                        option(.lock) { sidePicker($settings.lockIconRight) }
                        Divider()
                        option(.battery) { sidePicker(Binding(get: { !settings.lockBatteryLeft }, set: { settings.lockBatteryLeft = !$0 })) }
                        Divider()
                        option(.activity) { EmptyView() }
                        Divider()
                        option(.badge) {
                            Picker("", selection: $settings.lockOwnBadges) {
                                Text(L("Như màn hình chính")).tag(false)
                                Text(L("Riêng")).tag(true)
                            }
                            .pickerStyle(.segmented).labelsHidden().fixedSize()
                        }
                        Divider()
                        HStack(spacing: 10) {
                            Image(systemName: "lock.trianglebadge.exclamationmark").frame(width: 18).foregroundStyle(.secondary)
                            VStack(alignment: .leading, spacing: 1) {
                                Text(L("Báo khi mở khoá sai"))
                                Text(L("Notch rung đỏ khi sai mật khẩu hoặc vân tay. Mở máy xong sẽ báo có mấy lần sai."))
                                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                            }
                            Spacer()
                            Toggle("", isOn: $settings.lockAuth).toggleStyle(.switch).labelsHidden()
                        }
                        .padding(.vertical, 7)
                    }
                    if settings.lockShows.contains(.badge) && settings.lockOwnBadges {
                        BadgeBoard(badges: $settings.lockBadges)
                    }
                }
            }
        }
        .controlSize(.small)
    }

    func option(_ c: LockContent, @ViewBuilder trailing: () -> some View) -> some View {
        let on = settings.lockShows.contains(c)
        return HStack(spacing: 10) {
            Image(systemName: c.symbol).frame(width: 18).foregroundStyle(.secondary)
            Text(c.title)
            Spacer()
            if on { trailing() }
            Toggle("", isOn: Binding(get: { on }, set: { v in if v { settings.lockShows.insert(c) } else { settings.lockShows.remove(c) } }))
                .toggleStyle(.switch).labelsHidden()
        }
        .padding(.vertical, 7)
    }

    func sidePicker(_ right: Binding<Bool>) -> some View {
        Picker("", selection: right) {
            Text(L("Trái")).tag(false)
            Text(L("Phải")).tag(true)
        }
        .pickerStyle(.segmented).labelsHidden().fixedSize()
    }
}

/// A titled rounded block, optionally switched on and off from its header.
struct Card<Content: View>: View {
    let title: String
    let subtitle: String
    var isOn: Binding<Bool>?
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.headline)
                    Text(subtitle).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                if let isOn { Toggle("", isOn: isOn).toggleStyle(.switch).labelsHidden() }
            }
            content
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.quaternary.opacity(0.4)))
    }
}

/// The real notch view on a fake desktop or lock screen, so every change shows up as it will look.
struct ScreenPreview: View {
    @ObservedObject var model: Model
    let lockScreen: Bool

    var body: some View {
        ZStack(alignment: .top) {
            LinearGradient(colors: lockScreen ? [Color(red: 0.16, green: 0.2, blue: 0.42), Color(red: 0.45, green: 0.25, blue: 0.5)]
                                              : [Color(white: 0.85), Color(white: 0.7)],
                           startPoint: .top, endPoint: .bottom)
            if lockScreen {
                TimelineView(.everyMinute) { ctx in
                    Text(ctx.date.formatted(date: .omitted, time: .shortened))
                        .font(.system(size: 30, weight: .semibold, design: .rounded)).foregroundStyle(.white.opacity(0.9))
                }
                .padding(.top, model.notch.height + 12)
            }
            NotchView(model: model, lockScreen: lockScreen, preview: true)
                .allowsHitTesting(false)
        }
        .frame(maxWidth: .infinity)
        .frame(height: lockScreen ? 100 : 56)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

/// Icons laid out as they sit on the notch: left side, the camera, right side. + adds one, a click edits, a drag swaps.
struct BadgeBoard: View {
    @Binding var badges: [Badge]
    @State private var editing: EditingID?
    @State private var dropTarget: String?  // tile id or "side<right>" under a drag
    struct EditingID: Identifiable { let id: UUID }

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                side(right: false).frame(maxWidth: .infinity, alignment: .trailing)
                RoundedRectangle(cornerRadius: 6, style: .continuous).fill(.black).frame(width: 64, height: 22)
                    .overlay(Circle().fill(Color(white: 0.15)).frame(width: 6))
                side(right: true).frame(maxWidth: .infinity, alignment: .leading)
            }
            Text(L("Bấm + để thêm, bấm để sửa, kéo để đổi chỗ. Mỗi bên tối đa {0}{1}", Badge.perSide, Badge.pro ? "." : L(", Pro được 4.")))
                .font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 8) {
                Button { NotificationCenter.default.post(name: .openPane, object: Pane.store.rawValue) } label: {
                    Label(L("Thêm từ Kho biểu tượng"), systemImage: "sparkles.rectangle.stack")
                }
                if badges.contains(where: \.animated) {
                    Button { Animations.sync() } label: { Label(L("Đồng bộ chuyển động"), systemImage: "arrow.triangle.2.circlepath") }
                        .help(L("Cho mọi biểu tượng động chạy lại từ đầu cùng lúc"))
                }
            }
            .controlSize(.small)
            .padding(.top, 2)
        }
        .onReceive(DistributedNotificationCenter.default().publisher(for: .init("local.notchtasks.debug.expand"))) { n in
            if n.object as? String == "edit-badge", let b = badges.first { editing = EditingID(id: b.id) }
        }
        .sheet(item: $editing) { e in
            BadgeEditor(badge: binding(e.id), title: L("Biểu tượng cố định"),
                        subtitle: binding(e.id).wrappedValue.right ? L("Bên phải notch") : L("Bên trái notch"),
                        discard: Self.discard, delete: { remove(e.id) }, done: { editing = nil })
        }
    }

    /// Up to Badge.perSide icons per side, the add slot on the outside.
    func side(right: Bool) -> some View {
        let ids = badges.filter { $0.right == right }.map(\.id)
        return HStack(spacing: 6) {
            if !right && ids.count < Badge.perSide { addSlot(right: false) }
            ForEach(ids, id: \.self) { tile($0) }
            if right && ids.count < Badge.perSide { addSlot(right: true) }
        }
    }

    func addSlot(right: Bool) -> some View {
        Button { add(Badge(symbol: "star.fill", right: right)) } label: {
            Image(systemName: "plus").font(.system(size: 13, weight: .semibold)).foregroundStyle(.secondary)
                .frame(width: 34, height: 34)
                .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.tertiary, style: StrokeStyle(lineWidth: 1, dash: [3])))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(right ? L("Thêm bên phải") : L("Thêm bên trái"))
        .dropDestination(for: String.self) { items, _ in
            guard let id = items.first.flatMap(UUID.init(uuidString:)) else { return false }
            badges.moveBadge(id, right: right)
            return true
        } isTargeted: { on in let key = "side\(right)"; dropTarget = on ? key : (dropTarget == key ? nil : dropTarget) }
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.accentColor, lineWidth: dropTarget == "side\(right)" ? 2 : 0))
    }

    /// Looked up by id, never by index: the editor may still read it for a moment after the icon is deleted.
    func binding(_ id: UUID) -> Binding<Badge> {
        Binding(get: { badges.first { $0.id == id } ?? Badge() },
                set: { v in if let i = badges.firstIndex(where: { $0.id == id }) { badges[i] = v } })
    }

    func tile(_ id: UUID) -> some View {
        Button { editing = EditingID(id: id) } label: {
            NotchBadge(badge: binding(id).wrappedValue, size: 22)
                .padding(6)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.black))
        }
        .buttonStyle(.plain)
        // press and drag onto another icon to swap them, or onto an empty + to move there
        .draggable(id.uuidString) { NotchBadge(badge: binding(id).wrappedValue, size: 26) }
        .dropDestination(for: String.self) { items, _ in
            guard let other = items.first.flatMap(UUID.init(uuidString:)) else { return false }
            badges.swapBadges(other, id)
            return true
        } isTargeted: { on in dropTarget = on ? id.uuidString : (dropTarget == id.uuidString ? nil : dropTarget) }
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color.accentColor, lineWidth: dropTarget == id.uuidString ? 2 : 0))
    }

    func add(_ b: Badge) {
        guard badges.filter({ $0.right == b.right }).count < Badge.perSide else { return }
        badges.append(b)
        editing = EditingID(id: b.id)
    }

    func remove(_ id: UUID) {
        editing = nil
        // after the sheet has closed, so nothing on screen still points at it
        DispatchQueue.main.async {
            let image = badges.first { $0.id == id }?.image
            badges.removeAll { $0.id == id }
            Self.discard(image)
        }
    }

    /// Deletes a copied picture once no badge on either screen uses it (every pick makes its own copy).
    @MainActor static func discard(_ path: String?) {
        guard let path else { return }
        let s = AppSettings.shared
        if !(s.badges + s.lockBadges).contains(where: { $0.image == path }) { try? FileManager.default.removeItem(atPath: path) }
    }
}

/// The icon window: where the icon comes from on the left, its content in the middle, its style below, a live preview on top.
/// Used for fixed icons, watched apps and detected build tools.
struct BadgeEditor: View {
    @Binding var badge: Badge
    var title = L("Biểu tượng")
    var subtitle: String?
    var discard: (String?) -> Void = { _ in }
    var delete: (() -> Void)?
    var reset: (() -> Void)?   // back to the app's own icon (watched items, tools)
    var done: () -> Void

    static let presets = ["apple.logo", "star.fill", "heart.fill", "bolt.fill", "flame.fill", "pawprint.fill",
                          "face.smiling", "leaf.fill", "moon.fill", "sun.max.fill", "music.note", "gamecontroller.fill",
                          "cup.and.saucer.fill", "sparkles", "hammer.fill", "terminal.fill", "cpu.fill", "brain.head.profile",
                          "wrench.and.screwdriver.fill", "shippingbox.fill", "server.rack", "cloud.fill", "rocket.fill", "gearshape.fill"]
    static let swatches = ["#FFFFFF", "#000000", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#0A84FF", "#BF5AF2"]
    static let emoji = ["🔥", "🚀", "⚡️", "🤖", "🐱", "🐶", "💻", "☕️", "✅", "🎯", "🌟", "💡", "🛠️", "🍀", "🎧", "🧠"]

    enum Content: String, CaseIterable {
        case symbol = "Biểu tượng", text = "Chữ, emoji", image = "Ảnh", motion = "Ảnh động"  // shown through L()
        var icon: String {
            switch self {
            case .symbol: return "star.square.fill"
            case .text: return "textformat"
            case .image: return "photo.fill"
            case .motion: return "sparkles.rectangle.stack.fill"
            }
        }
        var hint: String {
            switch self {
            case .symbol: return L("Hơn 8.000 biểu tượng của Apple")
            case .text: return L("Vài ký tự hoặc emoji")
            case .image: return L("Ảnh của bạn")
            case .motion: return L("Nhân vật chuyển động, GIF")
            }
        }
    }
    var content: Content { badge.animated ? .motion : badge.image != nil ? .image : badge.text != nil ? .text : .symbol }
    @State private var shown: Content?  // the tab on screen; switching tabs alone changes nothing until something is picked
    var tab: Content { shown ?? content }
    @State private var symbolQuery = ""
    @State private var symbolCategory = "suggested"
    @State private var motionCategory = SpriteLibrary.categories.first ?? ""
    @State private var dropping = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            HStack(spacing: 0) {
                sidebar
                Divider()
                contentArea.frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top).padding(14)
            }
            Divider()
            styleBar.padding(14)
            Divider()
            footer.padding(14)
        }
        .frame(width: 680, height: 560)
        .onAppear { if let id = badge.sprite, let c = SpriteLibrary.entry(id)?.category { motionCategory = c } }
    }

    // MARK: Parts

    var header: some View {
        HStack(spacing: 14) {
            NotchBadge(badge: badge, size: 40)
                .frame(width: 64, height: 56)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.black))
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(.title3.weight(.semibold))
                if let subtitle { Text(subtitle).foregroundStyle(.secondary) }
            }
            Spacer()
            // at its real size, next to the camera, as it will sit on the notch
            HStack(spacing: 8) {
                NotchBadge(badge: badge, size: AppSettings.shared.iconSize)
                Capsule().fill(Color(white: 0.12)).frame(width: 46, height: 6)
            }
            .padding(.horizontal, 12).frame(height: 30)
            .background(UnevenRoundedRectangle(bottomLeadingRadius: 12, bottomTrailingRadius: 12).fill(.black))
            .help(L("Trên notch"))
        }
        .padding(16)
    }

    var sidebar: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Content.allCases, id: \.self) { c in
                Button { open(c) } label: {
                    HStack(spacing: 10) {
                        Image(systemName: c.icon).font(.system(size: 15)).frame(width: 22)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(L(c.rawValue)).font(.body.weight(.medium))
                            Text(c.hint).font(.caption2).foregroundStyle(tab == c ? .white.opacity(0.8) : .secondary).lineLimit(2)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 8)
                    .foregroundStyle(tab == c ? .white : .primary)
                    .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(tab == c ? Color.accentColor : .clear))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer()
        }
        .padding(10)
        .frame(width: 180)
    }

    @ViewBuilder var contentArea: some View {
        switch tab {
        case .symbol:
            SymbolPicker(selection: Binding(get: { content == .symbol ? badge.symbol : "" }, set: useSymbol),
                         query: $symbolQuery, category: $symbolCategory)
        case .text:
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    TextField("", text: Binding(get: { badge.text ?? "" }, set: { useText(String($0.prefix(8))) }), prompt: Text(L("Gõ tối đa 8 ký tự")))
                        .textFieldStyle(.roundedBorder).font(.system(size: 18)).controlSize(.large)
                    Button { NSApp.orderFrontCharacterPalette(nil) } label: { Label("Emoji", systemImage: "face.smiling") }
                        .controlSize(.large)
                }
                Text(L("Chọn nhanh")).font(.caption).foregroundStyle(.secondary)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 8), spacing: 8) {
                    ForEach(Self.emoji, id: \.self) { e in
                        Button { useText(e) } label: {
                            Text(e).font(.system(size: 22)).frame(maxWidth: .infinity).frame(height: 40)
                                .background(RoundedRectangle(cornerRadius: 8).fill(badge.text == e ? Color.accentColor.opacity(0.4) : Color.primary.opacity(0.06)))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        case .image:
            dropZone(text: content == .image ? L("Kéo ảnh khác vào đây") : L("Kéo ảnh vào đây"), button: L("Chọn ảnh…"), types: [.image]) { path in
                replaceImage(path); badge.sprite = nil; badge.frames = nil; badge.fps = nil; badge.text = nil
            }
        case .motion:
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Picker("", selection: $motionCategory) { ForEach(SpriteLibrary.categories, id: \.self) { Text(L($0)).tag($0) } }
                        .pickerStyle(.segmented).labelsHidden()
                    // the full shop window, with search and previews at a bigger size
                    Button {
                        done()
                        NotificationCenter.default.post(name: .openPane, object: Pane.store.rawValue)
                    } label: { Label("Kho", systemImage: "sparkles.rectangle.stack") }
                    .help(L("Mở Kho biểu tượng"))
                }
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 74), spacing: 8)], spacing: 8) {
                        ForEach(SpriteLibrary.all.filter { $0.category == motionCategory }) { e in
                            Button { useSprite(e.id) } label: {
                                VStack(spacing: 4) {
                                    Group { if let f = Animations.builtIn(e.id) { AnimatedArt(frames: f, size: 36) } }.frame(height: 40)
                                    Text(e.name).font(.caption2).lineLimit(1).foregroundStyle(.white.opacity(0.85))
                                }
                                .frame(maxWidth: .infinity).padding(.vertical, 8)
                                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.black.opacity(0.75)))
                                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(Color.accentColor, lineWidth: badge.sprite == e.id ? 2.5 : 0))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                HStack {
                    Button { if let path = Self.pickImage(types: [.gif, .png]) { useAnimation(path) } } label: {
                        Label(L("GIF hoặc spritesheet của bạn…"), systemImage: "film.stack")
                    }
                    if badge.sprite == nil, let n = badge.frames {
                        Spacer()
                        Stepper("\(n) khung", value: Binding(get: { n }, set: { badge.frames = max(1, $0) }), in: 1...120).fixedSize()
                        Slider(value: Binding(get: { badge.fps ?? 10 }, set: { badge.fps = $0.rounded() }), in: 1...30).frame(width: 90)
                        Text(L("{0}/giây", Int(badge.fps ?? 10))).monospacedDigit().foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    func dropZone(text: String, button: String, types: [UTType], _ use: @escaping (String) -> Void) -> some View {
        VStack(spacing: 12) {
            if content == .image, let path = badge.image, let img = NSImage(contentsOfFile: path) {
                Image(nsImage: img).resizable().scaledToFit().frame(maxHeight: 120).clipShape(RoundedRectangle(cornerRadius: 10))
            } else {
                Image(systemName: "photo.badge.plus").font(.system(size: 40)).foregroundStyle(.secondary)
            }
            Text(text).foregroundStyle(.secondary)
            Button(button) { if let p = Self.pickImage(types: types) { use(p) } }.controlSize(.large)
            Text(L("PNG, JPG, HEIC… Ảnh vuông trông đẹp nhất.")).font(.caption).foregroundStyle(.tertiary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14).strokeBorder(dropping ? Color.accentColor : Color.secondary.opacity(0.4),
                                                                   style: StrokeStyle(lineWidth: dropping ? 2 : 1, dash: [5])))
        .onDrop(of: [.fileURL], isTargeted: $dropping) { providers in
            _ = providers.first?.loadObject(ofClass: URL.self) { url, _ in
                guard let url else { return }
                DispatchQueue.main.async { if let p = Self.importImage(url) { use(p) } }
            }
            return true
        }
    }

    var paintsInk: Bool { content == .symbol || content == .text }

    var styleBar: some View {
        Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 10) {
            GridRow {
                label(L("Hình dạng"))
                HStack(spacing: 12) {
                    Picker("", selection: $badge.shape) {
                        Label(L("Tròn"), systemImage: "circle").tag(BadgeShape.circle)
                        Label(L("Vuông"), systemImage: "square").tag(BadgeShape.rounded)
                    }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    Spacer(minLength: 12)
                    Text(L("Viền")).foregroundStyle(.secondary).fixedSize()
                    Toggle("", isOn: $badge.border).toggleStyle(.switch).labelsHidden()
                }
            }
            GridRow { label(L("Nền")); fillSwatches }
            if paintsInk {
                GridRow { label(content == .text ? L("Màu chữ") : L("Màu biểu tượng")); inkSwatches }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    func label(_ text: String) -> some View { Text(text).foregroundStyle(.secondary).fixedSize().gridColumnAlignment(.trailing) }

    var footer: some View {
        HStack {
            if let delete {
                Button(role: .destructive, action: delete) { Label(L("Xoá biểu tượng"), systemImage: "trash") }
            }
            if let reset { Button(L("Dùng biểu tượng mặc định"), action: reset) }
            Spacer()
            Button("Xong", action: done).keyboardShortcut(.defaultAction).controlSize(.large)
        }
    }

    /// "No fill" first, then the colors; picking a color turns the fill back on.
    var fillSwatches: some View {
        HStack(spacing: 4) {
            swatch(selected: badge.plain == true, help: L("Không nền")) {
                Image(systemName: "circle.slash").font(.system(size: 16)).foregroundStyle(.secondary)
            } action: { badge.plain = true }
            ForEach(Self.swatches, id: \.self) { h in
                swatch(selected: badge.plain != true && badge.color == h, help: h) { Circle().fill(Color(hex: h)!) } action: {
                    badge.color = h; badge.plain = nil
                }
            }
            customSwatch(Binding(get: { badge.color }, set: { badge.color = $0; badge.plain = nil }))
        }
    }

    var inkSwatches: some View {
        HStack(spacing: 4) {
            ForEach(Self.swatches, id: \.self) { h in
                swatch(selected: badge.fg == h, help: h) { Circle().fill(Color(hex: h)!) } action: { badge.fg = h }
            }
            customSwatch(Binding(get: { badge.fg }, set: { badge.fg = $0 }))
        }
    }

    func swatch(selected: Bool, help: String, @ViewBuilder _ face: () -> some View, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            face().frame(width: 18, height: 18)
                .overlay(Circle().stroke(.white.opacity(0.25), lineWidth: 0.5))
                .padding(2)
                .overlay(Circle().stroke(Color.accentColor, lineWidth: selected ? 2 : 0))
        }
        .buttonStyle(.plain).help(help)
    }

    /// Any other color, from the system color panel.
    func customSwatch(_ hex: Binding<String?>) -> some View {
        let isCustom = hex.wrappedValue.map { !Self.swatches.contains($0) } ?? false
        return swatch(selected: isCustom, help: L("Màu khác")) {
            Circle().fill(AngularGradient(colors: [.red, .yellow, .green, .cyan, .blue, .purple, .red], center: .center))
        } action: {
            ColorPanel.open(hex.wrappedValue.flatMap { Color(hex: $0) } ?? .white) { hex.wrappedValue = $0 }
        }
    }

    // MARK: Changing what it shows

    func open(_ c: Content) {
        shown = c
        if c == .motion, !badge.animated { useSprite(badge.sprite ?? SpriteLibrary.all.first?.id ?? "") }
        if c == .text, badge.text == nil { useText("Hi") }
    }

    func clearAnimation() { badge.sprite = nil; badge.frames = nil; badge.fps = nil }

    func useSymbol(_ sym: String) {
        replaceImage(nil); clearAnimation(); badge.text = nil
        badge.symbol = sym
    }

    func useText(_ text: String) {
        replaceImage(nil); clearAnimation()
        badge.text = text
    }

    /// A built-in animation; the first time it drops the fill so the character stands on the notch itself.
    func useSprite(_ id: String) {
        if !badge.animated && badge.plain == nil { badge.plain = true }
        replaceImage(nil)
        badge.text = nil; badge.frames = nil; badge.fps = nil
        badge.sprite = id
    }

    /// The user's own GIF, or a PNG spritesheet with square frames side by side (the count can be changed after).
    func useAnimation(_ path: String) {
        if !badge.animated && badge.plain == nil { badge.plain = true }
        badge.sprite = nil; badge.text = nil
        if path.lowercased().hasSuffix(".png"), let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
           let img = CGImageSourceCreateImageAtIndex(src, 0, nil) {
            badge.frames = max(1, img.width / max(img.height, 1)); badge.fps = 10
        } else {
            badge.frames = nil; badge.fps = nil
        }
        replaceImage(path)
    }

    func replaceImage(_ path: String?) {
        let old = badge.image
        badge.image = path
        if old != path { discard(old) }
    }

    /// Keeps a copy, so the picture survives the original being moved or deleted.
    @MainActor static func pickImage(types: [UTType] = [.image]) -> String? {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = types
        guard panel.runModal() == .OK, let url = panel.url else { return nil }
        return importImage(url)
    }

    static func importImage(_ url: URL) -> String? {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("NotchTasks/Badges")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let dest = dir.appendingPathComponent(UUID().uuidString + "." + url.pathExtension)
        return (try? FileManager.default.copyItem(at: url, to: dest)) != nil ? dest.path : nil
    }
}

/// Every SF Symbol on this Mac, from the system's own glyph catalog, with its categories and search keywords.
// shortcut: reads CoreGlyphs' plists (undocumented); if Apple moves them the picker falls back to the presets
enum SymbolLibrary {
    static let dir = URL(fileURLWithPath: "/System/Library/CoreServices/CoreGlyphs.bundle/Contents/Resources")
    static func plist(_ name: String) -> Any? {
        (try? Data(contentsOf: dir.appendingPathComponent(name + ".plist"))).flatMap { try? PropertyListSerialization.propertyList(from: $0, format: nil) }
    }
    static let all: [String] = plist("symbol_order") as? [String] ?? BadgeEditor.presets
    static let byCategory = plist("symbol_categories") as? [String: [String]] ?? [:]
    static let keywords = plist("symbol_search") as? [String: [String]] ?? [:]
    static let categories: [(key: String, icon: String)] = (plist("categories") as? [[String: String]] ?? []).compactMap { c in
        guard let k = c["key"], let i = c["icon"], !["whatsnew", "draw", "variable", "multicolor"].contains(k) else { return nil }
        return (k, i)
    }
    static let pickerCategories = [("suggested", "star.circle")] + categories
    static var titles: [String: String] { ["suggested": L("Gợi ý"), "all": L("Tất cả"), "communication": L("Liên lạc"), "weather": L("Thời tiết"), "maps": L("Bản đồ"), "objectsandtools": L("Đồ vật"),
                         "devices": L("Thiết bị"), "cameraandphotos": L("Ảnh"), "gaming": L("Trò chơi"), "connectivity": L("Kết nối"), "transportation": L("Đi lại"),
                         "automotive": "Xe", "accessibility": L("Trợ năng"), "privacyandsecurity": L("Bảo mật"), "human": L("Con người"), "home": L("Nhà"),
                         "fitness": L("Thể thao"), "nature": L("Thiên nhiên"), "editing": L("Chỉnh sửa"), "textformatting": L("Văn bản"), "media": L("Đa phương tiện"),
                         "keyboard": L("Bàn phím"), "commerce": L("Mua bán"), "time": L("Thời gian"), "health": L("Sức khoẻ"), "shapes": L("Hình"), "arrows": L("Mũi tên"),
                         "indices": L("Số, chữ"), "math": L("Toán")] }

    static func search(_ query: String, in category: String) -> [String] {
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        if category == "suggested" && q.isEmpty { return BadgeEditor.presets }
        return all.filter { name in
            (category == "all" || category == "suggested" || byCategory[name]?.contains(category) == true)
                && (q.isEmpty || name.contains(q) || keywords[name]?.contains { $0.contains(q) } == true)
        }
    }
}

/// Searchable grid of every SF Symbol, by category.
struct SymbolPicker: View {
    @Binding var selection: String
    @Binding var query: String
    @Binding var category: String

    var body: some View {
        let results = SymbolLibrary.search(query, in: category)
        VStack(alignment: .leading, spacing: 10) {
            TextField("", text: $query, prompt: Text(L("Tìm bằng tiếng Anh: star, cloud, cat, hammer…"))).textFieldStyle(.roundedBorder)
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    ForEach(SymbolLibrary.pickerCategories, id: \.0) { c in
                        Button { category = c.0 } label: {
                            Label(SymbolLibrary.titles[c.0] ?? c.0, systemImage: c.1).font(.caption)
                                .padding(.horizontal, 8).padding(.vertical, 5)
                                .background(Capsule().fill(category == c.0 ? Color.accentColor.opacity(0.35) : Color.primary.opacity(0.06)))
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 40), spacing: 6)], spacing: 6) {
                    ForEach(results, id: \.self) { sym in
                        Button { selection = sym } label: {
                            Image(systemName: sym).font(.system(size: 17)).frame(width: 40, height: 36)
                                .background(RoundedRectangle(cornerRadius: 7).fill(selection == sym ? Color.accentColor.opacity(0.45) : Color.primary.opacity(0.05)))
                        }
                        .buttonStyle(.plain).help(sym)
                    }
                }
            }
            Text(query.isEmpty ? L("{0} · {1} biểu tượng", SymbolLibrary.titles[category] ?? category, results.count) : L("{0} kết quả", results.count))
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// The system color panel, reporting back as hex (SwiftUI's ColorPicker well is too wide for a swatch row).
@MainActor final class ColorPanel: NSObject {
    static let shared = ColorPanel()
    private var onChange: ((String) -> Void)?

    static func open(_ initial: Color, _ change: @escaping (String) -> Void) {
        shared.onChange = change
        let p = NSColorPanel.shared
        p.showsAlpha = false
        p.color = NSColor(initial)
        p.setTarget(shared)
        p.setAction(#selector(changed(_:)))
        p.orderFront(nil)
    }

    @objc func changed(_ p: NSColorPanel) { onChange?(Color(nsColor: p.color).hex) }
}

// MARK: - Icon store: the animated icons as a shop window
// Everything here ships with the app today, kept compact ("Có sẵn": one grid of small tiles) so the community's packs
// can take their own section below later.

struct IconStore: View {
    @ObservedObject var model: Model
    @ObservedObject var settings = AppSettings.shared
    @State private var category = ""  // "" = all
    @State private var search = ""
    @State private var justAdded: String?

    var shown: [SpriteLibrary.Entry] {
        SpriteLibrary.all.filter { $0.category != "pets" && (category.isEmpty || $0.category == category)
            && (search.isEmpty || $0.name.localizedCaseInsensitiveContains(search) || $0.category.localizedCaseInsensitiveContains(search)) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                Text(L("Có sẵn")).font(.title3.bold())
                Text(L("{0} biểu tượng động · Miễn phí", SpriteLibrary.all.filter { $0.category != "pets" }.count))
                    .font(.callout).foregroundStyle(.secondary)
                Spacer(minLength: 8)
                TextField(L("Tìm"), text: $search).textFieldStyle(.roundedBorder).frame(width: 140)
                Button { Animations.sync() } label: { Image(systemName: "arrow.triangle.2.circlepath") }
                    .buttonStyle(.borderless).help(L("Cho mọi biểu tượng động chạy lại từ đầu cùng lúc"))
            }
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    chip(L("Tất cả"), "")
                    ForEach(SpriteLibrary.categories, id: \.self) { chip(L($0), $0) }
                }
            }
            if shown.isEmpty {
                Text(L("Không có biểu tượng nào khớp “{0}”.", search)).foregroundStyle(.secondary).frame(maxWidth: .infinity).padding(24)
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 84), spacing: 8)], spacing: 8) {
                    ForEach(shown) { tile($0) }
                }
            }
            Label(L("Gói của cộng đồng: sắp có."), systemImage: "person.3").font(.caption).foregroundStyle(.secondary)
        }
    }

    func chip(_ title: String, _ value: String) -> some View {
        let on = category == value
        return Button { category = value } label: {
            Text(title).font(.callout.weight(on ? .semibold : .regular)).foregroundStyle(on ? .white : .primary)
                .padding(.horizontal, 12).frame(height: 26)
                .background(Capsule().fill(on ? Color.accentColor : Color.secondary.opacity(0.15)))
        }
        .buttonStyle(.plain)
    }

    /// A small tile: click for where to use it. A dot marks an icon in use.
    func tile(_ e: SpriteLibrary.Entry) -> some View {
        let used = Animations.inUse(model.items).contains(e.id) || settings.badges.contains { $0.sprite == e.id }
        return Menu { useMenu(e) } label: {
            VStack(spacing: 4) {
                ZStack {
                    RoundedRectangle(cornerRadius: 10, style: .continuous)
                        .fill(LinearGradient(colors: [Color(white: 0.16), .black], startPoint: .top, endPoint: .bottom))
                    if let f = Animations.builtIn(e.id) { AnimatedArt(frames: f, size: 36) }
                    if justAdded == e.id {
                        Image(systemName: "checkmark.circle.fill").font(.title3).foregroundStyle(.white, .green).transition(.opacity)
                    }
                }
                .frame(height: 56)
                .overlay(alignment: .topTrailing) {
                    if used { Circle().fill(Color.accentColor).frame(width: 7, height: 7).padding(6).help(L("Đang dùng")) }
                }
                Text(e.name).font(.caption).lineLimit(1).foregroundStyle(.primary)
            }
            .padding(6)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.quaternary.opacity(0.45)))
            .contentShape(Rectangle())
        }
        .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden)
        .help(e.name)
    }

    /// Where an icon can go: a fixed spot on the notch, or in place of a watched app's or build tool's icon.
    @ViewBuilder func useMenu(_ e: SpriteLibrary.Entry) -> some View {
        let icon = Badge(plain: true, sprite: e.id)
        let full = { (right: Bool) in settings.badges.filter { $0.right == right }.count >= Badge.perSide }
        Section(L("Biểu tượng cố định trên notch")) {
            Button(L("Thêm bên trái")) { add(e, right: false) }.disabled(full(false))
            Button(L("Thêm bên phải")) { add(e, right: true) }.disabled(full(true))
        }
        if !model.items.isEmpty {
            Section(L("Đặt cho ứng dụng đang theo dõi")) {
                ForEach(model.items) { item in
                    Button(item.name) { model.items[model.items.firstIndex { $0.id == item.id }!].icon = icon; flash(e) }
                }
            }
        }
        if !settings.seenTools.isEmpty {
            Section(L("Đặt cho công cụ build")) {
                ForEach(settings.seenTools, id: \.self) { t in Button(t) { settings.toolIcons[t] = icon; flash(e) } }
            }
        }
    }

    func add(_ e: SpriteLibrary.Entry, right: Bool) {
        settings.badges.append(Badge(plain: true, right: right, sprite: e.id))
        flash(e)
    }

    func flash(_ e: SpriteLibrary.Entry) {
        withAnimation { justAdded = e.id }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { withAnimation { if justAdded == e.id { justAdded = nil } } }
    }
}
