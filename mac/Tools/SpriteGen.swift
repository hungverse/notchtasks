// Draws the built-in animated icons into Resources/Sprites as PNG strips (square frames, left to right) plus catalog.json.
// Run from the project folder:  swiftc -O Tools/SpriteGen.swift -o build/spritegen && build/spritegen
// Characters are original designs; "AI" ones only borrow a color mood, never a logo.
import AppKit
import CoreGraphics
import Foundation

// MARK: - Pixel canvas (16×16 art, exported at native size; the app scales it up without smoothing)

final class Pix {
    let w: Int, h: Int
    var px: [UInt32?]
    init(_ w: Int = 16, _ h: Int = 16) { self.w = w; self.h = h; px = Array(repeating: nil, count: w * h) }
    func set(_ x: Int, _ y: Int, _ c: UInt32?) { if x >= 0, y >= 0, x < w, y < h { px[y * w + x] = c } }
    func rect(_ x: Int, _ y: Int, _ rw: Int, _ rh: Int, _ c: UInt32) {
        guard rw > 0, rh > 0 else { return }
        for j in y..<y + rh { for i in x..<x + rw { set(i, j, c) } }
    }
    func disc(_ cx: Double, _ cy: Double, _ r: Double, _ c: UInt32) {
        for y in 0..<h { for x in 0..<w where hypot(Double(x) + 0.5 - cx, Double(y) + 0.5 - cy) <= r { set(x, y, c) } }
    }
    func line(_ x0: Int, _ y0: Int, _ x1: Int, _ y1: Int, _ c: UInt32) {
        var x = x0, y = y0
        let dx = abs(x1 - x0), dy = -abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1
        var err = dx + dy
        while true {
            set(x, y, c)
            if x == x1 && y == y1 { break }
            let e2 = 2 * err
            if e2 >= dy { err += dy; x += sx }
            if e2 <= dx { err += dx; y += sy }
        }
    }
    /// ASCII art: "." is transparent, any other character looks up the palette.
    func art(_ rows: [String], _ ox: Int = 0, _ oy: Int = 0, _ pal: [Character: UInt32]) {
        for (j, row) in rows.enumerated() { for (i, ch) in row.enumerated() where ch != "." { if let c = pal[ch] { set(ox + i, oy + j, c) } } }
    }
    /// 1px outline around everything drawn so far.
    func outline(_ c: UInt32) {
        let snap = px
        for y in 0..<h { for x in 0..<w where snap[y * w + x] == nil {
            let near = [(1, 0), (-1, 0), (0, 1), (0, -1)].contains { d in
                let (nx, ny) = (x + d.0, y + d.1)
                return nx >= 0 && ny >= 0 && nx < w && ny < h && snap[ny * w + nx] != nil
            }
            if near { px[y * w + x] = c }
        } }
    }
    func cgImage() -> CGImage {
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        for (i, c) in px.enumerated() {
            guard let c else { continue }
            bytes[i * 4] = UInt8((c >> 16) & 0xFF); bytes[i * 4 + 1] = UInt8((c >> 8) & 0xFF); bytes[i * 4 + 2] = UInt8(c & 0xFF); bytes[i * 4 + 3] = 255
        }
        let ctx = CGContext(data: &bytes, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        return ctx.makeImage()!
    }
}

// MARK: - Vector frames (96×96, y up as CoreGraphics likes it)

let V = 96.0
func rgb(_ hex: UInt32, _ a: Double = 1) -> CGColor {
    CGColor(srgbRed: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255, alpha: a)
}
func vector(_ draw: (CGContext) -> Void) -> CGImage {
    let ctx = CGContext(data: nil, width: Int(V), height: Int(V), bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    draw(ctx)
    return ctx.makeImage()!
}
extension CGContext {
    func fillEllipse(_ x: Double, _ y: Double, _ w: Double, _ h: Double, _ c: CGColor) { setFillColor(c); fillEllipse(in: CGRect(x: x, y: y, width: w, height: h)) }
    func fillCircle(_ cx: Double, _ cy: Double, _ r: Double, _ c: CGColor) { fillEllipse(cx - r, cy - r, r * 2, r * 2, c) }
    func fillRound(_ x: Double, _ y: Double, _ w: Double, _ h: Double, _ r: Double, _ c: CGColor) {
        setFillColor(c); addPath(CGPath(roundedRect: CGRect(x: x, y: y, width: w, height: h), cornerWidth: min(r, w / 2), cornerHeight: min(r, h / 2), transform: nil)); fillPath()
    }
    func strokeLine(_ pts: [CGPoint], _ c: CGColor, _ width: Double) {
        setStrokeColor(c); setLineWidth(width); setLineCap(.round); setLineJoin(.round); addLines(between: pts); strokePath()
    }
    func star(_ cx: Double, _ cy: Double, _ rOut: Double, _ rIn: Double, _ points: Int, _ rot: Double, _ c: CGColor) {
        setFillColor(c)
        for i in 0..<points * 2 {
            let r = i % 2 == 0 ? rOut : rIn
            let a = rot + Double(i) * .pi / Double(points)
            let p = CGPoint(x: cx + cos(a) * r, y: cy + sin(a) * r)
            if i == 0 { move(to: p) } else { addLine(to: p) }
        }
        closePath(); fillPath()
    }
}

// MARK: - The set

struct Sprite { let id, title, category: String; let fps: Double; let pixel: Bool; let frames: [CGImage] }
var sprites: [Sprite] = []
func pixel(_ id: String, _ title: String, _ cat: String, fps: Double, frames n: Int, _ draw: (Int, Pix) -> Void) {
    sprites.append(Sprite(id: id, title: title, category: cat, fps: fps, pixel: true, frames: (0..<n).map { f in let p = Pix(); draw(f, p); return p.cgImage() }))
}
func vec(_ id: String, _ title: String, _ cat: String, fps: Double, frames n: Int, _ draw: (Double, CGContext) -> Void) {
    sprites.append(Sprite(id: id, title: title, category: cat, fps: fps, pixel: false, frames: (0..<n).map { f in vector { draw(Double(f) / Double(n), $0) } }))
}

let K: UInt32 = 0x1B1B24, W: UInt32 = 0xFFFFFF
let tau = Double.pi * 2

// ---------- AI agents (pixel) ----------

pixel("px-buddy", "Bé cam", "AI", fps: 6, frames: 4) { f, p in
    let O: UInt32 = 0xE8875B, D: UInt32 = 0xB85F3A
    let bob = f % 2
    p.art(["..OOOOOOOO..",
           ".OOOOOOOOOO.",
           "OOOOOOOOOOOO",
           "OOOOOOOOOOOO",
           "OOOOOOOOOOOO",
           "OOOOOOOOOOOO",
           "DOOOOOOOOOOD",
           ".DDDDDDDDDD."], 2, 4 + bob, ["O": O, "D": D])
    let eye = f == 3 ? 1 : 2
    p.rect(5, 7 + bob, 1, eye, K); p.rect(10, 7 + bob, 1, eye, K)
    p.rect(0, 8 + bob, 2, 1, O); p.rect(14, 8 + bob, 2, 1, O)
    let legs = f % 2 == 0 ? [4, 6, 9, 11] : [3, 5, 10, 12]
    for x in legs { p.rect(x, 12 + bob, 1, 2 - bob, D) }
    p.set(7 + (f % 2), 2 + bob, 0xFFD27A); p.set(8 - (f % 2), 1 + bob, 0xFFE9B0)
}

pixel("px-spark", "Tia sáng", "AI", fps: 8, frames: 6) { f, p in
    let O: UInt32 = 0xE8875B, L: UInt32 = 0xF6B38F
    let phase = [0, 1, 2, 3, 2, 1][f]
    let long = 3 + phase, short = 2 + phase / 2
    for i in 1...long {
        let c = i == long ? L : O
        p.rect(7, 7 - i, 2, 1, c); p.rect(7, 8 + i, 2, 1, c); p.rect(7 - i, 7, 1, 2, c); p.rect(8 + i, 7, 1, 2, c)
    }
    for i in 1...short { p.set(7 - i, 7 - i, L); p.set(8 + i, 7 - i, L); p.set(7 - i, 8 + i, L); p.set(8 + i, 8 + i, L) }
    p.rect(7, 7, 2, 2, W)
}

pixel("px-cloud-term", "Mây dòng lệnh", "AI", fps: 4, frames: 4) { f, p in
    let C: UInt32 = 0xEDEFF5, S: UInt32 = 0xB7BCCB
    let bob = f < 2 ? 0 : 1
    p.art(["....CCCC........",
           "..CCCCCCC.CCC...",
           ".CCCCCCCCCCCCCC.",
           "CCCCCCCCCCCCCCCC",
           "CCCCCCCCCCCCCCCC",
           "CCCCCCCCCCCCCCCC",
           ".SSSSSSSSSSSSSS."], 0, 4 + bob, ["C": C, "S": S])
    p.art(["G..", ".G.", "G.."], 3, 7 + bob, ["G": K])
    if f % 2 == 0 { p.rect(7, 9 + bob, 4, 1, K) }
}

pixel("px-rocket", "Tên lửa", "AI", fps: 10, frames: 4) { f, p in
    let B: UInt32 = 0xE9ECF5, R: UInt32 = 0x6C7BFF, Wn: UInt32 = 0x7FE3FF
    let up = f % 2
    p.art(["...BB...",
           "..BBBB..",
           "..BBBB..",
           ".BBWWBB.",
           ".BBWWBB.",
           ".BBBBBB.",
           ".BBBBBB.",
           "RBBBBBBR",
           "RR.BB.RR"], 4, 1 - up, ["B": B, "R": R, "W": Wn])
    let flame: [UInt32] = [0xFFD166, 0xFF8C42, 0xFF5D5D]
    for i in 0..<(3 + (f % 3)) { p.rect(7, 10 - up + i, 2, 1, flame[min(i, 2)]) }
    for (x, y) in [(1, (3 + f * 3) % 14), (14, (8 + f * 4) % 14), (2, (12 + f * 2) % 14)] { p.set(x, y, 0xC9D2FF) }
}

pixel("px-astro", "Phi hành gia", "AI", fps: 5, frames: 6) { f, p in
    let S: UInt32 = 0xF1F3F8, G: UInt32 = 0x2A2F45, Vz: UInt32 = 0x7FC8FF, P: UInt32 = 0xA48BFF
    let bob = [0, 0, 1, 1, 1, 0][f]
    p.art([".SSSSS.",
           "SSSSSSS",
           "SGGVGGS",
           "SGVVVGS",
           "SSSSSSS",
           ".PSSSP.",
           "SSSSSSS",
           "S.SSS.S",
           "..S.S.."], 4, 3 + bob, ["S": S, "G": G, "V": Vz, "P": P])
    p.outline(K)
    for (i, (x, y)) in [(1, 2), (14, 4), (1, 12), (14, 13)].enumerated() where (f + i) % 3 != 0 { p.set(x, y, 0xFFF3B0) }
}

pixel("px-gem", "Sao xanh", "AI", fps: 8, frames: 6) { f, p in
    let r = [3, 4, 5, 6, 5, 4][f]
    let B: UInt32 = 0x5B8CFF, L: UInt32 = 0xB9A7FF
    for y in 0..<16 { for x in 0..<16 {
        let dx = abs(Double(x) + 0.5 - 8), dy = abs(Double(y) + 0.5 - 8)
        // a 4-point star: |x|^0.5 + |y|^0.5 below the radius
        if sqrt(dx) + sqrt(dy) <= sqrt(Double(r)) * 1.25 { p.set(x, y, dx + dy < Double(r) * 0.45 ? L : B) }
    } }
    p.rect(7, 7, 2, 2, W)
    if f == 3 { p.set(13, 2, W); p.set(2, 13, W) }
}

pixel("px-robot", "Robot nhỏ", "AI", fps: 4, frames: 4) { f, p in
    let M: UInt32 = 0x9AA5B8, D: UInt32 = 0x5D6678, E: UInt32 = 0x5CF2C9
    p.line(8, 1, 8, 3, D); p.set(8, 0, f % 2 == 0 ? 0xFF5D5D : 0xFFB3B3)
    p.rect(3, 4, 10, 8, M); p.rect(3, 11, 10, 1, D)
    p.rect(4, 5, 8, 4, 0x2A2F45)
    p.rect(5, f == 2 ? 7 : 6, 2, f == 2 ? 1 : 2, E); p.rect(9, f == 2 ? 7 : 6, 2, f == 2 ? 1 : 2, E)
    for x in stride(from: 5, through: 10, by: 2) { p.set(x, 10, f % 2 == 0 ? E : D) }
    p.rect(1, 6, 2, 4, D); p.rect(13, 6, 2, 4, D)
    p.rect(5, 12, 6, 3, D)
}

pixel("px-think", "Đang nghĩ", "AI", fps: 5, frames: 4) { f, p in
    let H: UInt32 = 0xF4C9A8, Hr: UInt32 = 0x4A3A5C
    p.disc(7, 10, 4.6, H)
    p.rect(3, 5, 9, 2, Hr); p.rect(3, 6, 2, 3, Hr)
    p.set(6, 10, K); p.set(9, 10, K)
    p.rect(6, 12, 3, 1, 0xC46A5A)
    for (i, d) in [(11, 4), (13, 2), (14, 0)].enumerated() where i <= f { p.rect(d.0, d.1, i == 2 ? 2 : 1, i == 2 ? 2 : 1, W) }
}

// ---------- Build & grind (pixel) ----------

pixel("px-miner", "Thợ mỏ", "Cày cuốc", fps: 8, frames: 4) { f, p in
    let Hm: UInt32 = 0xFFC83D, Sk: UInt32 = 0xF2C29B, Sh: UInt32 = 0x3C6FD1, Pn: UInt32 = 0x4A3B30, R: UInt32 = 0x8A8F9E, Rd: UInt32 = 0x5E6270
    let Wd: UInt32 = 0x8B5A2B, Mt: UInt32 = 0xC8CCD6
    p.rect(2, 3, 5, 2, Hm); p.set(7, 4, Hm); p.set(4, 2, 0xFFF3B0)
    p.rect(2, 5, 4, 3, Sk); p.set(5, 6, K)
    p.rect(2, 8, 5, 4, Sh); p.rect(2, 12, 2, 3, Pn); p.rect(5, 12, 2, 3, Pn)
    p.art([".RRRR.", "RRRRRR", "RRDRRR", "RRRRDR"], 10, 11, ["R": R, "D": Rd])
    switch f {
    case 0: p.line(6, 8, 8, 2, Wd); p.line(6, 1, 10, 3, Mt)
    case 2: p.line(6, 9, 11, 9, Wd); p.line(11, 7, 12, 11, Mt)
            p.set(14, 9, 0xFFE066); p.set(15, 7, 0xFFE066); p.set(13, 8, 0xFFF3B0)
    default: p.line(6, 9, 10, 5, Wd); p.line(9, 3, 12, 6, Mt)
    }
}

pixel("px-anvil", "Rèn sắt", "Cày cuốc", fps: 8, frames: 4) { f, p in
    let A: UInt32 = 0x4C5466, Al: UInt32 = 0x6E778B, Hd: UInt32 = 0x8B5A2B, Hh: UInt32 = 0xB9C0CF, Hot: UInt32 = 0xFF8C42
    p.rect(2, 10, 12, 2, Al); p.rect(4, 12, 8, 1, A); p.rect(6, 13, 4, 1, A); p.rect(4, 14, 8, 2, A)
    p.rect(5, 9, 6, 1, Hot)
    let hy = [1, 4, 6, 3][f]
    p.line(12, hy + 1, 15, hy + 1 - (f == 0 ? 1 : 0), Hd)
    p.rect(9, hy, 4, 3, Hh)
    if f == 2 { for (x, y) in [(4, 7), (3, 5), (13, 6), (14, 8), (6, 6)] { p.set(x, y, 0xFFD166) } }
}

pixel("px-gears", "Bánh răng nhỏ", "Cày cuốc", fps: 10, frames: 6) { f, p in
    func gear(_ cx: Double, _ cy: Double, _ r: Double, _ teeth: Double, _ rot: Double, _ c: UInt32) {
        for y in 0..<16 { for x in 0..<16 {
            let dx = Double(x) + 0.5 - cx, dy = Double(y) + 0.5 - cy, d = hypot(dx, dy), a = atan2(dy, dx)
            let edge = cos(teeth * (a - rot)) > 0.3 ? r : r - 1.4
            if d <= edge && d > r * 0.35 { p.set(x, y, c) }
        } }
    }
    let t = Double(f) / 6 * tau / 8
    gear(5.5, 6, 5, 8, t, 0xB9C0CF)
    gear(11.5, 11.5, 4, 6, -t * 8 / 6 + 0.3, 0xFFB347)
}

pixel("px-hamster", "Chuột lăn bánh", "Cày cuốc", fps: 10, frames: 4) { f, p in
    let Wh: UInt32 = 0x8E97AA, Hm: UInt32 = 0xF0B46A, Bl: UInt32 = 0xFBE3C3
    for y in 0..<16 { for x in 0..<16 {
        let d = hypot(Double(x) + 0.5 - 8, Double(y) + 0.5 - 8)
        if d <= 7.5 && d > 6.4 { p.set(x, y, Wh) }
    } }
    let a = Double(f) / 4 * .pi / 2
    for k in 0..<4 { let ang = a + Double(k) * .pi / 2; p.line(8, 8, 8 + Int((cos(ang) * 6).rounded()), 8 + Int((sin(ang) * 6).rounded()), 0x5D6678) }
    p.rect(5, 9, 6, 4, Hm); p.rect(6, 10, 4, 2, Bl); p.rect(10, 8, 3, 3, Hm); p.set(12, 9, K); p.set(10, 7, Hm)
    for x in (f % 2 == 0 ? [5, 9] : [6, 10]) { p.set(x, 13, 0xD9866B) }
}

pixel("px-coffee", "Cà phê", "Cày cuốc", fps: 5, frames: 4) { f, p in
    let M: UInt32 = 0xF4F1EA, C: UInt32 = 0x6B4226, Ms: UInt32 = 0xCFC8BA, Ht: UInt32 = 0xE36A6A
    p.rect(3, 8, 8, 7, M); p.rect(3, 14, 8, 1, Ms); p.rect(4, 8, 6, 1, C)
    p.rect(11, 9, 2, 1, M); p.rect(12, 10, 1, 3, M); p.rect(11, 12, 2, 1, M)
    for (x, y) in [(6, 10), (8, 10), (6, 11), (7, 11), (8, 11), (7, 12)] { p.set(x, y, Ht) }
    for s in 0..<2 { for i in 0..<3 { p.set(5 + s * 3 + ((i + f + s) % 2), 6 - i - (f % 2), 0xC9CED9) } }
}

pixel("px-loading", "Thanh tiến trình", "Cày cuốc", fps: 8, frames: 8) { f, p in
    p.rect(1, 6, 14, 5, 0x3A4156); p.rect(2, 7, 12, 3, 0x22263A)
    for i in 0..<min(12, (f + 1) * 12 / 8) { p.rect(2 + i, 7, 1, 3, i % 2 == 0 ? 0x5CF2C9 : 0x3DD9AE) }
    if f == 7 { p.set(14, 4, 0xFFE066); p.set(1, 4, 0xFFE066) }
}

pixel("px-fire", "Lửa trại", "Cày cuốc", fps: 8, frames: 4) { f, p in
    let rows: [[String]] = [
        ["......Y.........", ".....YY.........", ".....YOY..Y.....", "....YOOY.YY.....", "....YOROYOY.....", "...YORRROOY.....", "...YORRRROY.....", "....YORRROY....."],
        [".........Y......", "........YY......", ".....Y.YOY......", "....YY.YOOY.....", "....YOYORROY....", "...YOORRRROY....", "...YORRRRROY....", "....YORRROY....."],
        [".......Y........", "......YYY.......", ".....YOOY.......", "....YOROY.Y.....", "....YORROYY.....", "...YORRRROY.....", "...YORRRRROY....", "....YOORROY....."],
        ["........Y.......", ".......YY.......", "...Y..YOY.......", "...YY.YOOY......", "....YOYORRY.....", "...YOORRRROY....", "...YORRRRROY....", "....YORRROY....."]]
    p.art(rows[f], 1, 3, ["Y": 0xFFD166, "O": 0xFF8C42, "R": 0xFF4D4D])
    p.line(2, 13, 13, 11, 0x7A4A2A); p.line(2, 11, 13, 13, 0x8B5A2B); p.rect(3, 14, 10, 1, 0x5A3A22)
}

pixel("px-bug", "Bắt bug", "Cày cuốc", fps: 8, frames: 4) { f, p in
    let G: UInt32 = 0x58C46A, D: UInt32 = 0x2F7A3D
    let x0 = [2, 3, 4, 3][f]
    p.disc(Double(x0) + 4.5, 10.5, 3.6, G); p.line(x0 + 4, 7, x0 + 4, 14, D)
    p.rect(x0 + 8, 9, 2, 3, D); p.set(x0 + 10, 8, D); p.set(x0 + 10, 12, D)
    for (i, ly) in [8, 10, 12].enumerated() { let sw = (f + i) % 2; p.set(x0, ly - sw, D); p.set(x0 + 8, ly + 2 - sw, D) }
    let mx = [10, 11, 12, 11][f]
    for y in 0..<16 { for x in 0..<16 { let d = hypot(Double(x) + 0.5 - Double(mx), Double(y) + 0.5 - 4); if d <= 2.8 && d > 1.7 { p.set(x, y, 0xD9DEE8) } } }
    p.line(mx - 2, 6, mx - 4, 8, 0x8B5A2B)
}

pixel("px-coin", "Đồng xu", "Cày cuốc", fps: 10, frames: 6) { f, p in
    let w = [6, 5, 3, 1, 3, 5][f]
    let Y: UInt32 = 0xFFC83D, Dk: UInt32 = 0xD99A1E
    for y in 3..<13 {
        let dy = Double(y) + 0.5 - 8, half = Double(w) * sqrt(max(0, 1 - dy * dy / 25))
        let lo = Int((8 - half).rounded()), hi = Int((8 + half).rounded())
        if hi > lo { for x in lo..<hi { p.set(x, y, x == lo || x == hi - 1 ? Dk : Y) } } else { p.set(8, y, Dk) }
    }
    if w >= 5 { p.rect(7, 6, 2, 4, Dk) }
    if f == 0 { p.set(11, 4, W) }
}

// ---------- Cute (pixel) ----------

pixel("px-cat", "Mèo", "Dễ thương", fps: 5, frames: 6) { f, p in
    let C: UInt32 = 0xF2A65A, Cd: UInt32 = 0xC97A35, Pk: UInt32 = 0xFF9EAA
    p.art(["C....C", "CC..CC", "CCCCCC", "CCCCCC", "CCCCCC", ".CCCC."], 3, 3, ["C": C])
    p.set(3, 4, Pk); p.set(8, 4, Pk)
    let blink = f == 4
    p.rect(4, blink ? 7 : 6, 1, blink ? 1 : 2, K); p.rect(7, blink ? 7 : 6, 1, blink ? 1 : 2, K); p.set(5, 8, Pk); p.set(6, 8, Pk)
    p.rect(3, 9, 6, 5, C); p.rect(4, 10, 4, 3, 0xFFD7A8); p.rect(3, 14, 2, 1, Cd); p.rect(7, 14, 2, 1, Cd)
    let tail: [[(Int, Int)]] = [[(9, 13), (10, 12), (11, 11), (11, 10)], [(9, 13), (10, 13), (11, 12), (12, 11)], [(9, 13), (10, 13), (11, 13), (12, 12)]]
    for (x, y) in tail[[0, 1, 2, 1, 0, 1][f]] { p.set(x, y, Cd) }
}

pixel("px-slime", "Slime", "Dễ thương", fps: 8, frames: 6) { f, p in
    let G: UInt32 = 0x6FE0A4, L: UInt32 = 0xB8F5D3, D: UInt32 = 0x2FA86B
    let (wd, ht, lift) = [(12, 6, 0), (10, 8, 1), (8, 10, 4), (9, 9, 5), (10, 8, 2), (13, 5, 0)][f]
    let top = 15 - ht - lift, left = 8 - wd / 2
    for j in 0..<ht { let shrink = j == 0 ? 2 : j == 1 ? 1 : 0; p.rect(left + shrink, top + j, wd - shrink * 2, 1, j == ht - 1 ? D : G) }
    p.set(left + 2, top + 1, L); p.set(left + 3, top + 1, L)
    p.set(left + wd / 2 - 2, top + ht / 2, K); p.set(left + wd / 2 + 1, top + ht / 2, K)
    if lift > 0 { p.rect(5, 15, 6, 1, 0x2A2F45) }
}

pixel("px-ghost", "Ma nhỏ", "Dễ thương", fps: 6, frames: 4) { f, p in
    let Gh: UInt32 = 0xF3F0FF
    let bob = [0, 1, 1, 0][f]
    p.art(["..GGGG..", ".GGGGGG.", "GGGGGGGG", "GGGGGGGG", "GGGGGGGG", "GGGGGGGG", "GGGGGGGG", "GGGGGGGG"], 4, 2 + bob, ["G": Gh])
    p.art([f % 2 == 0 ? "G.GG.GG." : ".GG.GG.G"], 4, 10 + bob, ["G": Gh])
    p.rect(6, 5 + bob, 1, 2, K); p.rect(9, 5 + bob, 1, 2, K); p.set(5, 7 + bob, 0xFFB3C1); p.set(10, 7 + bob, 0xFFB3C1)
    p.outline(0x7C6FB0)
}

pixel("px-duck", "Vịt cao su", "Dễ thương", fps: 5, frames: 4) { f, p in
    let Y: UInt32 = 0xFFD43B, O: UInt32 = 0xFF8C1A, Wt: UInt32 = 0x6EC6FF, Wd: UInt32 = 0x3A9BE0
    let bob = [0, 1, 1, 0][f]
    p.art(["..YYY....", ".YYYYY...", ".YKYYYOO.", ".YYYYYOO.", "..YYYY...", "YYYYYYYYY", "YYYYYYYYY", ".YYYYYYY."], 3, 4 + bob, ["Y": Y, "O": O, "K": K])
    p.rect(0, 12, 16, 4, Wt)
    for x in 0..<16 where (x + f) % 4 == 0 { p.set(x, 12, Wd) }
}

pixel("px-heart", "Tim đập", "Dễ thương", fps: 6, frames: 4) { f, p in
    let big = f == 1
    let R: UInt32 = 0xFF4D6D, L: UInt32 = 0xFF9FB2
    let art = big ? [".RR...RR.", "RRRR.RRRR", "RRRRRRRRR", "RRRRRRRRR", ".RRRRRRR.", "..RRRRR..", "...RRR...", "....R...."]
                  : [".RR.RR.", "RRRRRRR", "RRRRRRR", ".RRRRR.", "..RRR..", "...R..."]
    p.art(art, big ? 3 : 4, big ? 4 : 5, ["R": R])
    p.set(big ? 4 : 5, big ? 5 : 6, L)
    p.outline(0x8E1B33)
}

pixel("px-penguin", "Cánh cụt", "Dễ thương", fps: 6, frames: 4) { f, p in
    let B: UInt32 = 0x2A2F45, Wt: UInt32 = 0xF4F6FB, O: UInt32 = 0xFFA931
    let tilt = [0, 1, 0, -1][f]
    p.art(["..BBBB..", ".BBBBBB.", "BBWWWWBB", "BWKWWKWB", "BWWOOWWB", "BWWWWWWB", "BWWWWWWB", ".BWWWWB.", "..BBBB.."], 4 + tilt, 2, ["B": B, "W": Wt, "K": K, "O": O])
    p.rect(2 + tilt, 7, 2, 3, B); p.rect(12 + tilt, 7, 2, 3, B)
    p.rect(5 + tilt, 11, 2, 1, O); p.rect(9 + tilt, 11, 2, 1, O)
    p.outline(0x9AA5B8)
}

// ---------- Robot, anime and AI moods (vector) ----------

vec("v-robot", "Robot", "Robot", fps: 12, frames: 24) { t, c in
    let bob = sin(t * tau) * 2
    c.strokeLine([CGPoint(x: 48, y: 74 + bob), CGPoint(x: 48, y: 84 + bob)], rgb(0x9AA5B8), 4)
    c.fillCircle(48, 86 + bob, 5, rgb(t < 0.5 ? 0xFF5D5D : 0xFF9B9B))
    c.fillRound(16, 22 + bob, 64, 52, 16, rgb(0xD5DBE6))
    c.fillRound(22, 30 + bob, 52, 34, 10, rgb(0x1E2233))
    let scan = sin(t * tau) * 6
    let blink = (0.82...0.9).contains(t)
    for ex in [36.0, 60.0] { if blink { c.fillRound(ex - 7 + scan, 46 + bob, 14, 3, 1.5, rgb(0x5CF2C9)) } else { c.fillCircle(ex + scan, 47 + bob, 7, rgb(0x5CF2C9)) } }
    c.fillRound(8, 40 + bob, 8, 18, 4, rgb(0x9AA5B8)); c.fillRound(80, 40 + bob, 8, 18, 4, rgb(0x9AA5B8))
    c.fillRound(34, 10 + bob, 28, 8, 4, rgb(0x9AA5B8))
}

vec("v-mecha", "Robot chiến", "Robot", fps: 12, frames: 24) { t, c in
    let glow = 0.5 + 0.5 * sin(t * tau * 2)
    c.setFillColor(rgb(0x3A4156))
    c.move(to: CGPoint(x: 20, y: 30)); c.addLine(to: CGPoint(x: 76, y: 30)); c.addLine(to: CGPoint(x: 84, y: 60)); c.addLine(to: CGPoint(x: 66, y: 82))
    c.addLine(to: CGPoint(x: 30, y: 82)); c.addLine(to: CGPoint(x: 12, y: 60)); c.closePath(); c.fillPath()
    c.setFillColor(rgb(0x5D6678))
    c.move(to: CGPoint(x: 30, y: 82)); c.addLine(to: CGPoint(x: 40, y: 92)); c.addLine(to: CGPoint(x: 44, y: 80)); c.fillPath()
    c.move(to: CGPoint(x: 66, y: 82)); c.addLine(to: CGPoint(x: 56, y: 92)); c.addLine(to: CGPoint(x: 52, y: 80)); c.fillPath()
    c.fillRound(22, 50, 52, 12, 6, rgb(0x111522))
    c.fillRound(26 + t * 34, 52, 10, 8, 4, rgb(0xFF4D6D, 0.6 + 0.4 * glow))
    c.fillRound(34, 36, 28, 6, 3, rgb(0x7FE3FF, glow))
}

vec("v-chibi", "Chibi", "Anime", fps: 12, frames: 24) { t, c in
    let bob = sin(t * tau) * 1.5
    c.fillCircle(48, 44 + bob, 32, rgb(0xFFE2CF))
    c.setFillColor(rgb(0x4B3F72))
    c.addArc(center: CGPoint(x: 48, y: 46 + bob), radius: 34, startAngle: 0.12 * .pi, endAngle: 0.88 * .pi, clockwise: false); c.closePath(); c.fillPath()
    c.fillEllipse(14, 34 + bob, 18, 34, rgb(0x4B3F72)); c.fillEllipse(64, 34 + bob, 18, 34, rgb(0x4B3F72))
    let tuft = sin(t * tau * 2) * 6
    c.strokeLine([CGPoint(x: 48, y: 78 + bob), CGPoint(x: 52 + tuft, y: 90 + bob)], rgb(0x4B3F72), 5)
    let blink = (0.7...0.78).contains(t)
    for ex in [35.0, 61.0] {
        if blink { c.strokeLine([CGPoint(x: ex - 8, y: 40 + bob), CGPoint(x: ex + 8, y: 40 + bob)], rgb(0x2A2238), 3) }
        else {
            c.fillEllipse(ex - 8, 30 + bob, 16, 20, rgb(0x2A2238)); c.fillEllipse(ex - 5, 32 + bob, 10, 12, rgb(0x7E6BFF))
            c.fillCircle(ex + 3, 44 + bob, 3.5, rgb(W)); c.fillCircle(ex - 3, 36 + bob, 1.8, rgb(W))
        }
    }
    c.fillEllipse(20, 24 + bob, 12, 6, rgb(0xFF8FA3, 0.6)); c.fillEllipse(64, 24 + bob, 12, 6, rgb(0xFF8FA3, 0.6))
    c.strokeLine([CGPoint(x: 44, y: 22 + bob), CGPoint(x: 48, y: 19 + bob), CGPoint(x: 52, y: 22 + bob)], rgb(0x8A4A4A), 2.5)
}

vec("v-neko", "Mèo anime", "Anime", fps: 12, frames: 24) { t, c in
    let ear = t < 0.5 ? sin(t * tau * 2) * 4 : 0
    c.setFillColor(rgb(0xF3F0FF))
    c.move(to: CGPoint(x: 18, y: 58)); c.addLine(to: CGPoint(x: 22 - ear, y: 88)); c.addLine(to: CGPoint(x: 42, y: 70)); c.fillPath()
    c.move(to: CGPoint(x: 78, y: 58)); c.addLine(to: CGPoint(x: 74 + ear, y: 88)); c.addLine(to: CGPoint(x: 54, y: 70)); c.fillPath()
    c.fillEllipse(12, 12, 72, 66, rgb(0xF3F0FF))
    c.setFillColor(rgb(0xFFB3C6)); c.move(to: CGPoint(x: 24, y: 66)); c.addLine(to: CGPoint(x: 26 - ear, y: 80)); c.addLine(to: CGPoint(x: 36, y: 70)); c.fillPath()
    c.move(to: CGPoint(x: 72, y: 66)); c.addLine(to: CGPoint(x: 70 + ear, y: 80)); c.addLine(to: CGPoint(x: 60, y: 70)); c.fillPath()
    let blink = (0.86...0.94).contains(t)
    for ex in [34.0, 62.0] {
        if blink { c.strokeLine([CGPoint(x: ex - 7, y: 44), CGPoint(x: ex, y: 40), CGPoint(x: ex + 7, y: 44)], rgb(0x2A2238), 3) }
        else { c.fillEllipse(ex - 6, 36, 12, 16, rgb(0x2A2238)); c.fillCircle(ex + 2, 47, 3, rgb(W)) }
    }
    c.strokeLine([CGPoint(x: 42, y: 30), CGPoint(x: 48, y: 26), CGPoint(x: 54, y: 30)], rgb(0x2A2238), 2.5)
    c.fillEllipse(20, 26, 12, 6, rgb(0xFF8FA3, 0.55)); c.fillEllipse(64, 26, 12, 6, rgb(0xFF8FA3, 0.55))
}

vec("v-kawaii-cloud", "Mây dễ thương", "Anime", fps: 12, frames: 24) { t, c in
    let bob = sin(t * tau) * 3
    for (x, y, r) in [(30.0, 46.0, 18.0), (50, 56, 22), (68, 46, 17), (48, 40, 20)] { c.fillCircle(x, y + bob, r, rgb(W)) }
    let blink = (0.6...0.68).contains(t)
    for ex in [40.0, 58.0] { if blink { c.strokeLine([CGPoint(x: ex - 4, y: 48 + bob), CGPoint(x: ex + 4, y: 48 + bob)], rgb(0x3A3550), 3) } else { c.fillCircle(ex, 48 + bob, 3.6, rgb(0x3A3550)) } }
    c.strokeLine([CGPoint(x: 45, y: 40 + bob), CGPoint(x: 49, y: 37 + bob), CGPoint(x: 53, y: 40 + bob)], rgb(0x3A3550), 2.5)
    c.fillEllipse(30, 38 + bob, 9, 5, rgb(0xFF9FB2, 0.7)); c.fillEllipse(60, 38 + bob, 9, 5, rgb(0xFF9FB2, 0.7))
    let drop = (t * 2).truncatingRemainder(dividingBy: 1)
    c.fillEllipse(44, 18 - drop * 14 + bob, 5, 7, rgb(0x7FC8FF, 1 - drop))
}

vec("v-spark", "Tia sáng cam", "AI", fps: 12, frames: 24) { t, c in
    let pulse = 1 + sin(t * tau) * 0.12
    c.translateBy(x: 48, y: 48); c.rotate(by: t * tau / 5)
    for i in 0..<10 {
        let len = (i % 2 == 0 ? 40.0 : 30.0) * pulse
        c.saveGState(); c.rotate(by: Double(i) / 10 * tau)
        c.fillRound(-4, 4, 8, len - 4, 4, rgb(0xD97757))
        c.restoreGState()
    }
    c.fillCircle(0, 0, 9 * pulse, rgb(0xF0A07E))
}

vec("v-orbit", "Quỹ đạo", "AI", fps: 15, frames: 30) { t, c in
    let bob = sin(t * tau) * 2
    c.fillCircle(48, 48 + bob, 20, rgb(0x6C7BFF))
    c.fillCircle(42, 54 + bob, 7, rgb(0x9AA6FF))
    c.saveGState(); c.translateBy(x: 48, y: 48 + bob); c.scaleBy(x: 1, y: 0.35)
    c.setStrokeColor(rgb(0xC9B8FF)); c.setLineWidth(4); c.strokeEllipse(in: CGRect(x: -38, y: -38, width: 76, height: 76)); c.restoreGState()
    let a = t * tau
    c.fillCircle(48 + cos(a) * 38, 48 + bob + sin(a) * 13, 6, rgb(0x7FE3FF))
    for (i, (x, y)) in [(12.0, 80.0), (84.0, 70.0), (76.0, 14.0)].enumerated() { c.star(x, y, 4, 1.5, 4, 0, rgb(W, 0.4 + 0.6 * abs(sin((t + Double(i) / 3) * tau)))) }
}

vec("v-twinkle", "Lấp lánh", "AI", fps: 12, frames: 24) { t, c in
    for (i, (x, y, s)) in [(48.0, 48.0, 34.0), (22.0, 74.0, 12.0), (76.0, 22.0, 14.0)].enumerated() {
        let k = 0.75 + 0.25 * sin((t + Double(i) * 0.33) * tau)
        c.star(x, y, s * k, s * k * 0.28, 4, .pi / 2, rgb(i == 0 ? 0x7C9CFF : 0xC6B2FF))
        if i == 0 { c.star(x, y, s * k * 0.5, s * k * 0.14, 4, .pi / 2, rgb(0xE3E9FF)) }
    }
}

vec("v-neural", "Mạng nơ-ron", "AI", fps: 12, frames: 24) { t, c in
    let nodes: [(Double, Double)] = [(18, 70), (18, 26), (48, 82), (48, 48), (48, 14), (78, 64), (78, 30)]
    let links = [(0, 2), (0, 3), (1, 3), (1, 4), (2, 5), (3, 5), (3, 6), (4, 6)]
    for (k, (a, b)) in links.enumerated() {
        let on = 0.3 + 0.7 * max(0, sin((t * 2 - Double(k) / 8) * tau))
        c.strokeLine([CGPoint(x: nodes[a].0, y: nodes[a].1), CGPoint(x: nodes[b].0, y: nodes[b].1)], rgb(0x5CF2C9, on * 0.8), 3)
    }
    for (i, n) in nodes.enumerated() { c.fillCircle(n.0, n.1, 7 + 2 * max(0, sin((t * 2 - Double(i) / 7) * tau)), rgb(i % 2 == 0 ? 0x7C9CFF : 0xB9A7FF)) }
}

vec("v-typing", "Đang gõ…", "AI", fps: 15, frames: 30) { t, c in
    c.fillRound(6, 22, 84, 52, 26, rgb(0x2E3347))
    for i in 0..<3 {
        let lift = max(0, sin((t - Double(i) * 0.15) * tau)) * 10
        c.fillCircle(28 + Double(i) * 20, 48 + lift, 8, rgb(0xE6E9F2, 0.5 + lift / 20))
    }
}

vec("v-gear", "Bánh răng", "Cày cuốc", fps: 15, frames: 30) { t, c in
    func gear(_ cx: Double, _ cy: Double, _ r: Double, _ n: Int, _ rot: Double, _ col: UInt32) {
        c.saveGState(); c.translateBy(x: cx, y: cy); c.rotate(by: rot)
        for i in 0..<n { c.saveGState(); c.rotate(by: Double(i) / Double(n) * tau); c.fillRound(-r * 0.16, r * 0.7, r * 0.32, r * 0.42, 3, rgb(col)); c.restoreGState() }
        c.fillCircle(0, 0, r * 0.85, rgb(col)); c.fillCircle(0, 0, r * 0.34, rgb(0x1E2233))
        c.restoreGState()
    }
    gear(38, 40, 28, 10, t * tau / 10, 0xB9C0CF)
    gear(72, 72, 18, 8, -t * tau / 8 + 0.2, 0xFFB347)
}

vec("v-hourglass", "Đồng hồ cát", "Cày cuốc", fps: 12, frames: 36) { t, c in
    let flip = t > 0.85 ? (t - 0.85) / 0.15 : 0
    c.translateBy(x: 48, y: 48); c.rotate(by: flip * .pi)
    let sand = min(t / 0.85, 1)
    c.fillRound(-26, 36, 52, 8, 4, rgb(0x8B5A2B)); c.fillRound(-26, -44, 52, 8, 4, rgb(0x8B5A2B))
    c.setFillColor(rgb(0xDDEBFF, 0.35))
    c.move(to: CGPoint(x: -20, y: 36)); c.addLine(to: CGPoint(x: 20, y: 36)); c.addLine(to: CGPoint(x: 3, y: 0)); c.addLine(to: CGPoint(x: 20, y: -36))
    c.addLine(to: CGPoint(x: -20, y: -36)); c.addLine(to: CGPoint(x: -3, y: 0)); c.closePath(); c.fillPath()
    let top = 30 * (1 - sand), bot = 30 * sand
    c.setFillColor(rgb(0xFFC857))
    if top > 1 { c.move(to: CGPoint(x: -3 - top * 0.55, y: top)); c.addLine(to: CGPoint(x: 3 + top * 0.55, y: top)); c.addLine(to: CGPoint(x: 2, y: 2)); c.addLine(to: CGPoint(x: -2, y: 2)); c.closePath(); c.fillPath() }
    if bot > 1 { c.move(to: CGPoint(x: -20, y: -36)); c.addLine(to: CGPoint(x: 20, y: -36)); c.addLine(to: CGPoint(x: 0, y: -36 + bot)); c.closePath(); c.fillPath() }
    if sand < 1 { c.fillRound(-1.2, -36, 2.4, 36, 1.2, rgb(0xFFC857)) }
}

vec("v-bricks", "Xếp gạch", "Cày cuốc", fps: 10, frames: 20) { t, c in
    let placed = Int(t * 6)
    let spots: [(Double, Double)] = [(14, 12), (50, 12), (32, 30), (68, 30), (14, 48), (50, 48)]
    for (i, s) in spots.enumerated() where i < placed { c.fillRound(s.0, s.1, 32, 16, 3, rgb(i % 2 == 0 ? 0xE0734F : 0xC95F3E)) }
    if placed < 6 {
        let s = spots[placed], k = (t * 6).truncatingRemainder(dividingBy: 1)
        c.fillRound(s.0, s.1 + (1 - k) * 40, 32, 16, 3, rgb(0xF08A63))
        c.strokeLine([CGPoint(x: s.0 + 16, y: s.1 + 16 + (1 - k) * 40), CGPoint(x: s.0 + 16, y: 94)], rgb(0x9AA5B8), 2)
    }
}

vec("v-blob", "Bé tròn", "Dễ thương", fps: 15, frames: 30) { t, c in
    let s = sin(t * tau)
    let w = 60 + s * 6, h = 50 - s * 6
    c.fillEllipse(48 - w / 2, 18, w, h, rgb(0xFF9EC7))
    c.fillEllipse(48 - w / 2 + 8, 18 + h * 0.55, w * 0.28, h * 0.2, rgb(0xFFD3E6))
    let blink = (0.45...0.52).contains(t)
    for ex in [38.0, 58.0] {
        if blink { c.strokeLine([CGPoint(x: ex - 4, y: 18 + h * 0.5), CGPoint(x: ex + 4, y: 18 + h * 0.5)], rgb(0x4A2A3A), 3) }
        else { c.fillCircle(ex, 18 + h * 0.5, 4, rgb(0x4A2A3A)); c.fillCircle(ex + 1.5, 19.5 + h * 0.5, 1.4, rgb(W)) }
    }
    c.strokeLine([CGPoint(x: 45, y: 18 + h * 0.34), CGPoint(x: 48, y: 15 + h * 0.34), CGPoint(x: 51, y: 18 + h * 0.34)], rgb(0x4A2A3A), 2.5)
}

vec("v-star-friend", "Sao vui", "Dễ thương", fps: 12, frames: 24) { t, c in
    c.translateBy(x: 48, y: 46); c.rotate(by: sin(t * tau) * 0.12)
    c.star(0, 0, 40, 18, 5, .pi / 2, rgb(0xFFD43B))
    c.star(0, 0, 32, 14, 5, .pi / 2, rgb(0xFFE36E))
    let blink = (0.3...0.38).contains(t)
    for ex in [-9.0, 9.0] { if blink { c.strokeLine([CGPoint(x: ex - 3, y: 2), CGPoint(x: ex + 3, y: 2)], rgb(0x5A3A10), 2.5) } else { c.fillEllipse(ex - 2.5, -1, 5, 7, rgb(0x5A3A10)) } }
    c.strokeLine([CGPoint(x: -5, y: -8), CGPoint(x: 0, y: -12), CGPoint(x: 5, y: -8)], rgb(0x5A3A10), 2.5)
}

// ---------- The iPhone's own set (iOS/Live/PhoneSprites): new characters, never the Mac's ----------
// Drawn at 32×32 (the Mac's are 16×16): shaded parts, each with its own dark outline, in the colours of the part.
// Only the pets: every icon on the phone (states, widgets, lock screen) is the chosen pet in a pose.

let macCount = sprites.count
func phone(_ id: String, _ title: String, _ pack: String, fps: Double, frames n: Int, side: Int = 32, _ draw: (Int, Pix) -> Void) {
    sprites.append(Sprite(id: id, title: title, category: pack, fps: fps, pixel: true, frames: (0..<n).map { f in let p = Pix(side, side); draw(f, p); return p.cgImage() }))
}

/// A colour toward black (k < 1) or toward white (k > 1).
func tone(_ c: UInt32, _ k: Double) -> UInt32 {
    func ch(_ v: UInt32) -> UInt32 { let d = Double(v); return UInt32(k < 1 ? d * k : d + (255 - d) * (k - 1)) }
    return ch((c >> 16) & 0xFF) << 16 | ch((c >> 8) & 0xFF) << 8 | ch(c & 0xFF)
}

extension Pix {
    func inside(_ x: Int, _ y: Int, _ cx: Double, _ cy: Double, _ rx: Double, _ ry: Double) -> Bool {
        let dx = (Double(x) + 0.5 - cx) / rx, dy = (Double(y) + 0.5 - cy) / ry
        return dx * dx + dy * dy <= 1
    }
    func ell(_ cx: Double, _ cy: Double, _ rx: Double, _ ry: Double, _ c: UInt32) {
        for y in 0..<h { for x in 0..<w where inside(x, y, cx, cy, rx, ry) { set(x, y, c) } }
    }
    /// A round part lit from the upper left: a light crescent, the colour, a shadow lower right.
    func ball(_ cx: Double, _ cy: Double, _ rx: Double, _ ry: Double, _ c: UInt32) {
        for y in 0..<h { for x in 0..<w where inside(x, y, cx, cy, rx, ry) {
            let nx = (Double(x) + 0.5 - cx) / rx, ny = (Double(y) + 0.5 - cy) / ry
            let lit = -0.6 * nx - 0.8 * ny, dark = 0.45 * nx + 0.9 * ny
            set(x, y, lit > 0.62 ? tone(c, 1.35) : dark > 0.62 ? tone(c, 0.8) : c)
        } }
    }
    /// A filled polygon (pixel centres inside it).
    func poly(_ pts: [(Double, Double)], _ c: UInt32) {
        for y in 0..<h { for x in 0..<w {
            let px = Double(x) + 0.5, py = Double(y) + 0.5
            var inside = false, j = pts.count - 1
            for i in 0..<pts.count {
                let (xi, yi) = pts[i], (xj, yj) = pts[j]
                if (yi > py) != (yj > py), px < (xj - xi) * (py - yi) / (yj - yi) + xi { inside.toggle() }
                j = i
            }
            if inside { set(x, y, c) }
        } }
    }
    func dots(_ pts: [(Int, Int)], _ c: UInt32) { for (x, y) in pts { set(x, y, c) } }
    /// Draws a part on its own, rings it with a dark tone of its own colours, and lays it over what is there.
    func layer(outline: Bool = true, _ draw: (Pix) -> Void) {
        let l = Pix(w, h)
        draw(l)
        if outline {
            let snap = l.px
            for y in 0..<h { for x in 0..<w where snap[y * w + x] == nil {
                for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                    let nx = x + dx, ny = y + dy
                    if nx >= 0, ny >= 0, nx < w, ny < h, let c = snap[ny * w + nx] { l.px[y * w + x] = tone(c, 0.42); break }
                }
            } }
        }
        for i in 0..<px.count where l.px[i] != nil { px[i] = l.px[i] }
    }
}

let INK: UInt32 = 0x2A1E2E, BLUSH: UInt32 = 0xFF8FA3, MOUTH: UInt32 = 0x5A2A3A, TONGUE: UInt32 = 0xFF7C93
let TEAR: UInt32 = 0x7FD0FF, GOLD: UInt32 = 0xFFC83D

// ---- Pets: 3 looks (a baby at level 1–4; bigger with a flower crown and scarf from 5; full size, crowned, caped, with an aura from 10) × 8 poses × 4 frames.
// The pose order is Buddy.Pose's (iOS/Shared/Standalone.swift): idle, happy, sleep, hungry, sad, worried, focus, tired.

enum Kind { case fox, frog, panda, bunny, owl }

struct PetLook {
    let kind: Kind, base: UInt32, belly: UInt32, accent: UInt32
}

func petFrame(_ k: PetLook, look: Int, pose: Int, f: Int, _ p: Pix) {
    // it grows with its level: a baby (1–4), bigger with a flower crown (5–9), full size and royal (10+).
    // Parts scale; the face (eyes, mouth) keeps its size, so the baby's eyes look bigger.
    let z = [0.74, 0.88, 1.0][look]
    func r(_ v: Double) -> Double { v * z }
    // a 40 px frame: the pet as big as in 32, with room above it (jumps, ears, crown, raised arms) and at the sides
    let C = 20.0, G = 38.0  // the pet's centre line and the ground
    // how the body moves in this pose
    let dy = [[0, 1, 1, 0], [0, -2, -3, -1], [0, 0, 1, 1], [0, 0, 0, 0], [1, 1, 2, 2], [0, 0, 0, 0], [0, 1, 0, 1], [0, 1, 1, 0]][pose][f]  // every pose moves: breathing, nodding, sobbing
    let dx = pose == 3 ? [0, 1, 0, -1][f] : pose == 5 ? [0, 1, -1, 0][f] : 0
    let bunny = k.kind == .bunny
    // how it holds itself: sitting (idle, hungry, sad, focus), standing (happy, worried), lying down (asleep, tired)
    enum Posture { case sit, stand, lie }
    let posture: Posture = [1, 5].contains(pose) ? .stand : [2, 7].contains(pose) ? .lie : .sit
    let lie = posture == .lie, stand = posture == .stand
    // the head's centre (hx, hy) and the body's (bx, by); on the ground at G
    let hx = C + Double(dx) - (lie ? r(4) : 0)
    let bx = C + Double(dx) + (lie ? r(5) : 0)
    let by: Double = lie ? G - r(4.2) : stand ? G - r(9) + Double(dy) * 0.5 : G - r(bunny ? 5.4 : 6.5) + Double(dy) * 0.5
    var hy: Double = lie ? G - r(7.6) + Double(dy) : by - r(bunny ? 10.5 : 11) + Double(dy) * 0.5 + (pose == 4 ? 1 : 0)  // sad: head hangs
    // nothing may leave the frame (a safety net; the 8 px above leave room): how far the tallest part reaches above the head's centre (ears, tufts, flowers, crown)
    var reach: Double
    switch k.kind {
    case .fox: reach = r(12)
    case .owl: reach = r(11)
    case .bunny: reach = r(16)
    case .panda: reach = r(9.7)
    case .frog: reach = r(9.6)
    }
    if look >= 1 { reach = max(reach, r(k.kind == .frog ? 9.5 : 8.5) + (look == 2 ? 4 : 1.5)) }
    hy = max(hy, reach + 2)  // +2: the outline and rounding
    let bodyRX = lie ? r(9) : r(7), bodyRY = lie ? r(4.2) : stand ? r(6) : r(bunny ? 4.8 : 5.5)
    let B = k.base
    let limb: UInt32 = k.kind == .panda ? 0x2B2B33 : tone(B, 0.92)
    let sway = [0, 1, 1, 0][f]  // the tail swings

    // the aura of a royal pet: a soft golden ring behind it, turning
    if look == 2 {
        for i in 0..<16 {
            let a = Double(i) / 16 * tau + Double(f) * 0.2
            let x = Int((C + cos(a) * 16.5).rounded()), y = Int((G - 15 + sin(a) * 15.5).rounded())
            p.set(x, y, i % 2 == 0 ? 0xFFE58A : 0xC99A2E)
        }
    }
    // behind: cape, tail, wings
    p.layer { l in
        if look == 2, !lie { l.poly([(bx - r(7), by - r(5)), (bx + r(7), by - r(5)), (bx + r(11), by + r(6)), (bx - r(11), by + r(6))], 0x7A3FD0) }
        if look == 2, lie { l.ell(bx + r(1), by - r(2.5), r(9), r(3.5), 0x7A3FD0) }
        switch k.kind {
        case .fox: l.ball(bx + bodyRX + r(2), by - r(1), r(4), r(5), B); l.ball(bx + bodyRX + r(3) + Double(sway), by - r(4.5) - Double(sway), r(2.2), r(2.2), 0xFFF4E8)
        case .bunny: l.ball(bx + bodyRX + r(0.5), by + r(1) - Double(sway), r(2.6), r(2.6), 0xFFFFFF)
        case .owl where !lie: l.ball(bx - r(7), by, r(2.6), r(4.5), tone(B, 0.8)); l.ball(bx + r(7), by, r(2.6), r(4.5), tone(B, 0.8))
        default: break
        }
    }
    // ears go behind the head
    p.layer { l in
        switch k.kind {
        case .fox:
            for s in [-1.0, 1.0] {
                l.poly([(hx + s * r(10), hy - r(2)), (hx + s * r(8), hy - r(12)), (hx + s * r(2), hy - r(7))], B)
                l.poly([(hx + s * r(8), hy - r(4)), (hx + s * r(7.5), hy - r(9)), (hx + s * r(4), hy - r(6.5))], 0xFFE9D2)
                l.set(Int(hx + s * r(8)), Int(hy - r(11)), 0x5A3A2A)
            }
        case .bunny:
            let droop = pose == 4 || pose == 7 ? 2.0 : 0
            for s in [-1.0, 1.0] {
                l.ball(hx + s * (r(4.5) + droop), hy - r(10) + droop, r(2.6), r(6), B)
                l.ell(hx + s * (r(4.5) + droop), hy - r(9.5) + droop, r(1.1), r(4.2), k.accent)
            }
        case .panda:
            l.ball(hx - r(8), hy - r(6.5), r(3.2), r(3.2), 0x2B2B33); l.ball(hx + r(8), hy - r(6.5), r(3.2), r(3.2), 0x2B2B33)
        case .owl:
            for s in [-1.0, 1.0] { l.poly([(hx + s * r(9), hy - r(3)), (hx + s * r(9), hy - r(11)), (hx + s * r(4), hy - r(7))], tone(B, 0.85)) }
        case .frog: break
        }
    }
    // body and feet
    p.layer { l in
        let foot: UInt32 = k.kind == .panda ? 0x2B2B33 : k.kind == .owl ? 0xF0A030 : tone(B, 0.9)
        if stand {  // two legs down to the ground
            for s in [-1.0, 1.0] { l.rect(Int(bx + s * r(3) - 1), Int(by + bodyRY - 1), 3, Int(G - (by + bodyRY)) + 1, limb) }
        }
        l.ball(bx, by, bodyRX, bodyRY, B)
        if !lie { l.ell(bx, by + r(1), r(4), r(bunny ? 2.6 : 3.2), k.belly) }
        if lie {    // front paws under the chin, back feet at the end
            l.ball(hx - r(2), G - 1, r(2.6), r(1.5), foot); l.ball(hx + r(3.5), G - 1, r(2.6), r(1.5), foot); l.ball(bx + bodyRX - r(1), G - 1, r(2.2), r(1.4), foot)
        } else if stand {
            l.ball(bx - r(3), G - 0.5, r(2.4), r(1.3), foot); l.ball(bx + r(3), G - 0.5, r(2.4), r(1.3), foot)
        } else {
            l.ball(bx - r(3.5), by + r(4.6), r(2.4), r(1.6), foot); l.ball(bx + r(3.5), by + r(4.6), r(2.4), r(1.6), foot)
        }
        // arms: held together when worried, at the sides otherwise (raised in joy: drawn over the head, below)
        if pose == 5 { l.ball(bx - r(1.5), by - r(2), r(2), r(1.8), limb); l.ball(bx + r(1.5), by - r(2), r(2), r(1.8), limb) }
        else if !lie, k.kind == .panda || stand { for s in [-1.0, 1.0] { l.ball(bx + s * r(6), by - r(1), r(2), r(2.8), limb) } }
        if k.kind == .owl, look > 0, !lie { for (i, y) in [by - r(1), by + r(1.5)].enumerated() { for x in stride(from: bx - 2 + Double(i), through: bx + 2, by: 2) { l.set(Int(x), Int(y), tone(k.belly, 0.75)) } } }
    }
    // scarf at the neck (5+), under the head
    if look >= 1, !lie {
        p.layer { l in
            let c: UInt32 = look == 2 ? 0xE23B5A : 0xFF7FA8
            l.rect(Int(hx - r(6)), Int(hy + r(8)), Int(r(12)), 2, c); l.rect(Int(hx + r(2)), Int(hy + r(8)) + 1, 2, 3, tone(c, 0.85))
        }
    }
    // the head
    p.layer { l in
        if k.kind == .frog { l.ball(hx - r(5), hy - r(6), r(3.8), r(3.6), B); l.ball(hx + r(5), hy - r(6), r(3.8), r(3.6), B) }
        l.ball(hx, hy, r(10.5), r(8.6), B)
        switch k.kind {
        case .fox: l.ell(hx, hy + r(4.5), r(6), r(3.6), 0xFFF4E8); l.ell(hx - r(6.5), hy + r(2.5), r(2.6), r(2.2), 0xFFF4E8); l.ell(hx + r(6.5), hy + r(2.5), r(2.6), r(2.2), 0xFFF4E8)
        case .owl: l.ell(hx, hy + r(0.5), r(8.5), r(6.5), k.belly)
        case .frog: l.ell(hx, hy + r(4.5), r(6.5), r(2.5), k.belly)
        default: break
        }
    }
    // happy: both arms thrown up beside the head, paws waving
    if pose == 1 {
        p.layer { l in
            for s in [-1.0, 1.0] {
                // short, like the body: from the shoulder up and out, the paw beside the cheek (about the body's height)
                let sx = bx + s * r(4.5), sy = by - r(3)
                let px = bx + s * r(10.5), py = by - r(7) - (f % 2 == 1 ? 1 : 0)  // just outside the head, not over the face
                let nx = sy - py, ny = px - sx, len = (nx * nx + ny * ny).squareRoot(), w = max(r(1.6), 1.3)
                let ox = nx / len * w, oy = ny / len * w
                l.poly([(sx + ox, sy + oy), (px + ox, py + oy), (px - ox, py - oy), (sx - ox, sy - oy)], limb)
                l.ball(px, py, max(r(2.1), 1.8), max(r(2.1), 1.8), limb)
            }
        }
    }
    // the face: eyes and mouth keep their pixel size at every stage
    let eyeY = k.kind == .frog ? hy - r(6) : hy + r(1)
    let gap = max(r(4.5), 3.5)
    let eyes = [(hx - gap, eyeY), (hx + gap, eyeY)].map { (Int($0.0.rounded(.down)), Int($0.1.rounded(.down))) }
    if k.kind == .panda { for (ex, ey) in eyes { p.ell(Double(ex) + 0.5, Double(ey) + 0.5, 2.6, 3.3, 0x2B2B33) } }
    if k.kind == .owl { for (ex, ey) in eyes { p.ell(Double(ex) + 0.5, Double(ey) + 0.5, 3.4, 3.4, 0xFFFFFF); p.ell(Double(ex) + 0.5, Double(ey) + 0.5, 2.5, 2.5, 0xFFC83D) } }
    let blink = (pose == 0 || pose == 3 || pose == 5) && f == 3
    let eyeInk: UInt32 = k.kind == .panda ? 0x0E0E12 : INK
    for (i, (ex, ey)) in eyes.enumerated() {
        let inner = i == 0 ? 1 : -1  // toward the middle
        switch pose {
        case 1: p.dots([(ex - 1, ey), (ex, ey - 1), (ex + 1, ey)], eyeInk)                 // happy: ^ ^
        case 2: p.dots([(ex - 1, ey), (ex, ey + 1), (ex + 1, ey)], eyeInk)                 // asleep
        case 7: p.rect(ex - 1, ey - 1, 3, 1, tone(B, 0.55)); p.rect(ex - 1, ey, 3, 2, eyeInk); p.set(ex - 1, ey, 0xFFFFFF)  // heavy lids
        default:
            if blink { p.rect(ex - 1, ey, 3, 1, eyeInk); continue }
            let big = pose == 3
            p.rect(ex - 1, ey - 2, big ? 4 : 3, big ? 5 : 4, eyeInk)
            p.set(ex - 1, ey - 2, 0xFFFFFF); p.set(ex, ey - 2, 0xFFFFFF)
            p.set(ex + (big ? 2 : 1), ey + 1, 0xCFC6FF)
            if pose == 4 || pose == 5 { p.dots([(ex - 1 + (inner > 0 ? 0 : 2), ey - 4), (ex, ey - 4), (ex - 1 + (inner > 0 ? 2 : 0), ey - 5)], eyeInk) }  // sad brows: inner ends up
            if pose == 6 { p.dots([(ex - 1 + (inner > 0 ? 0 : 2), ey - 5), (ex, ey - 4), (ex - 1 + (inner > 0 ? 2 : 0), ey - 3)], eyeInk); p.rect(ex - 1, ey - 2, 3, 1, tone(B, 0.55)) }  // determined
        }
    }
    // cheeks
    let blushY = Int(eyeY + (k.kind == .frog ? r(9) : 3))
    if pose != 7 {
        let blush = pose == 1 ? BLUSH : tone(BLUSH, 0.92)
        p.rect(Int(hx - gap) - 4, blushY, 2 + look / 2, 1, blush); p.rect(Int(hx + gap) + 3 - look / 2, blushY, 2 + look / 2, 1, blush)
    }
    // the mouth (owls have a beak)
    let mx = Int(hx), my = Int(hy + (k.kind == .frog ? r(4) : max(r(5), 4)))
    if k.kind == .owl { p.poly([(hx - 1.5, Double(my) - 3), (hx + 1.5, Double(my) - 3), (hx, Double(my) - 0.5)], 0xF0A030) }
    switch pose {
    case 1: p.rect(mx - 2, my, 4, 2, MOUTH); p.rect(mx - 1, my + 1, 2, 1, TONGUE)
    case 2: p.set(mx, my, MOUTH)
    case 3: p.rect(mx - 1, my, 2, 2, MOUTH); p.rect(mx + 1, my + 2, 1, 1 + f % 3, TEAR)       // hungry: drooling
    case 4: p.dots([(mx - 2, my + 1), (mx - 1, my), (mx, my), (mx + 1, my + 1)], MOUTH)
    case 5: p.dots([(mx - 2, my), (mx - 1, my + 1), (mx, my), (mx + 1, my + 1), (mx + 2, my)], MOUTH)
    case 6, 7: p.rect(mx - 1, my, 3, 1, MOUTH)
    default: if k.kind != .owl { p.dots([(mx - 2, my), (mx - 1, my + 1), (mx, my), (mx + 1, my + 1), (mx + 2, my)], MOUTH) }
    }
    // what it has grown into
    let crownY = Int(hy - r(k.kind == .frog ? 9.5 : 8.5))
    if look == 1 {
        for (i, fx) in [-5, 0, 5].enumerated() {
            let c: UInt32 = [0xFF8FB8, 0xFFD166, 0xB59CFF][i]
            let x = mx + fx, y = crownY + (fx == 0 ? 0 : 1)
            p.dots([(x, y - 1), (x - 1, y), (x + 1, y), (x, y + 1)], c); p.set(x, y, 0xFFF4C2)
        }
        p.dots([(mx - 3, crownY + 1), (mx + 3, crownY + 1)], 0x7BC86C)
    }
    if look == 2 {
        p.layer { l in
            let top = crownY - 4
            l.rect(mx - 4, top + 3, 9, 3, GOLD)
            l.dots([(mx - 4, top + 1), (mx - 4, top + 2), (mx, top), (mx, top + 1), (mx, top + 2), (mx + 4, top + 1), (mx + 4, top + 2)], GOLD)
            l.set(mx, top + 4, 0xFF4F7A); l.set(mx - 3, top + 4, 0x6FD3FF); l.set(mx + 3, top + 4, 0x6FD3FF)
        }
        for (i, (sx, sy)) in [(4, 11), (35, 10), (36, 28), (3, 27)].enumerated() where (f + i) % 2 == 0 {
            p.dots([(sx, sy), (sx - 1, sy), (sx + 1, sy), (sx, sy - 1), (sx, sy + 1)], 0xFFF3A8)
        }
    }
    // props: an empty bowl when hungry, a laptop to work on when focused
    if pose == 3 {
        p.layer { l in l.ell(bx + r(1), G - 0.5, r(6), r(2), 0x5FA8E8); l.rect(Int(bx + r(1) - r(5)), Int(G) - 2, Int(r(10)), 1, 0x8CC8FF) }
    }
    if pose == 6 {
        p.layer { l in
            l.rect(Int(bx - r(6)), Int(G - r(7)), Int(r(12)), Int(r(6)), 0x9AA3B2)   // the lid, its back to us
            l.rect(Int(bx - r(7)), Int(G) - 2, Int(r(14)), 2, 0x6E7686)                       // the base
        }
        p.set(Int(bx), Int(G - r(4.5)), 0xDDE3EE)                                     // a glowing dot on the lid
    }
    // what the pose adds
    let top = Int(hy - r(8))
    switch pose {
    case 1:                                        // hearts, rising
        let y = max(top - 4 - f, 1), x = mx + 6
        p.dots([(x, y), (x + 2, y), (x - 1, y + 1), (x, y + 1), (x + 1, y + 1), (x + 2, y + 1), (x + 3, y + 1), (x, y + 2), (x + 1, y + 2), (x + 2, y + 2), (x + 1, y + 3)], 0xFF5C8A)
    case 2:                                        // Zzz
        for i in 0..<2 {
            let x = Int(C) + 8 + i * 3, y = max(top - i * 4 + (f % 2), 1)
            p.rect(x, y, 3, 1, W); p.set(x + 1, y + 1, W); p.rect(x, y + 2, 3, 1, W)
        }
    case 4: for (ex, ey) in eyes { p.rect(ex, ey + 3 + f, 1, 2, TEAR) }          // tears
    case 5:                                        // a sweat drop
        let y = top + f, x = mx + Int(r(9))
        p.dots([(x, y), (x - 1, y + 1), (x, y + 1), (x + 1, y + 1), (x, y + 2)], TEAR)
    case 6:                                        // a headband, its ends in the wind
        let y = Int(hy - r(5)), half = Int(r(9))
        p.rect(mx - half, y, half * 2, 2, 0xE23B3B); p.rect(mx - half, y, half * 2, 1, 0xFF6B6B)
        let wave = f % 2
        p.dots([(mx + half + 1, y - 1 + wave), (mx + half + 2, y - 2 + wave), (mx + half + 3, y - 2), (mx + half + 1, y + 2), (mx + half + 2, y + 2 - wave)], 0xE23B3B)
    case 7: for i in 0..<min(f + 1, 3) { p.set(mx + 7 + i * 2, max(top - 2, 1), W) }       // "..."
    default: break
    }
}

func pet(_ id: String, _ title: String, _ look: PetLook) {
    phone(id, title, "pets", fps: 4, frames: 96, side: 40) { n, p in petFrame(look, look: n / 32, pose: n / 4 % 8, f: n % 4, p) }
}
pet("ip-fox", "Cáo", PetLook(kind: .fox, base: 0xF28C38, belly: 0xFFF4E8, accent: 0xFFE9D2))
pet("ip-frog", "Ếch", PetLook(kind: .frog, base: 0x6CC24A, belly: 0xE4F7B8, accent: 0x6CC24A))
pet("ip-panda", "Gấu trúc", PetLook(kind: .panda, base: 0xF4F4F6, belly: 0xFFFFFF, accent: 0x2B2B33))
pet("ip-bunny", "Thỏ", PetLook(kind: .bunny, base: 0xFFF2F6, belly: 0xFFFFFF, accent: 0xFFB0C6))
pet("ip-owl", "Cú", PetLook(kind: .owl, base: 0xA8744A, belly: 0xF1D9B5, accent: 0xF0A030))

let phoneSprites = Array(sprites[macCount...])
sprites.removeLast(sprites.count - macCount)
// the phone's pets go on the Mac's notch too (level 5): one strip per look, "ip-fox", "ip-fox-l1", "ip-fox-l2"
for s in phoneSprites where s.category == "pets" {
    for look in 0..<3 {
        sprites.append(Sprite(id: look == 0 ? s.id : "\(s.id)-l\(look)", title: s.title, category: "pets", fps: s.fps, pixel: true,
                              frames: Array(s.frames[look * 32..<look * 32 + 32])))
    }
}

// MARK: - Export

let fm = FileManager.default
let root = URL(fileURLWithPath: fm.currentDirectoryPath)
let out = root.appendingPathComponent("Resources/Sprites")
try? fm.removeItem(at: out)
try fm.createDirectory(at: out, withIntermediateDirectories: true)
var catalog: [[String: Any]] = []
for s in sprites {
    let side = s.frames[0].width
    let ctx = CGContext(data: nil, width: side * s.frames.count, height: side, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .none
    for (i, img) in s.frames.enumerated() { ctx.draw(img, in: CGRect(x: i * side, y: 0, width: side, height: side)) }
    let dest = CGImageDestinationCreateWithURL(out.appendingPathComponent(s.id + ".png") as CFURL, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
    CGImageDestinationFinalize(dest)
    catalog.append(["id": s.id, "title": s.title, "category": s.category, "frames": s.frames.count, "fps": s.fps, "pixel": s.pixel])
}
try JSONSerialization.data(withJSONObject: catalog, options: [.prettyPrinted, .sortedKeys]).write(to: out.appendingPathComponent("catalog.json"))
print("\(sprites.count) sprites → \(out.path)")

let phoneOut = root.appendingPathComponent("iOS/Live/PhoneSprites")
try? fm.removeItem(at: phoneOut)
try fm.createDirectory(at: phoneOut, withIntermediateDirectories: true)
var phoneCatalog: [[String: Any]] = []
for s in phoneSprites {
    let side = s.frames[0].width
    let ctx = CGContext(data: nil, width: side * s.frames.count, height: side, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    for (i, img) in s.frames.enumerated() { ctx.draw(img, in: CGRect(x: i * side, y: 0, width: side, height: side)) }
    let dest = CGImageDestinationCreateWithURL(phoneOut.appendingPathComponent(s.id + ".png") as CFURL, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, ctx.makeImage()!, nil)
    CGImageDestinationFinalize(dest)
    phoneCatalog.append(["id": s.id, "title": s.title, "category": s.category, "frames": s.frames.count, "fps": s.fps, "pixel": s.pixel])
}
try JSONSerialization.data(withJSONObject: phoneCatalog, options: [.prettyPrinted, .sortedKeys]).write(to: phoneOut.appendingPathComponent("phone-sprites.json"))
print("\(phoneSprites.count) phone sprites → \(phoneOut.path)")

// contact sheet of every frame, upscaled, for checking the set by eye (not shipped)
sprites += phoneSprites  // the contact sheet shows both sets
let cell = 48, cols = sprites.map(\.frames.count).max()!
let sheet = CGContext(data: nil, width: cols * cell, height: sprites.count * cell, bitsPerComponent: 8, bytesPerRow: 0,
                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
sheet.setFillColor(rgb(0x000000)); sheet.fill(CGRect(x: 0, y: 0, width: cols * cell, height: sprites.count * cell))
sheet.interpolationQuality = .none
for (r, s) in sprites.enumerated() { for (i, img) in s.frames.enumerated() {
    sheet.draw(img, in: CGRect(x: i * cell + 4, y: (sprites.count - 1 - r) * cell + 4, width: cell - 8, height: cell - 8))
} }
try? fm.createDirectory(at: root.appendingPathComponent("build"), withIntermediateDirectories: true)
let pd = CGImageDestinationCreateWithURL(root.appendingPathComponent("build/sprites-preview.png") as CFURL, "public.png" as CFString, 1, nil)!
CGImageDestinationAddImage(pd, sheet.makeImage()!, nil); CGImageDestinationFinalize(pd)
