import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - Icons & colors

@MainActor enum Icons {
    static var images: [String: NSImage?] = [:]
    static var tints: [String: Color] = [:]
    static let palette: [Color] = [.blue, .teal, .green, .pink, .purple, .yellow, .orange]

    static func app(_ bundleID: String?) -> NSImage? {
        guard let id = bundleID else { return nil }
        if let cached = images[id] { return cached }
        let img = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id).map { NSWorkspace.shared.icon(forFile: $0.path) }
        images[id] = img
        return img
    }

    /// Accent color = the app icon's average color, or a stable palette pick.
    static func tint(_ item: WatchItem) -> Color {
        if let hex = item.icon?.color, let c = Color(hex: hex) { return c }
        if let custom = AppSettings.shared.accentColor { return custom }
        let key = item.bundleID ?? item.process + (item.args ?? "")
        if let c = tints[key] { return c }
        // Codex's icon averages to red, which reads as an error on the notch
        if item.kind == .codex { return Color(hex: "#6E8BFF")! }
        var c = palette[Int(key.unicodeScalars.reduce(0) { $0 + $1.value }) % palette.count]
        if let img = app(item.bundleID),
           let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1, pixelsHigh: 1, bitsPerSample: 8, samplesPerPixel: 4,
                                      hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 4, bitsPerPixel: 32) {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            img.draw(in: NSRect(x: 0, y: 0, width: 1, height: 1))
            NSGraphicsContext.restoreGraphicsState()
            if let avg = rep.colorAt(x: 0, y: 0)?.usingColorSpace(.deviceRGB), avg.saturationComponent > 0.15 {
                c = Color(hue: avg.hueComponent, saturation: max(avg.saturationComponent, 0.55), brightness: 0.95)
            }
        }
        tints[key] = c
        return c
    }
}

// MARK: - Views

struct NotchShape: Shape {
    var top: CGFloat
    var bottom: CGFloat
    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { .init(top, bottom) }
        set { top = newValue.first; bottom = newValue.second }
    }

    func path(in r: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: r.minX, y: r.minY))
        p.addQuadCurve(to: CGPoint(x: r.minX + top, y: r.minY + top), control: CGPoint(x: r.minX + top, y: r.minY))
        p.addLine(to: CGPoint(x: r.minX + top, y: r.maxY - bottom))
        p.addQuadCurve(to: CGPoint(x: r.minX + top + bottom, y: r.maxY), control: CGPoint(x: r.minX + top, y: r.maxY))
        p.addLine(to: CGPoint(x: r.maxX - top - bottom, y: r.maxY))
        p.addQuadCurve(to: CGPoint(x: r.maxX - top, y: r.maxY - bottom), control: CGPoint(x: r.maxX - top, y: r.maxY))
        p.addLine(to: CGPoint(x: r.maxX - top, y: r.minY + top))
        p.addQuadCurve(to: CGPoint(x: r.maxX, y: r.minY), control: CGPoint(x: r.maxX - top, y: r.minY))
        p.closeSubpath()
        return p
    }
}

struct ItemIcon: View {
    let item: WatchItem
    let size: CGFloat
    var body: some View {
        if let icon = item.icon {
            NotchBadge(badge: icon, size: size)
        } else if let img = Icons.app(item.bundleID) {
            Image(nsImage: img).resizable().interpolation(.high).frame(width: size, height: size)
        } else {
            RoundedRectangle(cornerRadius: size * 0.28, style: .continuous)
                .fill(Icons.tint(item).gradient)
                .overlay(Image(systemName: item.symbol).font(.system(size: size * 0.5, weight: .bold)).foregroundStyle(.white))
                .frame(width: size * 0.86, height: size * 0.86)
                .frame(width: size, height: size)
        }
    }
}

/// A task's own icon: the tool's app icon (Xcode, Android Studio…) or its symbol.
struct JobIcon: View {
    @ObservedObject var settings = AppSettings.shared
    let job: Job
    let tint: Color
    let size: CGFloat
    var body: some View {
        if let t = job.tool, let custom = settings.toolIcons[t] {
            NotchBadge(badge: custom, size: size)
        } else if let img = Icons.app(job.icon) {
            Image(nsImage: img).resizable().interpolation(.high).frame(width: size, height: size)
        } else if let s = job.symbol {
            Image(systemName: s).font(.system(size: size * 0.72, weight: .semibold)).foregroundStyle(tint).frame(width: size, height: size)
        }
    }
}

/// Thin progress line; an estimate (from past runs) is drawn softer than a reported value.
struct ProgressLine: View {
    let value: Double
    let estimated: Bool
    let tint: Color
    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.1))
                Capsule().fill(tint.opacity(estimated ? 0.6 : 1)).frame(width: max(3, g.size.width * min(max(value, 0), 1)))
            }
        }
        .frame(height: 3)
        .animation(.easeInOut(duration: 0.5), value: value)
    }
}

/// Ring without a number, for the collapsed notch.
struct ProgressRing: View {
    let value: Double
    let tint: Color
    var body: some View {
        ZStack {
            Circle().stroke(.white.opacity(0.15), lineWidth: 2)
            Circle().trim(from: 0, to: min(max(value, 0.02), 1)).stroke(tint, style: StrokeStyle(lineWidth: 2, lineCap: .round)).rotationEffect(.degrees(-90))
        }
        .frame(width: 11, height: 11)
        .animation(.easeInOut(duration: 0.5), value: value)
    }
}

struct Ripple: View {
    let color: Color
    @State private var on = false
    var body: some View {
        Circle().stroke(color, lineWidth: 1.5)
            .scaleEffect(on ? 2.4 : 1)
            .opacity(on ? 0 : 0.8)
            .onAppear { withAnimation(.easeOut(duration: 1.2).repeatForever(autoreverses: false)) { on = true } }
    }
}

struct StatusDot: View {
    let working: Bool
    let tint: Color
    var body: some View {
        Circle().fill(working ? tint : Color.gray.opacity(0.5))
            .frame(width: 7, height: 7)
            .overlay { if working { Ripple(color: tint) } }
    }
}

/// Equalizer bars; the TimelineView only exists while something works, so idle costs nothing.
struct Bars: View {
    let color: Color
    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 30)) { ctx in
            let t = ctx.date.timeIntervalSinceReferenceDate
            HStack(spacing: 1.5) {
                ForEach(0..<3, id: \.self) { i in
                    Capsule().fill(color).frame(width: 2.5, height: 3 + 8 * abs(sin(t * 3.4 + Double(i) * 1.1)))
                }
            }
            .frame(height: 12)
        }
    }
}

/// Usage limit as a ring around the tool icon.
struct LimitRing: View {
    let window: LimitWindow
    let lineWidth: CGFloat

    static func color(_ used: Double) -> Color { used >= 90 ? .red : used >= 70 ? .orange : .green }

    var body: some View {
        ZStack {
            Circle().stroke(.white.opacity(0.15), lineWidth: lineWidth)
            Circle().trim(from: 0, to: min(window.used, 100) / 100)
                .stroke(Self.color(window.used), style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
        .animation(.easeInOut(duration: 0.4), value: window.used)
    }
}

enum LimitCard {
    struct Badge {
        let window: LimitWindow
        let label: String?
        let detail: String?
    }

    static func badges(_ windows: [LimitWindow], narrow: Int, now: Date = Date()) -> (shown: [Badge], hidden: [LimitWindow]) {
        let pressing = windows.indices.filter { windows[$0].used >= 90 }
            .max { a, b in windows[a].used == windows[b].used ? windows[a].resets < windows[b].resets : windows[a].used < windows[b].used }
        let candidates = windows.indices.filter { narrow < 2 || windows[$0].used >= 70 }
        var selected = Array(candidates.prefix(3))
        if let pressing, selected.count == 3 && !selected.contains(pressing) { selected[2] = pressing }
        let shown = selected.sorted().map { index -> Badge in
            let window = windows[index]
            let urgent = index == pressing
            let reset = window.resetText(now: now)
            let detail: String? = window.used >= 100 && urgent ? "→ " + reset
                : window.used >= 90 && urgent ? "\(Int(window.used.rounded()))% · " + reset
                : window.used >= 70 ? "\(Int(window.used.rounded()))%" : nil
            return Badge(window: window, label: narrow >= 1 && window.used < 70 ? nil : window.label, detail: detail)
        }
        let hidden = windows.indices.filter { !selected.contains($0) }.map { windows[$0] }
        return (shown, hidden)
    }

    static func selfTest() {
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        func w(_ label: String, _ used: Double, _ later: TimeInterval = 3600) -> LimitWindow {
            LimitWindow(label: label, used: used, resets: now.addingTimeInterval(later))
        }
        precondition(badges([w("5h", 20), w("7d", 44)], narrow: 0, now: now).shown.allSatisfy { $0.detail == nil }, "calm limits")
        precondition(badges([w("5h", 76)], narrow: 0, now: now).shown[0].detail == "76%", "warning limit")
        let mixed = badges([w("5h", 94), w("7d", 76)], narrow: 0, now: now).shown
        precondition(mixed[0].detail?.contains("94% · ") == true && mixed[1].detail == "76%", "pressing reset")
        let urgent = badges([w("5h", 94), w("7d", 94, 7200)], narrow: 0, now: now).shown
        precondition(urgent[0].detail == "94%" && urgent[1].detail?.contains("94% · ") == true, "one reset, later tie")
        precondition(badges([w("5h", 100)], narrow: 0, now: now).shown[0].detail?.hasPrefix("→ ") == true, "full limit")
        let many = [w("a", 1), w("b", 2), w("c", 3), w("d", 4), w("e", 5)]
        precondition(badges(many, narrow: 0, now: now).hidden.count == 2
                     && badges(many, narrow: 1, now: now).shown.allSatisfy { $0.label == nil }
                     && badges(many, narrow: 2, now: now).shown.isEmpty, "limit overflow and narrow row")
        precondition(badges([w("a", 1), w("b", 2), w("c", 3), w("d", 4), w("e", 94)], narrow: 0, now: now)
                     .shown.contains { $0.window.label == "e" && $0.detail?.contains("94% · ") == true }, "pressing window stays visible")
    }
}

/// One compact limit on the agent card.
struct LimitBadge: View {
    let badge: LimitCard.Badge
    var body: some View {
        HStack(spacing: 3) {
            LimitRing(window: badge.window, lineWidth: 2).frame(width: 12, height: 12)
            if let label = badge.label {
                Text(label).font(.system(size: 9, weight: .semibold)).foregroundStyle(.white.opacity(0.55)).lineLimit(1)
            }
            if let detail = badge.detail {
                Text(detail).font(.system(size: 9, weight: .semibold)).monospacedDigit()
                    .foregroundStyle(LimitRing.color(badge.window.used)).lineLimit(1)
                    .contentTransition(.numericText())
            }
        }
        .fixedSize()
        .help("\(badge.window.label) · \(Int(badge.window.used.rounded()))% · " + badge.window.resetText())
    }
}

struct ItemCard: View {
    let state: ItemState
    let limits: [LimitWindow]  // every window of this agent's plan, each with its name
    let ns: Namespace.ID
    let open: () -> Void

    var body: some View {
        let tint = AppSettings.shared.color(for: state.item.inBuild ? .ci : .agents) ?? Icons.tint(state.item)
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 8) {
                ItemIcon(item: state.item, size: 20).matchedGeometryEffect(id: state.id, in: ns)
                Text(state.item.name).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white).fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: 6)
                ViewThatFits(in: .horizontal) {
                    limitRow(narrow: 0)
                    limitRow(narrow: 1)
                    limitRow(narrow: 2)
                    EmptyView()
                }
                Text("\(state.jobs.count)")
                    .font(.system(size: 11, weight: .bold, design: .rounded)).monospacedDigit()
                    .foregroundStyle(.black)
                    .padding(.horizontal, 6).frame(height: 16)
                    .background(Capsule().fill(tint))
                    .contentTransition(.numericText())
                    .help(L("{0} tác vụ đang chạy", state.jobs.count))
            }
            .contentShape(Rectangle())
            .onTapGesture(perform: open)
            ForEach(state.jobs.prefix(4)) { j in
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 8) {
                        Group {
                            if j.icon != nil || j.symbol != nil { JobIcon(job: j, tint: tint, size: 15) } else { StatusDot(working: j.working, tint: tint) }
                        }
                        .frame(width: 20)
                        Text(j.title).font(.system(size: 12, weight: .medium)).foregroundStyle(.white.opacity(0.9)).lineLimit(1)
                        Spacer(minLength: 8)
                        Text(j.detail).font(.system(size: 11)).monospacedDigit().foregroundStyle(j.working ? tint : .gray).lineLimit(1)
                        if j.url != nil {
                            Image(systemName: "text.alignleft").font(.system(size: 10)).foregroundStyle(.gray).help(L("Mở log"))
                        }
                    }
                    if let p = j.progress { ProgressLine(value: p, estimated: j.estimated, tint: tint).padding(.leading, 28) }
                }
                .help(j.title + " — " + j.detail)
                .contentShape(Rectangle())
                .onTapGesture { if let url = j.url { NSWorkspace.shared.open(url) } }
            }
            if state.jobs.count > 4 {
                Text(L("+{0} tác vụ khác", state.jobs.count - 4)).font(.system(size: 11)).foregroundStyle(.gray).padding(.leading, 28)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
        .animation(.easeInOut(duration: 0.3), value: state)
    }

    private func limitRow(narrow: Int) -> some View {
        let display = LimitCard.badges(limits, narrow: narrow)
        return HStack(spacing: 6) {
            ForEach(display.shown, id: \.window.id) { LimitBadge(badge: $0) }
            if !display.hidden.isEmpty {
                Text("+\(display.hidden.count)").font(.system(size: 9, weight: .semibold)).foregroundStyle(.secondary)
                    .help(display.hidden.map { "\($0.label) · \(Int($0.used.rounded()))% · " + $0.resetText() }.joined(separator: "\n"))
            }
        }
        .fixedSize(horizontal: true, vertical: false)
    }
}

/// A failed CI job; click opens the job page (its terminal log), ✕ dismisses it.
struct AlertCard: View {
    let alert: JobAlert
    let dismiss: () -> Void
    @State private var hover = false

    func openLog() { if let url = alert.url { NSWorkspace.shared.open(url) } }

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "xmark.octagon.fill").font(.system(size: 16)).foregroundStyle(.red)
            VStack(alignment: .leading, spacing: 1) {
                Text(L("CI lỗi: {0}", alert.name)).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                Text("\(alert.project) · \(alert.reason)").font(.system(size: 11)).foregroundStyle(.gray).lineLimit(1)
            }
            Spacer(minLength: 6)
            if alert.url != nil {
                Text(L("Xem log")).font(.system(size: 11, weight: .semibold)).foregroundStyle(.red)
                    .padding(.horizontal, 8).frame(height: 20)
                    .background(Capsule().fill(.red.opacity(hover ? 0.3 : 0.18)))
            }
            Button(action: dismiss) {
                Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.gray).frame(width: 18, height: 18)
            }
            .buttonStyle(.plain).help(L("Ẩn thông báo"))
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.red.opacity(0.14)))
        .contentShape(Rectangle())
        .onHover { hover = $0 }
        .onTapGesture(perform: openLog)
        .help(alert.url?.absoluteString ?? "")
    }
}

/// Album art when the player gives one, else the source app's icon.
struct NowPlayingArt: View {
    let np: NowPlaying
    let size: CGFloat
    var body: some View {
        Group {
            if let url = np.artwork {
                AsyncImage(url: url) { $0.resizable().scaledToFill() } placeholder: { ItemIcon(item: np.item, size: size) }
            } else {
                ItemIcon(item: np.item, size: size)
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
    }
}

struct NowPlayingCard: View {
    let np: NowPlaying
    let control: (MediaCommand) -> Void
    var seek: ((Double) -> Void)? = nil
    var enableControl: (() -> Void)? = nil  // set when this browser still blocks control from the notch
    var browserSeek = false                 // the playing tab accepts scripts, so it can seek

    func button(_ symbol: String, _ cmd: MediaCommand, _ size: CGFloat = 12) -> some View {
        Button { control(cmd) } label: {
            Image(systemName: symbol).font(.system(size: size, weight: .bold)).foregroundStyle(.white).frame(width: 24, height: 24)
        }
        .buttonStyle(.plain)
    }

    var body: some View {
        let tint = AppSettings.shared.color(for: .media) ?? Icons.tint(np.item)
        HStack(spacing: 12) {
            NowPlayingArt(np: np, size: 46)
                .onTapGesture { NSRunningApplication.runningApplications(withBundleIdentifier: np.source).first?.activate() }
                .help(L("Mở {0}", np.appName))
            VStack(alignment: .leading, spacing: 4) {
                Text(np.title ?? L("Đang phát âm thanh")).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                HStack(spacing: 5) {
                    ItemIcon(item: np.item, size: 12)
                    Text([np.artist, np.appName].compactMap { $0 }.joined(separator: " · "))
                        .font(.system(size: 11)).foregroundStyle(.gray).lineLimit(1)
                }
                if let enableControl {
                    Button(action: enableControl) {
                        HStack(spacing: 5) {
                            Image(systemName: "hand.tap.fill").font(.system(size: 9))
                            Text(L("Bật điều khiển trong {0}", np.appName)).font(.system(size: 10, weight: .semibold))
                            Image(systemName: "chevron.right").font(.system(size: 8, weight: .bold))
                        }
                        .foregroundStyle(.black)
                        .padding(.horizontal, 8).frame(height: 18)
                        .background(Capsule().fill(.yellow))
                    }
                    .buttonStyle(.plain).padding(.top, 2)
                    .help(L("Để dừng, phát và chuyển bài ngay trong tab, không làm ứng dụng nhạc khác tự phát"))
                }
                if let d = np.duration, d > 0, let p = np.position {
                    SeekBar(position: p, duration: d, playing: np.playing, tint: tint,
                            seek: NowPlaying.players[np.source] != nil || browserSeek ? seek : nil)
                }
            }
            Spacer(minLength: 4)
            HStack(spacing: 6) {
                button("backward.fill", .previous)
                button(np.playing ? "pause.fill" : "play.fill", .toggle, 16)
                button("forward.fill", .next)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
        .animation(.easeInOut(duration: 0.25), value: np)
    }
}

/// The track's progress, moving on its own between refreshes. Where the player allows it, press or drag anywhere on it
/// to jump; the bar thickens and shows a knob and the time under the pointer while you do.
struct SeekBar: View {
    let position: Double
    let duration: Double
    let playing: Bool
    let tint: Color
    let seek: ((Double) -> Void)?
    @State private var anchor = Date()  // the local clock started from `base` at this moment
    @State private var base: Double?    // nil: not started, use `position`
    @State private var dragging: Double?  // 0…1 under the pointer
    @State private var hover = false

    var body: some View {
        // redrawn every frame while playing so the fill glides instead of stepping
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !playing || dragging != nil)) { ctx in
            let live = min(duration, (base ?? position) + (playing ? ctx.date.timeIntervalSince(anchor) : 0))
            let fraction = dragging ?? live / duration
            let active = seek != nil && (hover || dragging != nil)
            VStack(spacing: 2) {
                GeometryReader { g in
                    ZStack(alignment: .leading) {
                        Capsule().fill(.white.opacity(0.15))
                        Capsule().fill(dragging != nil ? .white : tint).frame(width: max(3, g.size.width * fraction))
                        if active {
                            Circle().fill(.white).frame(width: 10, height: 10).shadow(radius: 2)
                                .offset(x: g.size.width * fraction - 5)
                        }
                    }
                    .frame(height: active ? 6 : 4)
                    .frame(maxHeight: .infinity)
                    .contentShape(Rectangle())
                    .gesture(seek == nil ? nil : DragGesture(minimumDistance: 0)
                        .onChanged { v in dragging = min(1, max(0, v.location.x / g.size.width)) }
                        .onEnded { v in
                            let f = min(1, max(0, v.location.x / g.size.width))
                            seek?(f * duration)
                            dragging = nil
                        })
                }
                .frame(height: 12)
                if active || dragging != nil {
                    HStack {
                        Text(mmss(fraction * duration))
                        Spacer()
                        Text("-" + mmss(duration - fraction * duration))
                    }
                    .font(.system(size: 9, weight: .medium)).monospacedDigit().foregroundStyle(.gray)
                }
            }
            .animation(.easeOut(duration: 0.15), value: active)
        }
        .onHover { hover = $0 }
        // each refresh reports a position read a moment ago: follow it only when it really moved (a seek, a new track),
        // otherwise tiny corrections would make the bar twitch back and forth
        .onChange(of: position) { _, new in
            let predicted = (base ?? new) + (playing ? Date().timeIntervalSince(anchor) : 0)
            if base == nil || abs(new - predicted) > 1.5 { base = new; anchor = Date() }
        }
        .onChange(of: playing) { _, now in
            // pausing freezes the bar where it is; resuming continues from there
            base = min(duration, (base ?? position) + (now ? 0 : Date().timeIntervalSince(anchor)))
            anchor = Date()
        }
        .onChange(of: duration) { base = position; anchor = Date() }
        .help(seek == nil ? "" : L("Kéo để tua"))
    }
}

// MARK: - Calls and the privacy sensors

struct CallCard: View {
    let call: CallInfo
    let mute: () -> Void
    let open: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            ZStack(alignment: .bottomTrailing) {
                if let img = Icons.app(call.app.hasPrefix("com.apple.") ? "com.apple.FaceTime" : call.app) {
                    Image(nsImage: img).resizable().frame(width: 34, height: 34)
                } else {
                    Image(systemName: "phone.fill").font(.system(size: 16)).foregroundStyle(.white)
                        .frame(width: 34, height: 34).background(Circle().fill(.green))
                }
                Image(systemName: call.video ? "video.fill" : "phone.fill").font(.system(size: 8, weight: .bold)).foregroundStyle(.white)
                    .frame(width: 15, height: 15).background(Circle().fill(.green)).offset(x: 3, y: 3)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(call.video ? L("Gọi video · {0}", call.name) : L("Đang gọi · {0}", call.name))
                    .font(.system(size: 12, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(mmss(ctx.date.timeIntervalSince(call.since)) + (call.muted ? L(" · Mic đang tắt") : ""))
                        .font(.system(size: 11, weight: .medium)).monospacedDigit().foregroundStyle(call.muted ? .red : .green)
                }
            }
            Spacer(minLength: 6)
            // big round buttons with words under them: easy to hit and to understand mid-call
            round(call.muted ? "mic.slash.fill" : "mic.fill", call.muted ? L("Bật mic") : L("Tắt mic"), call.muted ? .red : .white.opacity(0.18), mute)
            round("arrow.up.forward.app.fill", L("Mở"), .white.opacity(0.18), open)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.green.opacity(0.12)))
    }

    func round(_ symbol: String, _ label: String, _ fill: Color, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 2) {
                Image(systemName: symbol).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white)
                    .frame(width: 32, height: 32).background(Circle().fill(fill))
                Text(label).font(.system(size: 9, weight: .medium)).foregroundStyle(.gray)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(label)
    }
}

/// Which apps are listening or filming right now, outside a call.
struct SensorRow: View {
    let sensors: SensorState
    var body: some View {
        HStack(spacing: 8) {
            if sensors.mic {
                Image(systemName: "mic.fill").foregroundStyle(.orange).symbolEffect(.pulse)
                Text("Micro: " + sensors.micApps.map(Sensors.name).joined(separator: ", ")).lineLimit(1)
            }
            if sensors.camera {
                Image(systemName: "video.fill").foregroundStyle(.green).symbolEffect(.pulse)
                Text(L("Camera đang bật"))
            }
            Spacer(minLength: 0)
        }
        .font(.system(size: 11, weight: .medium)).foregroundStyle(.white.opacity(0.85))
        .padding(.horizontal, 10).frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.white.opacity(0.06)))
    }
}

// MARK: - Camera mirror: a quick look at yourself before a meeting

import AVFoundation

struct MirrorCard: View {
    let close: () -> Void
    @State private var status = AVCaptureDevice.authorizationStatus(for: .video)

    var body: some View {
        ZStack(alignment: .topTrailing) {
            if status == .denied || status == .restricted {
                VStack(spacing: 8) {
                    Image(systemName: "video.slash.fill").font(.system(size: 22)).foregroundStyle(.gray)
                    Text(L("NotchTasks chưa được dùng camera")).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                    Button(L("Mở Cài đặt Camera")) {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera")!)
                    }
                    .controlSize(.small)
                }
                .frame(maxWidth: .infinity).frame(height: 150)
            } else {
                MirrorView { status = AVCaptureDevice.authorizationStatus(for: .video) }
                    .frame(height: 220)
                    .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
            }
            Button(action: close) {
                Image(systemName: "xmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                    .frame(width: 22, height: 22).background(Circle().fill(.black.opacity(0.5)))
            }
            .buttonStyle(.plain).padding(8).help(L("Tắt gương"))
        }
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
    }
}

/// The camera, mirrored like a real mirror. The session runs only while this view is on screen.
struct MirrorView: NSViewRepresentable {
    var answered: () -> Void = {}
    func makeNSView(context: Context) -> MirrorNSView { MirrorNSView(answered: answered) }
    func updateNSView(_ v: MirrorNSView, context: Context) {}
    static func dismantleNSView(_ v: MirrorNSView, coordinator: ()) { v.stop() }
}

final class MirrorNSView: NSView {
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "notchtasks.mirror")

    init(answered: @escaping () -> Void) {
        super.init(frame: .zero)
        let preview = AVCaptureVideoPreviewLayer(session: session)
        preview.videoGravity = .resizeAspectFill
        layer = preview
        wantsLayer = true
        AVCaptureDevice.requestAccess(for: .video) { [weak self] ok in
            DispatchQueue.main.async { answered() }
            guard ok, let self else { return }
            self.queue.async { self.start() }
        }
    }
    required init?(coder: NSCoder) { nil }

    private func start() {
        guard let cam = AVCaptureDevice.default(for: .video), let input = try? AVCaptureDeviceInput(device: cam) else { return }
        session.beginConfiguration()
        session.sessionPreset = .medium
        if session.canAddInput(input) { session.addInput(input) }
        session.commitConfiguration()
        session.startRunning()
        DispatchQueue.main.async {
            guard let c = (self.layer as? AVCaptureVideoPreviewLayer)?.connection, c.isVideoMirroringSupported else { return }
            c.automaticallyAdjustsVideoMirroring = false
            c.isVideoMirrored = true
        }
    }

    func stop() { queue.async { [session] in session.stopRunning() } }
}

/// An agent waiting on you, in full: what it wants to run, or its questions with every choice.
struct AskCard: View {
    let ask: AgentAsk
    var item: WatchItem? = nil  // the agent's watch row: its icon is the one you picked
    let reply: (AskReply) -> Void
    @State private var picks: [Int: Set<String>] = [:]

    var ready: Bool { ask.questions.indices.allSatisfy { !(picks[$0] ?? []).isEmpty } }

    var answers: [String: String] {
        Dictionary(uniqueKeysWithValues: ask.questions.enumerated().map { i, q in
            (q.question, q.options.filter { picks[i]?.contains($0) ?? false }.joined(separator: ", "))
        })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 9) {
                // the agent's icon as set in the watch list (Claude's pixel crab, Codex's robot, or yours)
                if let item {
                    ItemIcon(item: item, size: 28)
                } else {
                    Image(systemName: "sparkles").font(.system(size: 14, weight: .bold)).foregroundStyle(.orange).frame(width: 28, height: 28)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(ask.kind.name + (ask.isQuestion ? L(" đang hỏi") : L(" cần bạn duyệt"))).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                    Text(ask.session).font(.system(size: 11)).foregroundStyle(.gray).lineLimit(1)
                }
                Spacer(minLength: 6)
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(L("Chờ ") + mmss(ctx.date.timeIntervalSince(ask.at))).font(.system(size: 10, weight: .medium)).monospacedDigit().foregroundStyle(.orange)
                }
            }

            if ask.isQuestion {
                ForEach(Array(ask.questions.enumerated()), id: \.offset) { i, q in
                    VStack(alignment: .leading, spacing: 6) {
                        Text(q.question).font(.system(size: 12, weight: .medium)).foregroundStyle(.white).fixedSize(horizontal: false, vertical: true)
                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), spacing: 6)], alignment: .leading, spacing: 6) {
                            ForEach(q.options, id: \.self) { o in option(i, q, o) }
                        }
                    }
                }
            } else {
                VStack(alignment: .leading, spacing: 4) {
                    Text(ask.action).font(.system(size: 10, weight: .bold)).foregroundStyle(.orange)
                    Text(ask.summary).font(.system(size: 11, design: .monospaced)).foregroundStyle(.white.opacity(0.9))
                        .lineLimit(5).textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(8).background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(.black.opacity(0.35)))
                }
            }

            HStack(spacing: 6) {
                Button { reply(.pass) } label: {
                    Text(L("Trả lời trong {0}", ask.kind.name)).font(.system(size: 11)).foregroundStyle(.gray).underline()
                }
                .buttonStyle(.plain).help(L("Bỏ qua ở đây, {0} sẽ tự hỏi như bình thường", ask.kind.name))
                Spacer()
                if ask.isQuestion {
                    // one single-choice question is sent by the tap itself
                    if !(ask.questions.count == 1 && !ask.questions[0].multiSelect) {
                        choiceButton(L("Gửi"), primary: true) { reply(.answers(answers)) }.disabled(!ready).opacity(ready ? 1 : 0.4)
                    }
                } else {
                    choiceButton(L("Từ chối"), primary: false) { reply(.deny) }
                    choiceButton(L("Cho phép"), primary: true) { reply(.allow) }
                }
            }
        }
        .padding(11)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.orange.opacity(0.13)))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(.orange.opacity(0.35), lineWidth: 1))
    }

    func option(_ i: Int, _ q: AgentAsk.Question, _ o: String) -> some View {
        let on = picks[i]?.contains(o) ?? false
        return Button {
            if q.multiSelect {
                picks[i, default: []].formSymmetricDifference([o])
            } else {
                picks[i] = [o]
                // one question, one choice: the tap is the answer
                if ask.questions.count == 1 { reply(.answers([q.question: o])) }
            }
        } label: {
            HStack(spacing: 5) {
                if q.multiSelect { Image(systemName: on ? "checkmark.square.fill" : "square") }
                Text(o).lineLimit(2).multilineTextAlignment(.leading)
            }
            .font(.system(size: 11, weight: .semibold)).foregroundStyle(on ? .black : .white)
            .padding(.horizontal, 10).padding(.vertical, 7)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(on ? .white : .white.opacity(0.12)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    func choiceButton(_ title: String, primary: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.system(size: 11, weight: .semibold)).foregroundStyle(primary ? .black : .white)
                .padding(.horizontal, 13).frame(height: 26).background(Capsule().fill(primary ? .white : .white.opacity(0.16)))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}
