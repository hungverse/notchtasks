import AppKit
import ImageIO
import SwiftUI

// MARK: - Animated icons: the built-in sprite set, the user's GIFs and spritesheets

/// Decoded frames of one animation, with how long each stays on screen.
struct Frames {
    let images: [CGImage]
    let delays: [Double]
    let pixel: Bool  // pixel art: scale up without smoothing
    var total: Double { delays.reduce(0, +) }
    var shortest: Double { delays.min() ?? 0.1 }

    func image(at time: Double) -> CGImage {
        guard images.count > 1, total > 0 else { return images[0] }
        var t = time.truncatingRemainder(dividingBy: total)
        if t < 0 { t += total }
        for (i, d) in delays.enumerated() { if t < d { return images[i] }; t -= d }
        return images[images.count - 1]
    }

    /// A horizontal strip of `count` equal frames (count nil: square frames, as many as fit).
    static func strip(_ url: URL, count: Int?, fps: Double, pixel: Bool) -> Frames? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil), let sheet = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let n = max(1, count ?? max(1, sheet.width / max(sheet.height, 1)))
        let w = sheet.width / n
        let images = (0..<n).compactMap { sheet.cropping(to: CGRect(x: $0 * w, y: 0, width: w, height: sheet.height)) }
        guard !images.isEmpty else { return nil }
        return Frames(images: images, delays: Array(repeating: 1 / max(fps, 1), count: images.count), pixel: pixel)
    }

    static func gif(_ url: URL) -> Frames? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        var images: [CGImage] = [], delays: [Double] = []
        for i in 0..<CGImageSourceGetCount(src) {
            guard let img = CGImageSourceCreateImageAtIndex(src, i, nil) else { continue }
            let props = CGImageSourceCopyPropertiesAtIndex(src, i, nil) as? [CFString: Any]
            let gif = props?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
            let d = (gif?[kCGImagePropertyGIFUnclampedDelayTime] as? Double) ?? (gif?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
            images.append(img)
            delays.append(d < 0.011 ? 0.1 : d)  // what browsers do with a zero delay
        }
        return images.isEmpty ? nil : Frames(images: images, delays: delays, pixel: false)
    }
}

/// The animations shipped in the app (drawn by Tools/SpriteGen.swift).
enum SpriteLibrary {
    struct Entry: Decodable, Identifiable { let id, category: String; private let title: String; let frames: Int; let fps: Double; let pixel: Bool
        /// The catalog holds the English key.
        var name: String { L(title) }
    }

    static let dir = Bundle.main.resourceURL?.appendingPathComponent("Sprites")
    static let all: [Entry] = dir.flatMap { try? Data(contentsOf: $0.appendingPathComponent("catalog.json")) }
        .flatMap { try? JSONDecoder().decode([Entry].self, from: $0) } ?? []
    static let categories = ["AI", "Grind", "Cute", "Robot", "Anime"].filter { c in all.contains { $0.category == c } }

    static func entry(_ id: String) -> Entry? { all.first { $0.id == id } }
}

/// Decoded once per source; they are small, and the notch redraws them many times a second.
@MainActor enum Animations {
    private static var cache: [String: Frames] = [:]
    /// Frame 0 of every animation falls on this moment (and every loop after it). "Đồng bộ" moves it to now,
    /// so icons added at different times start over together.
    static var epoch: TimeInterval = UserDefaults.standard.double(forKey: "animationEpoch")
    static func sync() {
        epoch = Date().timeIntervalSinceReferenceDate
        UserDefaults.standard.set(epoch, forKey: "animationEpoch")
    }

    /// Built-in animations in use anywhere: fixed icons (main and lock screen), watched apps, build tools.
    static func inUse(_ items: [WatchItem]) -> Set<String> {
        let s = AppSettings.shared
        return Set((s.badges + s.lockBadges + Array(s.toolIcons.values) + items.compactMap(\.icon)).compactMap(\.sprite))
    }

    static func frames(for b: Badge) -> Frames? {
        if let id = b.sprite { return builtIn(id) }
        guard let path = b.image, b.animated else { return nil }
        let key = "\(path)#\(b.frames ?? 0)@\(b.fps ?? 0)"
        if let f = cache[key] { return f }
        let url = URL(fileURLWithPath: path)
        let f = url.pathExtension.lowercased() == "gif" ? Frames.gif(url) : Frames.strip(url, count: b.frames, fps: b.fps ?? 10, pixel: false)
        cache[key] = f
        return f
    }

    /// The pose of the phone's pets on the notch (Buddy.Pose order: idle, happy, sleep, hungry, sad, worried, focus, tired),
    /// set by Model.tick from what the Mac is doing.
    static var petPose = 0

    static func builtIn(_ id: String) -> Frames? {
        if SpriteLibrary.entry(id)?.category == "pets" { return pet(id, pose: petPose) }
        if let f = cache[id] { return f }
        guard let e = SpriteLibrary.entry(id), let dir = SpriteLibrary.dir else { return nil }
        let f = Frames.strip(dir.appendingPathComponent(id + ".png"), count: e.frames, fps: e.fps, pixel: e.pixel)
        cache[id] = f
        return f
    }
}

extension Animations {
    /// A pet's strip holds 8 poses of 4 frames: the 4 of one pose.
    static func pet(_ id: String, pose: Int) -> Frames? {
        let key = "\(id)#\(pose)"
        if let f = cache[key] { return f }
        guard let e = SpriteLibrary.entry(id), let dir = SpriteLibrary.dir,
              let all = Frames.strip(dir.appendingPathComponent(id + ".png"), count: e.frames, fps: e.fps, pixel: true) else { return nil }
        let r = (pose * 4..<pose * 4 + 4).clamped(to: 0..<all.images.count)
        guard !r.isEmpty else { return nil }
        let f = Frames(images: Array(all.images[r]), delays: Array(all.delays[r]), pixel: true)
        cache[key] = f
        return f
    }
}

struct AnimatedArt: View {
    let frames: Frames
    let size: CGFloat
    var body: some View { FramesLayerView(frames: frames).frame(width: size, height: size) }
}

/// Frames played by Core Animation, so the notch is not laid out again on every frame.
private struct FramesLayerView: NSViewRepresentable {
    let frames: Frames
    final class Coordinator { var first: CGImage?; var count = 0 }
    func makeCoordinator() -> Coordinator { Coordinator() }
    /// Clicks go through to the button or notch underneath.
    final class PassThrough: NSView { override func hitTest(_ point: NSPoint) -> NSView? { nil } }
    func makeNSView(context: Context) -> NSView {
        let v = PassThrough()
        v.wantsLayer = true
        v.layer?.contentsGravity = .resizeAspect
        return v
    }
    func updateNSView(_ v: NSView, context: Context) {
        guard let layer = v.layer else { return }
        let c = context.coordinator
        guard c.first !== frames.images.first || c.count != frames.images.count else { return }
        c.first = frames.images.first; c.count = frames.images.count
        layer.magnificationFilter = frames.pixel ? .nearest : .linear
        layer.contents = frames.images.first
        layer.removeAnimation(forKey: "frames")
        guard frames.images.count > 1, frames.total > 0 else { return }
        let a = CAKeyframeAnimation(keyPath: "contents")
        a.values = frames.images
        var t = 0.0
        a.keyTimes = frames.delays.map { d in defer { t += d }; return NSNumber(value: t / frames.total) }
        a.calculationMode = .discrete
        a.duration = frames.total
        a.repeatCount = .infinity
        a.isRemovedOnCompletion = false
        // copies of one animation stay in step, as before (Animations.epoch)
        a.timeOffset = (Date().timeIntervalSinceReferenceDate - Animations.epoch).truncatingRemainder(dividingBy: frames.total)
        layer.add(a, forKey: "frames")
    }
}
