import AppKit
import SwiftUI
import QuickLookThumbnailing
import UniformTypeIdentifiers

// MARK: - Module cards

func mmss(_ s: TimeInterval) -> String {
    let t = max(0, Int(s.rounded(.up)))
    return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60) : String(format: "%d:%02d", t / 60, t % 60)
}

struct TimerCard: View {
    @ObservedObject var model: Model
    var tint: Color { AppSettings.shared.color(for: .timer) ?? AppSettings.shared.accentColor ?? .orange }
    var body: some View {
        if let end = model.timerEnd {
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                let left = end.timeIntervalSince(ctx.date)
                HStack(spacing: 12) {
                    ZStack {
                        Circle().stroke(.white.opacity(0.12), lineWidth: 3)
                        Circle().trim(from: 0, to: max(0, left / max(model.timerTotal, 1)))
                            .stroke(tint, style: StrokeStyle(lineWidth: 3, lineCap: .round)).rotationEffect(.degrees(-90))
                        Image(systemName: "timer").font(.system(size: 12, weight: .bold)).foregroundStyle(tint)
                    }
                    .frame(width: 34, height: 34)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(mmss(left)).font(.system(size: 20, weight: .semibold, design: .rounded)).monospacedDigit().foregroundStyle(.white)
                            .contentTransition(.numericText(countsDown: true))
                        Text(L("Hẹn giờ {0} phút", Int(model.timerTotal / 60))).font(.system(size: 11)).foregroundStyle(.gray)
                    }
                    Spacer()
                    pill("+1′") { model.extendTimer(minutes: 1) }
                    pill("+5′") { model.extendTimer(minutes: 5) }
                    Button { model.stopTimer() } label: {
                        Image(systemName: "stop.fill").font(.system(size: 11, weight: .bold)).foregroundStyle(.white).frame(width: 26, height: 26)
                            .background(Circle().fill(.red.opacity(0.7)))
                    }
                    .buttonStyle(.plain).help(L("Dừng hẹn giờ"))
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
        }
    }
}

struct NotchFanPanel: View {
    @ObservedObject var model: Model
    var pane = false
    @ObservedObject private var control: FanControl
    @State private var drafts: [Int: Double] = [:]
    @State private var dragging = Set<Int>()
    @State private var autoHover = Set<Int>()
    @State private var lastSent: [Int: Date] = [:]
    @State private var pending: [Int: UUID] = [:]
    @State private var editingMode: Int?

    init(model: Model, pane: Bool = false) {
        self.model = model; self.pane = pane
        _control = ObservedObject(wrappedValue: model.fanControl)
    }
    private var tint: Color { AppSettings.shared.accentColor ?? .cyan }
    private var nameWidth: CGFloat {
        model.fans.map { FanNotch.name(count: model.fans.count, index: $0.index) as NSString }
            .map { $0.size(withAttributes: [.font: NSFont.systemFont(ofSize: 11, weight: .semibold)]).width }
            .max().map { ceil($0) + 14 } ?? 40
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !pane && (!control.enabled || !control.available) {
                FanEnableControl(control: control, compact: true)
            } else {
                VStack(spacing: 4) {
                    ForEach(model.fans) { fan in
                        VStack(spacing: 4) {
                            if pane && fan.id != model.fans.first?.id { Divider() }
                            row(fan)
                            if pane && (editingMode == fan.index || control.choices[fan.index].map { if case .sensor = $0 { return true }; return false } == true) { modeRow(fan) }
                        }
                    }
                }
                if !pane { presets.padding(.top, 4) }
                if control.hot, let fan = model.fans.first {
                    Text(FanNotch.rowText(fan, choice: .automatic, hot: true)).foregroundStyle(.orange).padding(.top, 4)
                }
                if !pane { FanEnableControl(control: control, compact: true) }
            }
            if !pane && !control.status.isEmpty && control.status != L("Quạt đã về tự động vì máy quá nóng") {
                Text(control.status).foregroundStyle(.orange).padding(.top, 4)
            }
        }
        .font(.system(size: 11))
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(pane ? 0 : 8)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(pane ? 0 : 0.06)))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var presets: some View {
        let selected = FanNotch.preset(fans: model.fans, choices: control.choices)
        return HStack(spacing: 5) {
            Spacer(minLength: 0)
            preset(.automatic, L("Tự động"), selected: selected) { setAll { _ in .automatic } }
            Rectangle().fill(.white.opacity(0.2)).frame(width: 1, height: 9)
            preset(.half, L("Một nửa"), selected: selected) { setAll { .fixed(FanNotch.half($0)) } }
                .disabled(control.hot)
            Rectangle().fill(.white.opacity(0.2)).frame(width: 1, height: 9)
            preset(.maximum, L("Tối đa"), selected: selected) { setAll { .fixed($0.maximum) } }
                .disabled(control.hot)
        }
        .frame(height: 18)
    }

    private func preset(_ option: FanNotch.Preset, _ title: String, selected: FanNotch.Preset?, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.system(size: 10, weight: selected == option ? .semibold : .regular, design: .rounded))
                .foregroundStyle(selected == option ? tint : .secondary)
                .frame(height: 18)
        }
        .buttonStyle(FanPressStyle())
    }

    private func row(_ fan: FanReading) -> some View {
        let choice = control.choices[fan.index] ?? .automatic
        let value = drafts[fan.index] ?? FanNotch.thumbValue(fan, choice: choice)
        let name = FanNotch.name(count: model.fans.count, index: fan.index)
        return HStack(spacing: 6) {
            Color.clear.frame(width: 12, height: 12)
                .overlay { FanRotor(rpm: model.fanUnavailable.contains(fan.index) ? 0 : fan.actual,
                                    minimum: fan.minimum, maximum: fan.maximum, size: 12, tint: tint)
                    .frame(width: 12, height: 12).clipped() }
                .accessibilityHidden(true)
            HStack(spacing: 2) {
                Text(name).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                if case .sensor = choice {
                    Image(systemName: "waveform.path").font(.system(size: 8)).foregroundStyle(.secondary)
                        .help(L("Theo cảm biến"))
                }
            }
            .frame(width: nameWidth, alignment: .leading)
            FanSpeedSlider(fan: fan, value: value, dimmed: choice == .automatic,
                           dragging: dragging.contains(fan.index), tint: tint,
                           onBegin: {
                               dragging.insert(fan.index); model.dragging = true
                           }, onChange: { speed in
                               move(fan, value: speed)
                           }, onEnd: { speed, changed in
                               dragging.remove(fan.index)
                               if changed {
                                   move(fan, value: speed)
                                   pending[fan.index] = nil
                                   send(fan)
                               }
                               model.dragging = !dragging.isEmpty
                           }, onAdjust: { speed in move(fan, value: speed) })
                .disabled(control.hot || !control.enabled || !control.available)
                .accessibilityLabel(L("Quạt {0}", name))
            Text(FanNotch.valueText(actual: model.fanUnavailable.contains(fan.index) ? nil : fan.actual,
                                    forced: choice != .automatic, parked: fan.actual == 0 && choice == .automatic))
                .font(.system(size: 11, weight: .semibold, design: .rounded)).monospacedDigit()
                .foregroundStyle(fan.actual == 0 ? .secondary : .primary)
                .contentTransition(.numericText())
                .animation(AppSettings.shared.motion == .reduced ? nil : .easeOut(duration: 0.18), value: fan.actual)
                .frame(width: 39, alignment: .trailing)
            if pane {
                Button { editingMode = editingMode == fan.index ? nil : fan.index } label: {
                    Text(choice == .automatic ? L("Tự động") : mode(choice) == "fixed" ? L("Cố định") : L("Theo cảm biến"))
                        .font(.caption).padding(.horizontal, 8).padding(.vertical, 3)
                        .background(Capsule().fill(.quaternary.opacity(0.6)))
                }
                .buttonStyle(.plain).disabled(control.hot || !control.enabled || !control.available)
                .accessibilityLabel(L("Chế độ"))
            } else {
            Button { automatic(fan.index) } label: {
                Text(verbatim: "A").font(.system(size: 10, weight: .semibold, design: .rounded))
                    .foregroundStyle(.white.opacity(choice == .automatic || autoHover.contains(fan.index) ? 1 : 0.72))
                    .frame(width: 18, height: 18)
                    .background(Circle().fill(choice == .automatic ? tint.opacity(autoHover.contains(fan.index) ? 1 : 0.85) : .clear))
                    .overlay(Circle().strokeBorder(.white.opacity(choice == .automatic ? 0 : 0.5), lineWidth: 1))
            }
            .buttonStyle(FanPressStyle()).help(L("Tự động"))
            .onHover { if $0 { autoHover.insert(fan.index) } else { autoHover.remove(fan.index) } }
            .accessibilityLabel(L("Tự động"))
            .accessibilityValue(name)
            }
        }
        .frame(height: 22)
    }

    private func modeRow(_ fan: FanReading) -> some View {
        let choice = control.choices[fan.index] ?? .automatic
        return VStack(alignment: .leading, spacing: 4) {
            if editingMode == fan.index {
                Picker(L("Chế độ"), selection: Binding(get: { mode(choice) }, set: { select($0, fan: fan) })) {
                    Text(L("Tự động")).tag("automatic")
                    Text(L("Cố định")).tag("fixed")
                    Text(L("Theo cảm biến")).tag("sensor")
                }.pickerStyle(.segmented).fixedSize().disabled(control.hot || !control.enabled || !control.available)
            }
            if case let .sensor(name, startsAt, maximumAt) = choice {
                HStack(spacing: 8) {
                    Picker(L("Cảm biến"), selection: Binding(get: { name }, set: { control.choices[fan.index] = .sensor(name: $0, startsAt: startsAt, maximumAt: maximumAt) })) {
                        ForEach(model.thermals, id: \.name) { Text($0.name).tag($0.name) }
                    }
                    Stepper(L("Bắt đầu {0}°C", Int(startsAt)), value: Binding(get: { startsAt }, set: { control.choices[fan.index] = .sensor(name: name, startsAt: $0, maximumAt: max(maximumAt, $0 + 1)) }), in: 5...94)
                    Stepper(L("Tối đa {0}°C", Int(maximumAt)), value: Binding(get: { maximumAt }, set: { control.choices[fan.index] = .sensor(name: name, startsAt: min(startsAt, $0 - 1), maximumAt: $0) }), in: 6...95)
                }.font(.caption).disabled(control.hot || !control.enabled || !control.available)
            }
        }
    }

    private func mode(_ choice: FanChoice) -> String {
        switch choice { case .automatic: return "automatic"; case .fixed: return "fixed"; case .sensor: return "sensor" }
    }

    private func select(_ mode: String, fan: FanReading) {
        switch mode {
        case "fixed": control.choices[fan.index] = .fixed(FanCurve.clamp(fan.actual, minimum: fan.minimum, maximum: fan.maximum))
        case "sensor": guard let sensor = model.thermals.first else { return }; control.choices[fan.index] = .sensor(name: sensor.name, startsAt: 55, maximumAt: 85)
        default: automatic(fan.index)
        }
        control.tick(fans: model.fans, sensors: model.thermals)
    }

    private func move(_ fan: FanReading, value: Double) {
        drafts[fan.index] = value
        switch control.choices[fan.index] ?? .automatic {
        case .automatic, .sensor: lastSent[fan.index] = nil
        case .fixed: break
        }
        let now = Date()
        if FanNotch.shouldSend(last: lastSent[fan.index], now: now) {
            pending[fan.index] = nil
            send(fan)
        } else if pending[fan.index] == nil {
            let token = UUID()
            pending[fan.index] = token
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                guard pending[fan.index] == token else { return }
                pending[fan.index] = nil
                send(fan)
            }
        }
    }

    private func send(_ fan: FanReading) {
        guard let value = drafts[fan.index] else { return }
        guard !control.hot else { drafts[fan.index] = nil; return }
        let rpm = FanCurve.clamp(Int(value.rounded()), minimum: fan.minimum, maximum: fan.maximum)
        let choice = FanNotch.choiceOnInput(rpm)
        if control.choices[fan.index] != choice {
            control.choices[fan.index] = choice
            lastSent[fan.index] = Date()
            control.tick(fans: model.fans, sensors: model.thermals)
        }
        if !dragging.contains(fan.index) { drafts[fan.index] = nil }
    }

    private func automatic(_ index: Int) {
        pending[index] = nil; drafts[index] = nil; lastSent[index] = nil
        control.choices[index] = .automatic
        control.tick(fans: model.fans, sensors: model.thermals)
    }

    private func setAll(_ choice: (FanReading) -> FanChoice) {
        for fan in model.fans { pending[fan.index] = nil; drafts[fan.index] = nil; lastSent[fan.index] = nil }
        var choices = control.choices
        for fan in model.fans { choices[fan.index] = choice(fan) }
        control.choices = choices
        control.tick(fans: model.fans, sensors: model.thermals)
    }
}

private struct FanSpeedSlider: View {
    let fan: FanReading
    let value: Double
    let dimmed: Bool
    let dragging: Bool
    let tint: Color
    let onBegin: () -> Void
    let onChange: (Double) -> Void
    let onEnd: (Double, Bool) -> Void
    let onAdjust: (Double) -> Void
    @State private var hovering = false
    @State private var lastEnd: Int?
    @State private var bubble = FanBubblePhase.released
    @State private var arrival = UUID()

    var body: some View {
        GeometryReader { geometry in
            let width = max(geometry.size.width, 18)
            let fraction = fan.maximum > fan.minimum
                ? min(max((value - Double(fan.minimum)) / Double(fan.maximum - fan.minimum), 0), 1) : 0
            let thumbX = 9 + (width - 18) * fraction
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.14)).frame(width: width - 18, height: 3).position(x: width / 2, y: 11)
                Capsule().fill(LinearGradient(colors: [tint.opacity(dimmed ? 0.35 : 0.75), tint.opacity(dimmed ? 0.55 : 1)],
                                              startPoint: .leading, endPoint: .trailing))
                    .frame(width: max(0, thumbX - 9), height: 3).position(x: 9 + max(0, thumbX - 9) / 2, y: 11)
                Circle().fill(Color.primary.opacity(hovering ? 1 : dimmed ? 0.78 : 0.92))
                    .frame(width: dragging ? 15 : 12, height: dragging ? 15 : 12)
                    .overlay(Circle().strokeBorder(tint.opacity(hovering || dragging ? 0.9 : 0.45), lineWidth: 0.5))
                    .shadow(color: tint.opacity(hovering ? 0.35 : 0.16), radius: hovering ? 5 : 3, y: 1)
                    .position(x: thumbX, y: 11)
                if bubble.visible {
                    Text(verbatim: String(Int(value.rounded())))
                        .font(.system(size: 9, weight: .semibold, design: .rounded)).monospacedDigit()
                        .padding(.horizontal, 5).frame(height: 14)
                        .background(Capsule().fill(tint))
                        .position(x: min(max(thumbX, 20), width - 20), y: 0)
                        .transition(.opacity)
                }
            }
            .frame(width: width, height: 22)
            .overlay {
                FanSliderTrackingView(onBegin: { x, width in
                    let thumb = abs(x - (9 + (width - 18) * fraction)) <= 9
                    arrival = UUID()
                    bubble = thumb ? .pressedThumb : .moving
                    onBegin()
                    return thumb
                }, onChange: { x, width in
                    let speed = speed(at: x, width: width)
                    tick(at: speed)
                    withAnimation(.easeOut(duration: 0.18)) { onChange(Double(speed)) }
                    let token = arrival
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.18) {
                        guard arrival == token, bubble == .moving || bubble == .movingReleased else { return }
                        let released = bubble == .movingReleased
                        bubble = .arrived
                        if released { fadeBubble() }
                    }
                }, onDrag: { x, width in
                    arrival = UUID()
                    bubble = .dragging
                    let speed = speed(at: x, width: width)
                    tick(at: speed)
                    withTransaction(Transaction(animation: nil)) { onChange(Double(speed)) }
                }, onEnd: { x, width, changed in
                    let speed = speed(at: x, width: width)
                    if changed { tick(at: speed) }
                    withTransaction(Transaction(animation: nil)) { onEnd(Double(speed), changed) }
                    if bubble == .moving { bubble = .movingReleased }
                    else { fadeBubble() }
                })
            }
        }
        .frame(height: 22)
        .focusable()
        .onHover { inside in
            hovering = inside
            if inside { NSCursor.pointingHand.push() } else { NSCursor.pop() }
        }
        .onDisappear { if hovering { NSCursor.pop() } }
        .onMoveCommand { direction in
            if direction == .left { adjust(-50) }
            if direction == .right { adjust(50) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityValue("\(Int(value.rounded())) rpm")
        .accessibilityAdjustableAction { direction in
            switch direction {
            case .increment: adjust(50)
            case .decrement: adjust(-50)
            @unknown default: break
            }
        }
    }

    private func speed(at x: CGFloat, width: CGFloat) -> Int {
        FanNotch.sliderRPM(at: x, width: width, minimum: fan.minimum, maximum: fan.maximum)
    }

    private func fadeBubble() {
        withAnimation(.easeOut(duration: 0.15)) { bubble = .released }
    }

    private func tick(at speed: Int) {
        let end = speed == fan.minimum || speed == fan.maximum ? speed : nil
        if let end, end != lastEnd, AppSettings.shared.haptics {
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
        }
        lastEnd = end
    }

    private func adjust(_ step: Int) {
        let speed = FanNotch.snappedRPM(value + Double(step), minimum: fan.minimum, maximum: fan.maximum)
        tick(at: speed)
        onAdjust(Double(speed))
    }
}

private struct FanSliderTrackingView: NSViewRepresentable {
    @Environment(\.isEnabled) private var isEnabled
    let onBegin: (CGFloat, CGFloat) -> Bool
    let onChange: (CGFloat, CGFloat) -> Void
    let onDrag: (CGFloat, CGFloat) -> Void
    let onEnd: (CGFloat, CGFloat, Bool) -> Void

    func makeNSView(context: Context) -> TrackingView { TrackingView() }
    func updateNSView(_ view: TrackingView, context: Context) {
        view.enabled = isEnabled
        view.onBegin = onBegin
        view.onChange = onChange
        view.onDrag = onDrag
        view.onEnd = onEnd
    }

    final class TrackingView: NSView {
        var enabled = true
        private var tracking = false
        private var startedOnThumb = false
        private var moved = false
        var onBegin: ((CGFloat, CGFloat) -> Bool)?
        var onChange: ((CGFloat, CGFloat) -> Void)?
        var onDrag: ((CGFloat, CGFloat) -> Void)?
        var onEnd: ((CGFloat, CGFloat, Bool) -> Void)?
        override var mouseDownCanMoveWindow: Bool { false }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func mouseDown(with event: NSEvent) {
            guard enabled else { return }
            tracking = true
            moved = false
            let x = convert(event.locationInWindow, from: nil).x
            startedOnThumb = onBegin?(x, bounds.width) ?? false
            if !startedOnThumb { onChange?(x, bounds.width) }
        }
        override func mouseDragged(with event: NSEvent) {
            guard tracking else { return }
            moved = true
            onDrag?(convert(event.locationInWindow, from: nil).x, bounds.width)
        }
        override func mouseUp(with event: NSEvent) {
            guard tracking else { return }
            tracking = false
            onEnd?(convert(event.locationInWindow, from: nil).x, bounds.width, moved || !startedOnThumb)
        }
    }
}

private struct FanPressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.scaleEffect(configuration.isPressed ? 0.97 : 1)
    }
}

/// The fan glyph spinning at the fan's speed. Drawn once as an image and turned by Core Animation: redrawing it
/// every frame in SwiftUI laid out the whole open notch 60 times a second (~60% CPU).
private struct FanRotor: View {
    let rpm: Int
    let minimum: Int
    let maximum: Int
    let size: CGFloat
    let tint: Color

    var body: some View {
        let reduced = AppSettings.shared.motion == .reduced || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        let turns = reduced ? 0 : FanNotch.turnsPerSecond(rpm, minimum: minimum, maximum: maximum)
        SpinningImage(image: rotorImage(blur: FanNotch.blur(rpm, minimum: minimum, maximum: maximum), trails: !reduced), turns: turns)
            .frame(width: size * 1.6, height: size * 1.6)
    }

    @MainActor private func rotorImage(blur: Double, trails: Bool) -> CGImage? {
        let r = ImageRenderer(content: rotor(blur: blur, trails: trails))
        r.scale = NSScreen.main?.backingScaleFactor ?? 2
        return r.cgImage
    }

    private func rotor(blur: Double, trails: Bool) -> some View {
        Canvas { graphics, canvasSize in
            let center = CGPoint(x: canvasSize.width / 2, y: canvasSize.height / 2)
            let disc = size * (0.6 + 0.2 * blur)
            graphics.fill(Path(ellipseIn: CGRect(x: center.x - disc, y: center.y - disc,
                                                 width: disc * 2, height: disc * 2)),
                          with: .color(tint.opacity(0.24 * blur)))
            let glyph = graphics.resolve(Image(systemName: "fanblades.fill").resizable())
            if trails {
                for offset in (1...3).reversed() {
                    var trail = graphics
                    trail.translateBy(x: center.x, y: center.y)
                    trail.rotate(by: Angle.degrees(-Double(offset * 9)))
                    trail.opacity = blur * (0.24 / Double(offset))
                    trail.draw(glyph, in: CGRect(x: -size / 2, y: -size / 2, width: size, height: size))
                }
            }
            var sharp = graphics
            sharp.translateBy(x: center.x, y: center.y)
            sharp.opacity = 1 - 0.4 * blur
            sharp.draw(glyph, in: CGRect(x: -size / 2, y: -size / 2, width: size, height: size))
        }
        .foregroundStyle(tint)
        .frame(width: size * 1.6, height: size * 1.6)
    }
}

/// An image turning clockwise `turns` times a second, on its own layer (no SwiftUI update per frame).
private struct SpinningImage: NSViewRepresentable {
    let image: CGImage?
    let turns: Double

    final class Host: NSView {
        let spin = CALayer()
        var turns = 0.0
        override init(frame: NSRect) {
            super.init(frame: frame)
            wantsLayer = true
            spin.contentsGravity = .resizeAspect
            layer?.addSublayer(spin)
        }
        required init?(coder: NSCoder) { nil }
        override func layout() { super.layout(); CATransaction.setDisableActions(true); spin.frame = bounds }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }  // clicks go to the notch underneath
    }

    func makeNSView(context: Context) -> Host { Host() }

    func updateNSView(_ v: Host, context: Context) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        v.spin.contents = image
        v.spin.contentsScale = image.map { CGFloat($0.width) / max(v.bounds.width, 1) } ?? 2
        guard abs(v.turns - turns) > 0.02 else { return }
        v.turns = turns
        // carry on from where it is now, at the new speed
        let angle = (v.spin.presentation()?.value(forKeyPath: "transform.rotation.z") as? Double) ?? 0
        v.spin.removeAnimation(forKey: "spin")
        v.spin.setValue(angle, forKeyPath: "transform.rotation.z")
        guard turns > 0 else { return }
        let a = CABasicAnimation(keyPath: "transform.rotation.z")
        a.byValue = -2 * Double.pi  // negative: clockwise on a Mac layer
        a.duration = 1 / turns
        a.repeatCount = .infinity
        a.isAdditive = true
        v.spin.add(a, forKey: "spin")
    }
}

func pill(_ text: String, _ action: @escaping () -> Void) -> some View {
    Button(action: action) {
        Text(text).font(.system(size: 11, weight: .semibold, design: .rounded)).foregroundStyle(.white)
            .padding(.horizontal, 8).frame(height: 22).background(Capsule().fill(.white.opacity(0.12)))
    }
    .buttonStyle(.plain)
}

/// The day ahead: the running or next event in full, the following ones as short rows, all-day ones as a line on top.
/// Every one can be closed (×) and then stays off the notch.
struct EventList: View {
    @ObservedObject var model: Model
    static let rows = 3

    var body: some View {
        TimelineView(.periodic(from: .now, by: 30)) { ctx in
            let now = ctx.date
            let all = model.visibleEvents
            let allDay = all.filter(\.allDay), timed = all.filter { !$0.allDay }
            VStack(alignment: .leading, spacing: 8) {
                // one row each, like the timed ones: name, which calendar, how many days; a click shows it in Calendar
                ForEach(allDay, id: \.id) { e in
                    HStack(spacing: 8) {
                        Image(systemName: "sun.max.fill").font(.system(size: 10)).foregroundStyle(e.color).frame(width: 15)
                        Text(L("Cả ngày")).font(.system(size: 11, weight: .semibold)).foregroundStyle(.gray)
                        Button { open(e) } label: {
                            HStack(spacing: 6) {
                                Text(e.title).font(.system(size: 12, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                                Text([e.calendar, e.untilText].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                                    .font(.system(size: 10)).foregroundStyle(.gray).lineLimit(1)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain).help(L("Mở trong Lịch"))
                        Spacer(minLength: 6)
                        close(e, size: 8)
                    }
                }
                if !allDay.isEmpty && !timed.isEmpty { Divider().overlay(.white.opacity(0.08)) }
                if let first = timed.first { main(first, now) }
                ForEach(timed.dropFirst().prefix(Self.rows), id: \.id) { e in row(e, now) }
                if timed.count > Self.rows + 1 {
                    Text(L("+{0} sự kiện nữa", timed.count - Self.rows - 1)).font(.system(size: 10)).foregroundStyle(.gray).padding(.leading, 14)
                }
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(0.06)))
    }

    func status(_ e: CalEvent, _ now: Date) -> (text: String, running: Bool) {
        e.running(at: now) ? (L("Đang diễn ra · còn {0}", ago(e.end.timeIntervalSince(now))), true)
                           : (L("Sắp tới · còn {0}", ago(e.start.timeIntervalSince(now))), false)
    }

    func span(_ e: CalEvent) -> String {
        e.start.formatted(date: .omitted, time: .shortened) + "–" + e.end.formatted(date: .omitted, time: .shortened)
    }

    func main(_ e: CalEvent, _ now: Date) -> some View {
        let st = status(e, now)
        return HStack(spacing: 10) {
            RoundedRectangle(cornerRadius: 2).fill(e.color).frame(width: 4, height: 40)
            VStack(alignment: .leading, spacing: 3) {
                Button { open(e) } label: {
                    Text(e.title).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                }
                .buttonStyle(.plain).help(e.calendar.isEmpty ? L("Mở trong Lịch") : L("{0} · mở trong Lịch", e.calendar))
                HStack(spacing: 5) {
                    Circle().fill(st.running ? e.color : .clear).overlay(Circle().stroke(e.color, lineWidth: 1.2)).frame(width: 6, height: 6)
                    Text(span(e) + " · " + st.text).font(.system(size: 11)).foregroundStyle(st.running || e.start.timeIntervalSince(now) < 600 ? e.color : .gray)
                        .lineLimit(1)
                }
                if st.running { ProgressLine(value: e.elapsed(at: now), estimated: false, tint: e.color).frame(height: 3).padding(.top, 2) }
            }
            Spacer(minLength: 6)
            if let link = e.link {
                Button { NSWorkspace.shared.open(link) } label: {
                    Label("Tham gia", systemImage: "video.fill").font(.system(size: 11, weight: .semibold)).foregroundStyle(.white)
                        .padding(.horizontal, 10).frame(height: 24).background(Capsule().fill(.green.opacity(0.8)))
                }
                .buttonStyle(.plain)
            }
            close(e, size: 10)
        }
    }

    func row(_ e: CalEvent, _ now: Date) -> some View {
        HStack(spacing: 8) {
            Circle().fill(e.running(at: now) ? e.color : .clear).overlay(Circle().stroke(e.color, lineWidth: 1.2)).frame(width: 7, height: 7)
                .padding(.leading, 4)
            Text(e.start.formatted(date: .omitted, time: .shortened)).font(.system(size: 11, weight: .semibold)).monospacedDigit().foregroundStyle(.gray)
            Button { open(e) } label: { Text(e.title).font(.system(size: 12)).foregroundStyle(.white.opacity(0.9)).lineLimit(1) }
                .buttonStyle(.plain).help(e.calendar.isEmpty ? L("Mở trong Lịch") : L("{0} · mở trong Lịch", e.calendar))
            Spacer(minLength: 6)
            Text(status(e, now).running ? L("đang diễn ra") : L("còn ") + ago(e.start.timeIntervalSince(now)))
                .font(.system(size: 10)).foregroundStyle(.gray).lineLimit(1)
            if let link = e.link {
                Button { NSWorkspace.shared.open(link) } label: { Image(systemName: "video.fill").font(.system(size: 10)).foregroundStyle(.green) }
                    .buttonStyle(.plain).help("Tham gia")
            }
            close(e, size: 8)
        }
    }

    func open(_ e: CalEvent) {
        if let url = e.calendarURL { NSWorkspace.shared.open(url) }
        else { NSWorkspace.shared.open(URL(fileURLWithPath: "/System/Applications/Calendar.app")) }
        model.expanded = false
    }

    func close(_ e: CalEvent, size: CGFloat) -> some View {
        Button { withAnimation(.snappy) { model.dismiss(e) } } label: {
            Image(systemName: "xmark").font(.system(size: size, weight: .bold)).foregroundStyle(.gray)
                .frame(width: size + 8, height: size + 8).contentShape(Rectangle())
        }
        .buttonStyle(.plain).help(L("Ẩn sự kiện này khỏi notch"))
    }
}

/// Files held for later. Click picks one (click again to unpick), "Chọn tất cả" picks every one; the header's actions
/// then apply to the picked files, or to all of them when none is picked. Dragging a picked file drags every picked one.
struct ShelfCard: View {
    @ObservedObject var model: Model
    let targeted: Bool
    @State private var selected: Set<URL> = []

    var picked: [URL] { model.shelf.filter { selected.contains($0) } }
    var targets: [URL] { picked.isEmpty ? model.shelf : picked }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                if picked.isEmpty {
                    Label(L("Khay file"), systemImage: "tray.full.fill").font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                    Text("\(model.shelf.count)").font(.system(size: 11)).foregroundStyle(.gray)
                } else {
                    Text(L("Đã chọn {0}/{1}", picked.count, model.shelf.count)).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white)
                }
                Spacer()
                if !model.shelf.isEmpty {
                    pill(picked.count == model.shelf.count ? L("Bỏ chọn") : L("Chọn tất cả")) {
                        selected = picked.count == model.shelf.count ? [] : Set(model.shelf)
                    }
                    if !picked.isEmpty {
                        iconButton("arrow.up.forward.app", L("Mở")) { picked.forEach { NSWorkspace.shared.open($0) } }
                    }
                    iconButton("folder", L("Hiện trong Finder")) { NSWorkspace.shared.activateFileViewerSelecting(targets) }
                    iconButton("airplayaudio", picked.isEmpty ? L("Gửi tất cả qua AirDrop") : L("Gửi qua AirDrop")) {
                        NSSharingService(named: .sendViaAirDrop)?.perform(withItems: targets)
                    }
                    iconButton(picked.isEmpty ? "trash" : "minus.circle", picked.isEmpty ? L("Bỏ hết khỏi khay (file gốc giữ nguyên)") : L("Bỏ khỏi khay (file gốc giữ nguyên)")) {
                        let gone = Set(targets)
                        withAnimation(.snappy) { model.shelf.removeAll { gone.contains($0) } }
                        selected.subtract(gone)
                    }
                }
                iconButton("plus", L("Thêm file…")) { model.pickShelfFiles() }
            }
            if model.shelf.isEmpty {
                Text(targeted ? L("Thả vào đây") : L("Kéo file vào notch, hoặc bấm + để chọn"))
                    .font(.system(size: 11)).foregroundStyle(targeted ? .white : .gray)
                    .frame(maxWidth: .infinity).frame(height: 44)
                    .background(RoundedRectangle(cornerRadius: 10).strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [4])).foregroundStyle(.gray))
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 8) { ForEach(model.shelf, id: \.self) { tile($0) } }.padding(2)
                }
                Text(picked.isEmpty ? L("Bấm để chọn · bấm đúp để mở · kéo ra để thả vào ứng dụng khác")
                                    : L("Kéo một file đã chọn để mang theo cả {0} file", picked.count))
                    .font(.system(size: 9)).foregroundStyle(.gray)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.white.opacity(targeted ? 0.14 : 0.06)))
        .onChange(of: model.shelf) { selected.formIntersection($0) }
    }

    func tile(_ url: URL) -> some View {
        let on = selected.contains(url)
        let missing = !FileManager.default.fileExists(atPath: url.path)
        return VStack(spacing: 4) {
            FileThumb(url: url).frame(width: 40, height: 40).opacity(missing ? 0.35 : 1)
            Text(url.lastPathComponent).font(.system(size: 9)).foregroundStyle(.white.opacity(missing ? 0.4 : 0.85))
                .lineLimit(1).truncationMode(.middle).frame(width: 62)
        }
        .padding(.vertical, 6).padding(.horizontal, 4)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(on ? Color.accentColor.opacity(0.28) : .white.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).stroke(on ? Color.accentColor : .clear, lineWidth: 1.5))
        .overlay(alignment: .topTrailing) {
            if on { Image(systemName: "checkmark.circle.fill").font(.system(size: 13)).foregroundStyle(.white, Color.accentColor).offset(x: 3, y: -3) }
        }
        // one AppKit view takes the clicks and drags: SwiftUI can only drag one file at a time
        .overlay(FileDragSource(
            files: { on ? picked : [url] },
            click: { count in
                if count >= 2 { NSWorkspace.shared.open(url) } else if on { selected.remove(url) } else { selected.insert(url) }
            },
            dragBegan: { model.dragging = true },
            presenting: { model.presenting = $0 },
            menu: [(L("Mở"), { NSWorkspace.shared.open(url) }),
                   (L("Hiện trong Finder"), { NSWorkspace.shared.activateFileViewerSelecting([url]) }),
                   (L("Gửi qua AirDrop"), { NSSharingService(named: .sendViaAirDrop)?.perform(withItems: [url]) }),
                   (L("Bỏ khỏi khay"), { model.shelf.removeAll { $0 == url } })]))
        .help(missing ? L("Không còn tìm thấy file: {0}", url.path) : url.path)
    }

    func pill(_ text: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(text).font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.85))
                .padding(.horizontal, 8).frame(height: 20).background(Capsule().fill(.white.opacity(0.1)))
        }
        .buttonStyle(.plain)
    }
}

/// QuickLook thumbnail (photos, PDFs, videos…), the Finder icon until it arrives or when there is none.
struct FileThumb: View {
    let url: URL
    @State private var thumb: NSImage?
    @MainActor static var cache: [URL: NSImage] = [:]

    var body: some View {
        Image(nsImage: thumb ?? Self.cache[url] ?? NSWorkspace.shared.icon(forFile: url.path))
            .resizable().aspectRatio(contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 4))
            .task(id: url) {
                guard Self.cache[url] == nil else { return }
                let req = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: 80, height: 80), scale: 2, representationTypes: .thumbnail)
                if let rep = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: req) {
                    Self.cache[url] = rep.nsImage
                    thumb = rep.nsImage
                }
            }
    }
}

/// Click, double-click, right-click menu and a drag that carries several files, for one shelf tile.
struct FileDragSource: NSViewRepresentable {
    let files: () -> [URL]
    let click: (Int) -> Void
    let dragBegan: () -> Void
    let presenting: (Bool) -> Void
    let menu: [(String, () -> Void)]

    func makeNSView(context: Context) -> SourceView { SourceView() }
    func updateNSView(_ v: SourceView, context: Context) { v.source = self }

    final class SourceView: NSView, NSDraggingSource, NSMenuDelegate {
        var source: FileDragSource?
        private var down: NSEvent?
        private var dragged = false

        override func mouseDown(with e: NSEvent) { down = e; dragged = false }
        override func mouseUp(with e: NSEvent) { if !dragged { source?.click(e.clickCount) } }
        override func mouseDragged(with e: NSEvent) {
            guard !dragged, let down, let source, hypot(e.locationInWindow.x - down.locationInWindow.x, e.locationInWindow.y - down.locationInWindow.y) > 3 else { return }
            dragged = true
            let urls = source.files()
            let items = urls.enumerated().map { i, url -> NSDraggingItem in
                let item = NSDraggingItem(pasteboardWriter: url as NSURL)
                let icon = NSWorkspace.shared.icon(forFile: url.path)
                item.setDraggingFrame(NSRect(x: CGFloat(i) * 6, y: CGFloat(-i) * 6, width: 40, height: 40), contents: icon)
                return item
            }
            guard !items.isEmpty else { return }
            source.dragBegan()
            beginDraggingSession(with: items, event: down, source: self)
        }
        func draggingSession(_ s: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation { [.copy, .generic] }

        override func menu(for event: NSEvent) -> NSMenu? {
            let m = NSMenu()
            for (title, action) in source?.menu ?? [] { m.addItem(ClosureMenuItem(title, nil, action)) }
            m.delegate = self
            return m
        }
        func menuWillOpen(_ menu: NSMenu) { source?.presenting(true) }
        func menuDidClose(_ menu: NSMenu) { source?.presenting(false) }
    }
}

func iconButton(_ symbol: String, _ help: String, _ action: @escaping () -> Void) -> some View {
    Button(action: action) {
        Image(systemName: symbol).font(.system(size: 11, weight: .bold)).foregroundStyle(.gray).frame(width: 20, height: 20)
    }
    .buttonStyle(.plain).help(help)
}

// MARK: - The notch

/// Blur used by the cross-fade between notch states.
struct Soften: ViewModifier {
    let radius: CGFloat
    func body(content: Content) -> some View { content.blur(radius: radius) }
}

struct NotchView: View {
    @ObservedObject var model: Model
    @ObservedObject var settings = AppSettings.shared
    var lockScreen = false  // the display-only copy above the lock screen
    var preview = false     // drawn in the control panel: must not report its size to the real notch
    @Namespace private var ns
    @State private var leftW: CGFloat = 0
    @State private var rightW: CGFloat = 0
    @State private var dropTargeted = false
    @State private var showFans = false
    @State private var topLeftWidth: CGFloat = 0
    @State private var topRightWidth: CGFloat = 0

    var working: Int { model.states.reduce(0) { $0 + $1.working } }
    var total: Int { model.states.reduce(0) { $0 + $1.jobs.count } }
    /// The notch only shows what is actually running; idle sessions stay hidden.
    var running: [ItemState] {
        model.states.compactMap { s in
            let jobs = s.jobs.filter(\.working)
            return jobs.isEmpty ? nil : ItemState(item: s.item, jobs: jobs)
        }
    }
    var playingNow: NowPlaying? { model.nowPlaying?.playing == true ? model.nowPlaying : nil }
    /// The collapsed notch shows one event: one about to start (15 min) wins over one already running.
    var soonEvent: CalEvent? {
        let now = Date(), timed = model.visibleEvents.filter { !$0.allDay }
        return timed.first { (0..<15 * 60).contains($0.start.timeIntervalSince(now)) } ?? timed.first { $0.running(at: now) }
    }
    /// A plain process item (Antigravity) is told from the others by its app.
    func limits(_ st: ItemState) -> [LimitWindow] {
        model.limits.first { $0.kind == st.item.kind && ($0.kind != .process || $0.bundleID == st.item.bundleID) }?.windows ?? []
    }

    var hasCollapsed: Bool {
        if lockScreen { return !leftExtras.isEmpty || !rightExtras.isEmpty || !chips.isEmpty }
        return !settings.badges.isEmpty || !chips.isEmpty
    }

    /// The lock screen copy never opens and shows no toasts: nobody can hover or click it there.
    var expanded: Bool { model.expanded && !lockScreen }
    var toast: Toast? { lockScreen ? nil : model.toast }

    enum Extra: Identifiable {
        case lock, badge(Badge), battery
        var id: String {
            switch self {
            case .lock: return "lock"
            case .badge(let b): return b.id.uuidString
            case .battery: return "battery"
            }
        }
    }
    var shownBadges: [Badge] {
        guard lockScreen else { return settings.badges }
        return settings.lockShows.contains(.badge) ? (settings.lockOwnBadges ? settings.lockBadges : settings.badges) : []
    }
    /// An unlock attempt just now (lock screen only): the lock icon shows the answer even when it's normally hidden.
    var flash: AuthEvent? { lockScreen ? model.authFlash?.event : nil }
    var lockShows: Set<LockContent> {
        guard lockScreen else { return [] }
        return flash == nil ? settings.lockShows : settings.lockShows.union([.lock])
    }
    /// Fixed items at the outer edges: the lock icon outermost, then badges, the battery closest to the camera.
    var leftExtras: [Extra] {
        (lockShows.contains(.lock) && !settings.lockIconRight ? [.lock] : [])
            + shownBadges.filter { !$0.right }.map(Extra.badge)
            + (lockShows.contains(.battery) && settings.lockBatteryLeft && model.battery != nil ? [.battery] : [])
    }
    var rightExtras: [Extra] {
        (lockShows.contains(.battery) && !settings.lockBatteryLeft && model.battery != nil ? [.battery] : [])
            + shownBadges.filter(\.right).map(Extra.badge)
            + (lockShows.contains(.lock) && settings.lockIconRight ? [.lock] : [])
    }

    @ViewBuilder func extra(_ e: Extra) -> some View {
        let size = settings.iconSize
        switch e {
        case .lock:
            switch flash {
            case .failed(let touch)?:
                HStack(spacing: 5) {
                    Image(systemName: touch ? "touchid" : "lock.fill").font(.system(size: size * 0.75, weight: .bold))
                    Text(touch ? L("Vân tay không khớp") : L("Sai mật khẩu")).font(.system(size: 11, weight: .bold, design: .rounded))
                }
                .foregroundStyle(.red)
                .transition(.blurReplace)
            case .success?:
                Image(systemName: "lock.open.fill").font(.system(size: size * 0.75, weight: .bold)).foregroundStyle(.green)
                    .transition(.blurReplace)
            case nil:
                Image(systemName: "lock.fill").font(.system(size: size * 0.75, weight: .bold)).foregroundStyle(.white)
            }
        case .badge(let b):
            NotchBadge(badge: b, size: size)
        case .battery:
            if let b = model.battery {
                Label("\(b.percent)%", systemImage: b.symbol)
                    .font(.system(size: 10, weight: .bold, design: .rounded)).monospacedDigit()
                    .foregroundStyle(b.plugged ? .green : b.percent <= 20 ? .red : .white)
            }
        }
    }

    /// Toast drawn inside the notch shape (the "card" template floats below it instead).
    var inlineToast: Toast? { settings.toastStyle == .card ? nil : toast }
    var opened: Bool { expanded || inlineToast != nil }

    var shapeBottom: CGFloat {
        if expanded { return 22 }
        guard let t = inlineToast else { return 10 }
        return t.compact || settings.toastStyle == .compact ? 10 : 18
    }

    /// A one-row notch ends exactly at the camera housing's edge, which hides half the glow line: drop the edge below it.
    var glowDrop: CGFloat {
        guard settings.background == .glow, !expanded else { return 0 }
        return (inlineToast.map { $0.compact || settings.toastStyle == .compact } ?? hasCollapsed) ? 6 : 0
    }

    /// Background template; the notch row itself stays pure black so it blends with the camera housing.
    @ViewBuilder var surface: some View {
        let fade = VStack(spacing: 0) {
            Color.black.frame(height: model.notch.height)
            LinearGradient(colors: [.black, .black.opacity(0)], startPoint: .top, endPoint: .bottom).frame(height: 26)
            Spacer(minLength: 0)
        }
        switch opened ? settings.background : .black {
        case .black, .glow:
            Color.black
        case .glass:
            ZStack { Rectangle().fill(.ultraThinMaterial); Color.black.opacity(0.4); fade }
        case .gradient:
            ZStack {
                LinearGradient(colors: [Color(hex: settings.gradientA) ?? .indigo, Color(hex: settings.gradientB) ?? .black],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
                Color.black.opacity(0.3)
                fade
            }
        }
    }

    /// Nothing to show: a floating pill disappears (no housing to blend into); its hover area stays.
    var idleIsland: Bool { model.island && !opened && !hasCollapsed && shownLevel == nil }

    /// The volume / brightness bar, while the user is changing one (never on the lock screen or over the open notch).
    var shownLevel: LevelHUD? { lockScreen || expanded || !settings.on(.levels) ? nil : model.level }

    /// The island is a free-standing pill, round at every corner; the notch hugs the screen edge with flared ears.
    var shape: AnyShape {
        guard model.island else { return AnyShape(NotchShape(top: 8, bottom: shapeBottom)) }
        let r: CGFloat = expanded ? 28 : opened && shapeBottom > 10 ? 24 : model.notch.height / 2
        return AnyShape(RoundedRectangle(cornerRadius: r, style: .continuous))
    }

    var body: some View {
        let shape = shape
        let glow = settings.accentColor ?? .cyan
        VStack(spacing: 6) {
            // top-aligned: while a bounce overshoots, the extra height lands at the bottom, never as a gap above the content
            ZStack(alignment: .top) { content }
                .padding(.bottom, glowDrop)
                .background(surface.opacity(idleIsland ? 0 : 1))
                .clipShape(shape)
                .overlay {
                    if settings.background == .glow && (opened || hasCollapsed) {
                        shape.stroke(glow.opacity(0.9), lineWidth: 1.2).shadow(color: glow, radius: 6)
                            // the top edge sits on the screen border: fade it out so the glow rises from the sides
                            .mask {
                                VStack(spacing: 0) {
                                    Color.clear.frame(height: model.island ? 0 : model.notch.height * 0.5)
                                    LinearGradient(colors: [.clear, .black], startPoint: .top, endPoint: .bottom).frame(height: model.notch.height * 0.6)
                                    Color.black
                                }
                                .padding(-8)  // room for the shadow
                            }
                    }
                }
                // a floating pill casts a soft shadow so it lifts off the menu bar and wallpaper
                .shadow(color: .black.opacity(expanded ? 0.45 : model.island && !idleIsland ? 0.3 : 0), radius: model.island && !expanded ? 6 : 14, y: model.island && !expanded ? 2 : 6)
            if let t = toast, settings.toastStyle == .card, !expanded {
                cardToast(t).transition(liquid(anchor: .top).combined(with: .offset(y: -12)))
            }
        }
        .onGeometryChange(for: CGSize.self) { $0.size } action: { if !lockScreen && !preview && !expanded { model.closedSize = $0 } }
        .padding(.top, model.islandTop)
        // a wrong password shakes the lock screen notch side to side, like the password field does
        .keyframeAnimator(initialValue: CGFloat(0), trigger: failedAt) { view, x in view.offset(x: x) } keyframes: { _ in
            KeyframeTrack {
                for x in [-12.0, 10, -8, 6, -3, 0] as [CGFloat] { CubicKeyframe(x, duration: 0.07) }
            }
        }
        .animation(settings.motion.animation, value: model.authFlash?.at)
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            guard settings.on(.shelf) else { return false }
            model.receiveDrop(providers)
            return true
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        // grow with a little bounce, shrink without one
        .animation(model.expanded ? settings.motion.animation : settings.motion.settle, value: model.expanded)
        .animation(model.toast == nil ? settings.motion.settle : settings.motion.animation, value: model.toast)
        .animation(settings.motion.settle, value: layoutKey)
        .animation(settings.motion.animation, value: model.level?.kind)
        .animation(settings.motion.settle, value: model.level == nil)
        .environment(\.colorScheme, .dark)
        // the camera goes off with the notch: nobody keeps watching a mirror they can't see
        .onChange(of: model.expanded) { _, open in
            showFans = false
            if !open { model.fanPanelOpen = false }
            if !open && !lockScreen && !preview { model.mirrorOn = false }
        }
        .onChange(of: showFans) { _, open in model.fanPanelOpen = open && expanded }
        .onDisappear { model.fanPanelOpen = false }
    }

    /// Liquid feel: content blurs into place while the notch shape springs to its new size.
    /// No scaling: a scaled layer pulls away from the screen edge mid-bounce and shows a gap at the top.
    func liquid(anchor: UnitPoint) -> AnyTransition {
        settings.motion == .reduced ? .opacity
            : AnyTransition.modifier(active: Soften(radius: 6), identity: Soften(radius: 0)).combined(with: .opacity)
    }

    /// Chips seep out of the camera housing: left ones come from the right, right ones from the left.
    func ooze(fromNotchOn edge: HorizontalEdge) -> AnyTransition {
        guard settings.motion != .reduced else { return .opacity }
        return AnyTransition(.blurReplace).combined(with: .scale(scale: 0.4, anchor: edge == .trailing ? .trailing : .leading))
            .combined(with: .offset(x: edge == .trailing ? 18 : -18))
    }

    var failedAt: Date? {
        guard lockScreen, let f = model.authFlash, case .failed = f.event else { return nil }
        return f.at
    }

    var layoutKey: [String] {
        running.map { "\($0.id)\($0.jobs.count)" } + model.alerts.map(\.id)
            + [playingNow?.source ?? "", model.timerEnd == nil ? "" : "t", soonEvent?.id ?? "", "\(model.shelf.count)",
               model.call?.app ?? "", "\(model.sensors.mic)\(model.sensors.camera)"] + settings.badges.map(\.id.uuidString)
    }

    @ViewBuilder var content: some View {
        if expanded {
            expandedView
                .fixedSize(horizontal: false, vertical: showFans)
                .onGeometryChange(for: CGSize.self) { $0.size } action: { if !preview { model.openSize = $0 } }
                .transition(liquid(anchor: .top))
        } else if let l = shownLevel {
            levelView(l).transition(liquid(anchor: .center))
        } else if let t = inlineToast {
            toastView(t).transition(liquid(anchor: .top))
        } else if hasCollapsed {
            collapsedView.transition(liquid(anchor: .center))
                .contentShape(Rectangle())
                .onTapGesture { model.expanded = true }  // click-to-open mode
        } else {
            // an idle island is invisible: give the pointer a pill-sized spot at the top middle to open it
            Color.clear.frame(width: noCamera ? 160 : model.notch.width, height: model.notch.height)
                .contentShape(Rectangle())
                .onTapGesture { model.expanded = true }
        }
    }

    // MARK: Volume / brightness bar

    func levelView(_ l: LevelHUD) -> some View {
        let icon = Image(systemName: l.symbol).font(.system(size: 13, weight: .bold)).foregroundStyle(.white)
            .contentTransition(.symbolEffect(.replace)).frame(width: 20)
        let bar = GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(.white.opacity(0.2))
                Capsule().fill(l.muted ? Color.gray : .white).frame(width: g.size.width * (l.muted ? 0 : l.value))
            }
        }
        .frame(height: 5)
        .animation(.easeOut(duration: 0.12), value: l.value)
        let pct = Text(l.muted ? L("Tắt") : "\(Int((l.value * 100).rounded()))").font(.system(size: 11, weight: .bold, design: .rounded))
            .monospacedDigit().foregroundStyle(.white).frame(width: 26, alignment: .trailing)
        return Group {
            if noCamera {
                HStack(spacing: 10) { icon; bar.frame(width: 110); pct }.padding(.horizontal, 14)
            } else {
                // icon left of the camera, bar and number right of it
                HStack(spacing: 0) {
                    icon.frame(width: 120, alignment: .leading)
                    Color.clear.frame(width: model.notch.width)
                    HStack(spacing: 8) { bar; pct }.frame(width: 120)
                }
                .padding(.horizontal, 14)
            }
        }
        .frame(height: model.notch.height)
    }

    // MARK: Toast templates

    @ViewBuilder func toastIcon(_ t: Toast, _ size: CGFloat) -> some View {
        if let art = t.art {
            ToastArtView(art: art, tint: t.tint, size: size)
        } else if let item = t.icon {
            ItemIcon(item: item, size: size)
        } else {
            Image(systemName: t.symbol).font(.system(size: size * 0.75, weight: .bold)).foregroundStyle(t.tint)
        }
    }

    func openToast(_ t: Toast) {
        // an agent's ask: open the notch, where the whole question and every choice are
        if t.ask != nil { model.dismissToast(); model.expanded = true; return }
        if let pane = t.pane {
            model.showSettings()
            NotificationCenter.default.post(name: .openPane, object: pane)
        }
        // a downloaded file is shown in Finder, not opened: it may be an installer
        else if let url = t.url, url.isFileURL, url.pathExtension != "app" { NSWorkspace.shared.activateFileViewerSelecting([url]) }
        else if let url = t.url { NSWorkspace.shared.open(url) }
        model.dismissToast()
    }

    @ViewBuilder func toastView(_ t: Toast) -> some View {
        Group {
            if t.compact || settings.toastStyle == .compact {
                // on the notch row itself: icon left, value (or title) right
                let side: CGFloat = t.compact ? 52 : 170
                HStack(spacing: 0) {
                    toastIcon(t, 18).frame(width: side, alignment: .leading)
                    Color.clear.frame(width: model.notch.width)
                    Text(t.compact ? t.subtitle : t.title)
                        .font(.system(size: 12, weight: .bold, design: .rounded)).monospacedDigit().lineLimit(1)
                        .foregroundStyle(t.compact ? t.tint : .white)
                        .frame(width: side, alignment: .trailing)
                }
                .padding(.horizontal, 14)
                .frame(height: model.notch.height)
                .help(t.title + (t.subtitle.isEmpty ? "" : " · " + t.subtitle))
            } else {
                let banner = settings.toastStyle == .banner
                VStack(spacing: 0) {
                    // the notch row carries the icons, the text sits right under the notch
                    HStack(spacing: 0) {
                        // a device's picture is too detailed for the notch row: it gets the big spot beside the text
                        Group { if !t.hasDeviceArt { toastIcon(t, 20) } }.frame(width: 40, alignment: .leading)
                        Spacer(minLength: model.notch.width)
                        Group {
                            if t.icon != nil { Image(systemName: t.symbol).font(.system(size: 14, weight: .bold)).foregroundStyle(t.tint) }
                        }
                        .frame(width: 40, alignment: .trailing)
                    }
                    .padding(.horizontal, 16)
                    .frame(height: model.notch.height)
                    HStack(spacing: 10) {
                        if banner { RoundedRectangle(cornerRadius: 2).fill(t.tint).frame(width: 4, height: 34) }
                        if t.hasDeviceArt { toastIcon(t, 54) }
                        VStack(alignment: .leading, spacing: 1) {
                            Text(t.title).font(.system(size: banner ? 15 : 13, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                            if !t.subtitle.isEmpty {
                                Text(t.subtitle).font(.system(size: banner ? 12 : 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                            }
                        }
                        Spacer(minLength: 6)
                        toastButtons(t)
                    }
                    .padding(.horizontal, 18)
                    .padding(.top, 2)
                    .padding(.bottom, banner ? 12 : 10)
                }
                // an agent's ask has two answers and a line of what it wants: it gets the wide layout
                .frame(width: banner || t.ask != nil ? max(model.notch.width + 300, 520) : max(model.notch.width + 150, 340))
            }
        }
        .contentShape(Rectangle())
        .onTapGesture { openToast(t) }
    }

    /// What a toast offers, spelled out: its action as a real button, and × to put it away.
    @ViewBuilder func toastButtons(_ t: Toast) -> some View {
        HStack(spacing: 6) {
            if let a = t.ask {
                if a.isQuestion {
                    choiceButton(L("Trả lời"), primary: true) { openToast(t) }
                } else {
                    choiceButton(L("Từ chối"), primary: false) { model.reply(a, .deny) }
                    choiceButton(L("Cho phép"), primary: true) { model.reply(a, .allow) }
                }
            } else if let a = t.actionTitle {
                choiceButton(a, primary: true) { openToast(t) }
            }
            Button { model.dismissToast() } label: {
                Image(systemName: "xmark").font(.system(size: 9, weight: .bold)).foregroundStyle(.white.opacity(0.7))
                    .frame(width: 22, height: 22).background(Circle().fill(.white.opacity(0.1))).contentShape(Circle())
            }
            .buttonStyle(.plain).help(L("Ẩn thông báo"))
        }
    }

    /// White on black reads at a glance, whatever the toast's color; the second choice is a quieter grey.
    func choiceButton(_ title: String, primary: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(.system(size: 11, weight: .semibold)).foregroundStyle(primary ? .black : .white).lineLimit(1)
                .fixedSize()  // the toast's text gives way, never the button's words
                .padding(.horizontal, 11).frame(height: 24).background(Capsule().fill(primary ? .white : .white.opacity(0.16)))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    /// "Thẻ nổi" template: a separate rounded card under the notch.
    func cardToast(_ t: Toast) -> some View {
        HStack(spacing: 12) {
            toastIcon(t, 26)
            VStack(alignment: .leading, spacing: 2) {
                Text(t.title).font(.system(size: 13, weight: .semibold)).foregroundStyle(.white).lineLimit(1)
                if !t.subtitle.isEmpty { Text(t.subtitle).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1) }
            }
            Spacer(minLength: 6)
            if t.icon != nil { Image(systemName: t.symbol).font(.system(size: 15, weight: .bold)).foregroundStyle(t.tint) }
            toastButtons(t)
        }
        .padding(12)
        .frame(width: 380)
        .background(
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(settings.background == .gradient
                      ? AnyShapeStyle(LinearGradient(colors: [Color(hex: settings.gradientA) ?? .indigo, Color(hex: settings.gradientB) ?? .black],
                                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                      : AnyShapeStyle(Color.black.opacity(0.92)))
        )
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(t.tint.opacity(0.5), lineWidth: 1))
        .shadow(color: .black.opacity(0.4), radius: 12, y: 6)
        .contentShape(Rectangle())
        .onTapGesture { openToast(t) }
    }

    // MARK: Collapsed: two wings sized to the wider one so the notch stays centered

    /// No camera in the middle (island, or a screen without a notch): nothing to straddle, so items sit together and
    /// a single icon gets a pill of its own size.
    var noCamera: Bool { model.island || !model.hasNotch }

    @ViewBuilder var collapsedView: some View {
        if noCamera { tightView } else { wingsView }
    }

    var tightView: some View {
        let s = sides
        let hasLeft = !leftExtras.isEmpty || s.single != nil || !s.left.isEmpty
        let singleTrail: Bool = { if case .sensor = s.single { return false }; return s.single != nil }()
        let hasRight = !rightExtras.isEmpty || singleTrail || !s.right.isEmpty
        return HStack(spacing: s.single != nil ? 5 : 10) {
            if hasLeft { leftWing.fixedSize() }
            if hasRight { rightWing.fixedSize() }
        }
        // island: the same gap on all four sides, so one icon sits in a circle; a notch shape needs room for its flared ears
        .padding(.horizontal, model.island ? max((model.notch.height - settings.iconSize) / 2, 6) : 20)
        .frame(height: model.notch.height)
    }

    var wingsView: some View {
        let side = max(leftW, rightW, 20)
        return HStack(spacing: 0) {
            leftWing.fixedSize()
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { w in withAnimation(w < leftW ? settings.motion.settle : settings.motion.animation) { leftW = w } }
                .frame(width: side, alignment: .leading)
            Color.clear.frame(width: model.notch.width)
            rightWing.fixedSize()
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { w in withAnimation(w < rightW ? settings.motion.settle : settings.motion.animation) { rightW = w } }
                .frame(width: side, alignment: .trailing)
        }
        .padding(.horizontal, 14)
        .frame(height: model.notch.height)
    }

    /// One collapsed activity: an icon ("lead") and its value ("trail").
    enum Chip: Identifiable {
        case alerts(Int), media(NowPlaying), timer(Date), event(CalEvent), agent(ItemState), call(CallInfo), sensor(SensorState), ask([AgentAsk]), limit(String, LimitWindow)
        var module: ModuleID {
            switch self {
            case .call: return .calls
            case .sensor: return .privacy
            case .ask: return .agents
            case .limit: return .limits
            case .alerts: return .ci
            case .media: return .media
            case .timer: return .timer
            case .event: return .calendar
            case .agent(let s): return s.item.inBuild ? .ci : .agents
            }
        }
        var id: String {
            switch self {
            case .alerts: return "alerts"
            case .media: return "media"
            case .timer: return "timer"
            case .event(let e): return e.id
            case .agent(let s): return s.id.uuidString
            case .call: return "call"
            case .sensor: return "sensor"
            case .ask: return "ask"
            case .limit(let name, let w): return "limit-\(name)-\(w.label)"
            }
        }
    }

    var chips: [Chip] {
        if lockScreen && !settings.lockShows.contains(.activity) { return [] }
        // a live call is the most urgent thing on screen: always first
        let order = settings.activeModules.filter { $0 == .calls } + settings.activeModules.filter { $0 != .calls }
        let list = order.flatMap { m -> [Chip] in
            switch m {
            case .ci: return (model.alerts.isEmpty ? [] : [.alerts(model.alerts.count)]) + running.filter(\.item.inBuild).map { .agent($0) }
            case .media: return playingNow.map { [.media($0)] } ?? []
            case .timer: return model.timerEnd.map { [.timer($0)] } ?? []
            case .calendar: return soonEvent.map { [.event($0)] } ?? []
            case .agents: return running.filter { !$0.item.inBuild }.prefix(4).map { .agent($0) }
            case .calls: return model.call.map { [.call($0)] } ?? []
            // a limit past your warning level stays on the notch until it resets: the toast alone was easy to miss
            case .limits:
                return model.limits.compactMap { p in
                    p.windows.filter { Double(LimitWindow.step($0.used)) >= settings.limitWarn }.max { $0.used < $1.used }.map { .limit(p.name, $0) }
                }
            case .privacy:
                let s = model.sensors
                return model.call == nil && (s.mic || s.camera) ? [.sensor(s)] : []
            default: return []
            }
        }
        // an agent waiting on you comes right after a call: it is stuck until you answer
        guard settings.on(.agents), !model.asks.isEmpty else { return list }
        let calls = list.prefix { if case .call = $0 { return true } else { return false } }
        return Array(calls) + [.ask(model.asks)] + list.dropFirst(calls.count)
    }

    @ViewBuilder func lead(_ c: Chip) -> some View {
        let size = settings.iconSize
        switch c {
        case .alerts:
            Image(systemName: "exclamationmark.triangle.fill").font(.system(size: size * 0.8, weight: .bold)).foregroundStyle(settings.color(for: .ci) ?? .red)
        case .media(let np):
            NowPlayingArt(np: np, size: size)
        case .timer:
            Image(systemName: "timer").font(.system(size: size * 0.8, weight: .bold)).foregroundStyle(timerTint)
        case .call(let c):
            Image(systemName: c.muted ? "mic.slash.fill" : c.video ? "video.fill" : "phone.fill")
                .font(.system(size: size * 0.75, weight: .bold)).foregroundStyle(c.muted ? .red : settings.color(for: .calls) ?? .green)
                .symbolEffect(.pulse, isActive: !c.muted)
                .frame(width: size, height: size)
        case .limit(_, let w):
            Image(systemName: w.used >= 100 ? "hourglass" : "gauge.with.dots.needle.67percent").font(.system(size: size * 0.75, weight: .bold))
                .foregroundStyle(w.used >= 95 ? .red : .orange).frame(width: size, height: size)
        case .ask(let asks):
            // the asking agent's own icon, with an orange hand: it waits on you
            ZStack(alignment: .bottomTrailing) {
                if let item = asks.first?.kind.item(in: model.items) {
                    ItemIcon(item: item, size: size)
                } else {
                    Image(systemName: "hand.raised.fill").font(.system(size: size * 0.72, weight: .bold)).foregroundStyle(.orange)
                        .frame(width: size, height: size)
                }
                Image(systemName: "hand.raised.fill").font(.system(size: size * 0.3, weight: .bold)).foregroundStyle(.white)
                    .padding(2.5).background(Circle().fill(.orange)).overlay(Circle().stroke(.black, lineWidth: 1.5))
                    .offset(x: size * 0.2, y: size * 0.14)
                    .symbolEffect(.pulse)
            }
            .frame(width: size, height: size)
        case .sensor(let s):
            HStack(spacing: 3) {
                if s.mic { Image(systemName: "mic.fill").foregroundStyle(settings.color(for: .privacy) ?? .orange) }
                if s.camera { Image(systemName: "video.fill").foregroundStyle(.green) }
            }
            .font(.system(size: size * 0.7, weight: .bold)).symbolEffect(.pulse)
            .frame(height: size)
        case .event(let e):
            let tint = settings.color(for: .calendar) ?? e.color
            if e.running(at: Date()) {
                // running: a ring filling up to its end; upcoming: the calendar icon
                ZStack {
                    Circle().stroke(tint.opacity(0.25), lineWidth: 2.5)
                    Circle().trim(from: 0, to: max(e.elapsed(at: Date()), 0.03)).stroke(tint, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
                .frame(width: size * 0.75, height: size * 0.75).frame(width: size, height: size)
            } else {
                Image(systemName: "calendar").font(.system(size: size * 0.8, weight: .bold)).foregroundStyle(tint)
            }
        case .agent(let st):
            Group {
                // builds & CI: the running tool's own icon (or the one the user gave it) says more than a hammer
                if st.item.kind == .dev, st.item.icon == nil, let j = st.jobs.first,
                   j.tool.flatMap({ settings.toolIcons[$0] }) != nil || Icons.app(j.icon) != nil {
                    JobIcon(job: j, tint: agentTint(st), size: size)
                } else {
                    ItemIcon(item: st.item, size: size)
                }
            }
            .frame(width: size, height: size)
            .matchedGeometryEffect(id: st.id, in: ns)
        }
    }

    @ViewBuilder func trail(_ c: Chip) -> some View {
        Group {
            switch c {
            case .alerts(let n):
                Text(n == 1 ? L("1 lỗi") : L("{0} lỗi", n)).foregroundStyle(settings.color(for: .ci) ?? .red)
            case .media(let np):
                Bars(color: settings.color(for: .media) ?? Icons.tint(np.item))
            case .timer(let end):
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(mmss(end.timeIntervalSince(ctx.date))).foregroundStyle(timerTint)
                }
            case .event(let e):
                let left = e.start.timeIntervalSinceNow
                // upcoming: minutes until it starts, in white; running: minutes until it ends, in the calendar's color
                Text("\(Int((left > 0 ? left : e.end.timeIntervalSinceNow) / 60) + 1)′")
                    .foregroundStyle(left > 0 ? .white : settings.color(for: .calendar) ?? e.color)
            case .call(let c):
                TimelineView(.periodic(from: .now, by: 1)) { ctx in
                    Text(mmss(ctx.date.timeIntervalSince(c.since))).foregroundStyle(c.muted ? .red : settings.color(for: .calls) ?? .green)
                }
            case .sensor:
                EmptyView()
            case .ask(let asks):
                Text(asks.count == 1 ? L("Chờ bạn") : L("{0} chờ", asks.count)).foregroundStyle(.orange)
            case .limit(let name, let w):
                // out of limit: when you can go again; else the step reached
                Text(w.used >= 100 ? L("Chờ ") + w.resetText() : "\(Int(w.used))%")
                    .foregroundStyle(w.used >= 95 ? .red : .orange).help(L("{0} đã dùng {1}% giới hạn {2}", name, Int(w.used), w.label))
            case .agent(let st):
                HStack(spacing: 4) {
                    if st.jobs.count == 1, let p = st.jobs[0].progress { ProgressRing(value: p, tint: agentTint(st)) }
                    else { Text("\(st.jobs.count)").foregroundStyle(agentTint(st)).contentTransition(.numericText()) }
                }
            }
        }
        .font(.system(size: 11, weight: .bold, design: .rounded)).monospacedDigit()
    }

    func chip(_ c: Chip, from edge: HorizontalEdge) -> some View {
        HStack(spacing: 4) { lead(c); trail(c) }.transition(ooze(fromNotchOn: edge))
    }

    var timerTint: Color { settings.color(for: .timer) ?? settings.accentColor ?? .orange }
    func agentTint(_ st: ItemState) -> Color { settings.color(for: st.item.inBuild ? .ci : .agents) ?? Icons.tint(st.item) }

    /// Dynamic Island style: one activity = icon left, value right; several = split across both wings.
    /// Modules pinned left/right (Settings › Mô-đun) go there; "auto" ones balance the two sides.
    var sides: (left: [Chip], right: [Chip], single: Chip?) {
        let all = chips
        if all.count == 1, settings.placement(all[0].module) == .auto { return ([], [], all[0]) }
        var left: [Chip] = [], right: [Chip] = []
        for c in all {
            switch settings.placement(c.module) {
            case .left: left.append(c)
            case .right: right.append(c)
            case .auto: break
            }
        }
        for c in all where settings.placement(c.module) == .auto {
            if left.count <= right.count { left.append(c) } else { right.append(c) }
        }
        let order = { (c: Chip) in all.firstIndex { $0.id == c.id } ?? 0 }
        return (left.sorted { order($0) < order($1) }, right.sorted { order($0) < order($1) }, nil)
    }

    var leftWing: some View {
        let s = sides
        return HStack(spacing: 10) {
            ForEach(leftExtras) { extra($0).transition(ooze(fromNotchOn: .trailing)) }
            if let one = s.single { lead(one).transition(ooze(fromNotchOn: .trailing)) } else { ForEach(s.left) { chip($0, from: .trailing) } }
        }
    }

    var rightWing: some View {
        let s = sides
        return HStack(spacing: 10) {
            if let one = s.single { trail(one).transition(ooze(fromNotchOn: .leading)) } else { ForEach(s.right) { chip($0, from: .leading) } }
            ForEach(rightExtras) { extra($0).transition(ooze(fromNotchOn: .leading)) }
        }
    }

    // MARK: Expanded

    var summary: String {
        var parts: [String] = []
        if working > 0 { parts.append(L("{0} đang chạy", working)) }
        if total > working { parts.append(L("{0} đang chờ", total - working)) }
        if let np = playingNow { parts.append("♪ " + np.appName) }
        return parts.isEmpty ? L("Không có gì đang chạy") : parts.joined(separator: " · ")
    }

    func openActivityMonitor() {
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: "/System/Applications/Utilities/Activity Monitor.app"), configuration: .init())
        model.expanded = false
    }

    var expandedView: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 0) {
                // CPU/RAM take the status text's place; the open cards already say what is running
                Group {
                    if settings.systemLoad, let l = model.load {
                        Button(action: openActivityMonitor) {
                            HStack(spacing: 10) {
                                LoadMeter(label: "CPU", value: l.cpu)
                                LoadMeter(label: "RAM", value: l.memory)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .fixedSize()
                    } else {
                        Text(summary).font(.system(size: 11, weight: .semibold)).foregroundStyle(.gray).lineLimit(1)
                    }
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { topLeftWidth = $0 }
                .frame(width: max(topLeftWidth, topRightWidth), alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                Color.clear.frame(width: model.notch.width)
                HStack(alignment: .center, spacing: 0) {
                    Spacer(minLength: 0)
                    if Requirement.fan.met(on: Requirement.current), settings.systemLoad, let t = Thermals.hottest(model.thermals) {
                        HStack(alignment: .center, spacing: 4) {
                            Image(systemName: "thermometer.medium").font(.system(size: 12))
                                .frame(width: 14, height: 14)
                            Text("\(Int(t.celsius.rounded()))°").font(.system(size: 11, weight: .semibold)).monospacedDigit()
                        }
                        .foregroundStyle(t.celsius >= 85 ? .red : t.celsius >= 60 ? .orange : .green)
                        .help(L("Nhiệt độ") + ": \(t.name) \(t.celsius.formatted(.number.precision(.fractionLength(1))))°C")
                        .fixedSize()
                    } else if Requirement.fan.met(on: Requirement.current) && settings.systemLoad {
                        let state = ProcessInfo.processInfo.thermalState
                        Label(state == .critical || state == .serious ? L("Nóng") : state == .fair ? L("Ấm") : L("Mát"), systemImage: "thermometer.medium")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(state == .critical || state == .serious ? .red : state == .fair ? .orange : .green)
                            .fixedSize()
                    }
                    if Requirement.fan.met(on: Requirement.current) && settings.systemLoad && !model.fans.isEmpty {
                        Color.clear.frame(width: 10)
                        Button { showFans.toggle() } label: {
                            HStack(alignment: .center, spacing: 4) {
                                if let fastest = model.fans.max(by: { $0.actual < $1.actual }) {
                                    Color.clear.frame(width: 14, height: 14)
                                        .overlay { FanRotor(rpm: model.fanUnavailable.contains(fastest.index) ? 0 : fastest.actual,
                                                            minimum: fastest.minimum, maximum: fastest.maximum,
                                                            size: 12, tint: model.fans.contains(where: \.forced) ? (settings.accentColor ?? .cyan) : .white.opacity(0.7))
                                            .frame(width: 14, height: 14).clipped() }
                                        .accessibilityHidden(true)
                                }
                                let speed = model.fans.filter { !model.fanUnavailable.contains($0.index) }.map(\.actual).max() ?? 0
                                if speed != 0 || model.fanUnavailable.count == model.fans.count {
                                    Text(verbatim: model.fanUnavailable.count == model.fans.count ? "—" : String(speed))
                                        .font(.system(size: 11, weight: .semibold)).monospacedDigit().lineLimit(1)
                                        .foregroundStyle(speed == 0 ? Color.secondary : model.fans.contains(where: \.forced) ? (settings.accentColor ?? .cyan) : .white.opacity(0.7))
                                }
                            }
                            .fixedSize()
                            .foregroundStyle(model.fans.contains(where: \.forced) ? (settings.accentColor ?? .cyan) : .white.opacity(0.7))
                        }
                        .buttonStyle(.plain)
                        .help(L("Quạt & nhiệt độ"))
                        .accessibilityLabel(L("Quạt") + (model.fans.allSatisfy { $0.actual == 0 } ? "" : ", \(model.fans.map(\.actual).max() ?? 0) rpm"))
                    }
                }
                .frame(width: 120, alignment: .trailing)
                .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { topRightWidth = $0 }
                .frame(width: max(topLeftWidth, topRightWidth), alignment: .trailing)
                .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .frame(height: model.notch.height)

            if Requirement.fan.met(on: Requirement.current) && showFans && settings.systemLoad && !model.fans.isEmpty { NotchFanPanel(model: model) }

            ForEach(settings.activeModules.filter { $0 == .calls } + settings.activeModules.filter { $0 != .calls }) { m in section(m) }

            if !hasCollapsed && !(settings.on(.shelf) && (!model.shelf.isEmpty || dropTargeted)) {
                Text(L("Không có gì đang chạy")).font(.system(size: 12)).foregroundStyle(.gray)
                    .frame(maxWidth: .infinity).padding(.vertical, 6)
            }
            quickBar
        }
        .padding(.horizontal, 16)
        .padding(.top, model.island ? 8 : 0)  // clear the pill's round top corners
        .padding(.bottom, 12)
        // Mirror the measured top groups around the camera, even when one side has no fan.
        .frame(width: max(settings.size == .compact ? 480 : 520,
                          FanNotch.openWidth(left: topLeftWidth, right: topRightWidth, camera: model.notch.width, padding: 32)))
    }

    @ViewBuilder func section(_ m: ModuleID) -> some View {
        switch m {
        case .ci:
            ForEach(model.alerts) { a in AlertCard(alert: a) { model.dismiss(a) } }
            ForEach(running.filter(\.item.inBuild)) { st in ItemCard(state: st, limits: [], ns: ns) {} }
        case .media:
            if let np = model.nowPlaying {
                let blocked = model.browserBlocked.contains(np.source)
                NowPlayingCard(np: np, control: { model.control($0) }, seek: { model.seek(to: $0) },
                               enableControl: blocked ? { model.enableBrowserControl(np.source) } : nil,
                               browserSeek: NowPlaying.browsers.contains(np.source) && !blocked)
            }
        case .timer:
            TimerCard(model: model)
        case .calendar:
            if model.visibleEvents.contains(where: { !$0.allDay }) || model.visibleEvents.contains(where: \.allDay) {
                EventList(model: model)
            }
        case .agents:
            ForEach(model.asks) { a in AskCard(ask: a, item: a.kind.item(in: model.items)) { model.reply(a, $0) } }
            ForEach(running.filter { !$0.item.inBuild }) { st in
                ItemCard(state: st, limits: limits(st), ns: ns) { model.open(st.item) }
            }
        case .shelf:
            if !model.shelf.isEmpty || dropTargeted { ShelfCard(model: model, targeted: dropTargeted) }
        case .devices:
            if !model.devices.isEmpty { DevicesCard(devices: model.devices) }
        case .calls:
            if let c = model.call { CallCard(call: c, mute: model.toggleMute, open: model.openCallApp) }
        case .mirror:
            if model.mirrorOn { MirrorCard { model.mirrorOn = false } }
        case .privacy:
            if model.call == nil && (model.sensors.mic || model.sensors.camera) { SensorRow(sensors: model.sensors) }
        default:
            EmptyView()
        }
    }

    var quickBar: some View {
        HStack(spacing: 6) {
            if settings.on(.timer) && model.timerEnd == nil {
                Image(systemName: "timer").font(.system(size: 11, weight: .bold)).foregroundStyle(.gray)
                ForEach([5, 15, 25, 45], id: \.self) { m in pill("\(m)′") { model.startTimer(minutes: m) } }
            }
            if settings.on(.mirror) && !model.mirrorOn {
                Button { model.openMirror() } label: {
                    Label(L("Gương"), systemImage: "person.crop.square").font(.system(size: 11, weight: .semibold)).foregroundStyle(.white)
                        .padding(.horizontal, 8).frame(height: 22).background(Capsule().fill(.white.opacity(0.12)))
                }
                .buttonStyle(.plain).help(L("Xem nhanh camera"))
            }
            Spacer()
            if settings.on(.shelf) && model.shelf.isEmpty && !dropTargeted {
                // also a button: not every file is easy to drag up here
                Button { model.pickShelfFiles() } label: {
                    Label(L("Kéo thả hoặc chọn file"), systemImage: "tray.and.arrow.down").font(.system(size: 10)).foregroundStyle(.gray).lineLimit(1)
                }
                .buttonStyle(.plain).help(L("Kéo file lên notch, hoặc bấm để chọn file đưa vào khay"))
            }
            HStack(spacing: 6) {
                toolButton("gearshape", L("Bảng điều khiển")) { model.showSettings() }
                    .overlay(alignment: .topTrailing) {
                        if model.updatePending { Circle().fill(.blue).frame(width: 5, height: 5).offset(x: 2, y: -2) }
                    }
                toolButton("power", L("Thoát NotchTasks")) { NSApp.terminate(nil) }
            }
        }
    }

    func toolButton(_ symbol: String, _ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.system(size: 11)).foregroundStyle(.gray)
                .frame(width: 22, height: 22).background(Capsule().fill(.white.opacity(0.12)))
        }
        .buttonStyle(.plain).help(title).accessibilityLabel(title)
    }
}

/// One fixed icon: a symbol, a few letters or a picture, round or square, filled or plain, optionally bordered.
struct NotchBadge: View {
    @ObservedObject var settings = AppSettings.shared
    let badge: Badge
    let size: CGFloat

    var tint: Color { badge.color.flatMap { Color(hex: $0) } ?? settings.accentColor ?? Color(white: 0.3) }
    var plain: Bool { badge.plain == true && (badge.image == nil || badge.animated) }
    /// Icon or text color: the user's choice, else white on a fill, else the badge color itself.
    var ink: Color { badge.fg.flatMap { Color(hex: $0) } ?? (plain ? tint : .white) }
    var text: String? { badge.image == nil ? badge.text.flatMap { $0.isEmpty ? nil : $0 } : nil }

    var shape: AnyShape {
        badge.shape == .circle ? AnyShape(Capsule()) : AnyShape(RoundedRectangle(cornerRadius: size * 0.28, style: .continuous))
    }

    var body: some View {
        Group {
            if let frames = Animations.frames(for: badge) {
                // on a fill the character sits inside the shape; without one it uses the whole square
                AnimatedArt(frames: frames, size: plain ? size : size * 0.78).frame(width: size, height: size)
            } else if let path = badge.image, let img = NSImage(contentsOfFile: path) {
                Image(nsImage: img).resizable().scaledToFill().frame(width: size, height: size)
            } else if let text {
                // letters widen the badge into a pill instead of shrinking to nothing
                Text(text).font(.system(size: size * 0.5, weight: .bold, design: .rounded)).lineLimit(1)
                    .foregroundStyle(ink)
                    .padding(.horizontal, text.count > 2 ? size * 0.25 : 0)
                    .frame(minWidth: size, minHeight: size)
                    .fixedSize()
            } else {
                Image(systemName: badge.symbol).font(.system(size: size * (plain ? 0.75 : 0.55), weight: .bold))
                    .foregroundStyle(ink)
                    .frame(width: size, height: size)
            }
        }
        .background(plain ? Color.clear : tint)
        .clipShape(plain && badge.animated ? AnyShape(Rectangle()) : shape)
        .overlay { if badge.border { shape.stroke(.white.opacity(0.6), lineWidth: 1) } }
    }
}

/// A ring filling with the load, the number inside it and what it measures beside it; tinted only when the load gets high.
struct LoadMeter: View {
    let label: String
    let value: Double

    var tint: Color { value >= 0.9 ? .red : value >= 0.7 ? .orange : .green }

    var body: some View {
        HStack(spacing: 5) {
            ZStack {
                Circle().stroke(.white.opacity(0.14), lineWidth: 2.5)
                Circle().trim(from: 0, to: min(max(value, 0.02), 1))
                    .stroke(tint, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Text("\(Int((value * 100).rounded()))").font(.system(size: 7.5, weight: .bold, design: .rounded)).monospacedDigit()
                    .foregroundStyle(.white)
            }
            .frame(width: 20, height: 20)
            Text(label).font(.system(size: 10, weight: .semibold)).foregroundStyle(.white.opacity(0.6))
        }
        .animation(.easeOut(duration: 0.4), value: value)
        .help((label == "CPU" ? L("CPU của cả máy: ") : L("Bộ nhớ đang dùng: ")) + L("{0}% · bấm để mở Activity Monitor", Int((value * 100).rounded())))
    }
}

extension Toast {
    var hasDeviceArt: Bool { if case .device = art { return true } else { return false } }
}

/// The animated picture of a toast.
struct ToastArtView: View {
    let art: ToastArt
    let tint: Color
    let size: CGFloat

    var body: some View {
        switch art {
        case .charge(let percent, let plugged): ChargeArt(percent: percent, plugged: plugged, height: size)
        case .device(let key, let symbol, let connected): DeviceArtView(key: key, symbol: symbol, connected: connected, tint: tint, size: size)
        case .loading: Spinner().frame(width: size, height: size)
        }
    }
}

/// Plugged in: the battery fills up to its level, a bolt snaps in and a green glow pulses. Unplugged: the plug slides
/// away, the bolt fades, the green drains to white.
struct ChargeArt: View {
    let percent: Int
    let plugged: Bool
    let height: CGFloat
    @State private var shown = false
    @State private var glow = false

    var level: Double { Double(percent) / 100 }
    var fill: Color { plugged ? .green : percent <= 20 ? .red : .white }

    var body: some View {
        let h = height * 0.72, w = h * 2
        HStack(spacing: 3) {
            if !plugged {
                Image(systemName: "powerplug.portrait.fill").font(.system(size: h * 0.85))
                    .foregroundStyle(.gray)
                    .offset(x: shown ? -6 : 0).opacity(shown ? 0 : 1)
            }
            HStack(spacing: 1) {
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: h * 0.3, style: .continuous).stroke(.white.opacity(0.6), lineWidth: 1.3)
                    RoundedRectangle(cornerRadius: h * 0.2, style: .continuous).fill(shown ? fill : .green)
                        .frame(width: max(2, (w - 4) * (plugged ? (shown ? level : 0) : level)))
                        .padding(2)
                    if plugged {
                        Image(systemName: "bolt.fill").font(.system(size: h * 0.75, weight: .black)).foregroundStyle(.white)
                            .shadow(color: .black.opacity(0.5), radius: 1)
                            .scaleEffect(shown ? 1 : 0.2).opacity(shown ? 1 : 0)
                            .frame(width: w)
                    }
                }
                .frame(width: w, height: h)
                RoundedRectangle(cornerRadius: 1).fill(.white.opacity(0.6)).frame(width: 2, height: h * 0.4)
            }
            .shadow(color: plugged ? .green.opacity(glow ? 0.9 : 0) : .clear, radius: glow ? 6 : 0)
        }
        .onAppear {
            withAnimation(.spring(response: 0.6, dampingFraction: 0.7).delay(0.1)) { shown = true }
            if plugged { withAnimation(.easeInOut(duration: 0.7).repeatCount(3).delay(0.5)) { glow = true } }
        }
    }
}

/// Connected: the product rises into place and floats (a ripple for devices without a picture).
/// Disconnected: it greys out and sinks.
struct DeviceArtView: View {
    let key: String?
    let symbol: String
    let connected: Bool
    let tint: Color
    let size: CGFloat
    @State private var shown = false
    @State private var float = false

    var body: some View {
        Group {
            if let img = DeviceArt.image(key) {
                // the system pictures carry a wide transparent margin
                Image(nsImage: img).resizable().interpolation(.high).aspectRatio(contentMode: .fit).scaleEffect(1.3)
            } else {
                ZStack {
                    if connected { Ripple(color: tint).frame(width: size * 0.45, height: size * 0.45) }
                    Image(systemName: symbol).font(.system(size: size * 0.5, weight: .semibold)).foregroundStyle(connected ? tint : .gray)
                        .symbolEffect(.bounce, value: shown)
                }
            }
        }
        .frame(width: size, height: size)
        // no 3D models ship with macOS: the product photo turns in from the side with perspective, then sways gently
        .rotation3DEffect(.degrees(connected ? (shown ? (float ? 8 : -8) : -75) : (shown ? 35 : 0)), axis: (x: 0.15, y: 1, z: 0), perspective: 0.6)
        .saturation(connected ? 1 : (shown ? 0 : 1))
        .opacity(connected ? (shown ? 1 : 0) : (shown ? 0.45 : 1))
        .scaleEffect(connected ? (shown ? 1 : 0.6) : (shown ? 0.85 : 1))
        .offset(y: connected ? (shown ? (float ? -1.5 : 1.5) : 10) : (shown ? 6 : 0))
        .onAppear {
            withAnimation(.spring(response: 0.55, dampingFraction: 0.62)) { shown = true }
            if connected { withAnimation(.easeInOut(duration: 1.6).repeatForever().delay(0.6)) { float = true } }
        }
    }
}

/// AppKit's spinner on its own animation thread: it keeps turning while the main thread is busy clicking menus.
struct Spinner: NSViewRepresentable {
    func makeNSView(context: Context) -> NSProgressIndicator {
        let p = NSProgressIndicator()
        p.style = .spinning
        p.controlSize = .small
        p.usesThreadedAnimation = true
        p.appearance = NSAppearance(named: .darkAqua)
        p.startAnimation(nil)
        return p
    }
    func updateNSView(_ p: NSProgressIndicator, context: Context) {}
}
