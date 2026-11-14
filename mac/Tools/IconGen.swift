// App icon on a graphite squircle. Styles: the MacBook notch, or a floating island (Dynamic Island, Macs without a
// notch, a future iPhone app).
// Usage: swift Tools/IconGen.swift <out-dir>   → <name>.png (1024 px) for each design, plus preview.png
import SwiftUI
import AppKit

enum Style { case notch, island, islandDrop, eye, islandPulse }

struct Design {
    let name: String
    let style: Style
    var top = Color(red: 0.20, green: 0.20, blue: 0.22)
    var bottom = Color(red: 0.09, green: 0.09, blue: 0.10)
    var dot = Color(red: 1.0, green: 0.58, blue: 0.20)
}

let designs = [
    Design(name: "A-notch", style: .notch),             // current
    Design(name: "B-island", style: .island),
    Design(name: "C-island-drop", style: .islandDrop),
    Design(name: "D-eye", style: .eye),
    Design(name: "E-island-pulse", style: .islandPulse),
]

struct Mark: View {
    let d: Design
    var glow: some View { Circle().fill(d.dot).shadow(color: d.dot.opacity(0.55), radius: 14) }

    var body: some View {
        switch d.style {
        case .notch:
            // black, joined to the top edge, rounded only at the bottom
            UnevenRoundedRectangle(bottomLeadingRadius: 78, bottomTrailingRadius: 78, style: .continuous).fill(.black)
                .frame(width: 480, height: 190)
                .overlay(alignment: .trailing) { glow.frame(width: 66, height: 66).padding(.trailing, 66) }
                .frame(maxHeight: .infinity, alignment: .top)
        case .island:
            // the island floats under the top edge, the way it sits on an iPhone
            Capsule().fill(.black).frame(width: 470, height: 150)
                .overlay(alignment: .trailing) { glow.frame(width: 70, height: 70).padding(.trailing, 42) }
                .frame(maxHeight: .infinity, alignment: .top).padding(.top, 118)
        case .islandDrop:
            // the island and the card it drops: what the app does, in two shapes
            VStack(spacing: 34) {
                Capsule().fill(.black).frame(width: 380, height: 118)
                    .overlay(alignment: .trailing) { glow.frame(width: 56, height: 56).padding(.trailing, 32) }
                RoundedRectangle(cornerRadius: 64, style: .continuous).fill(.white.opacity(0.10))
                    .frame(width: 560, height: 230)
                    .overlay(alignment: .leading) {
                        VStack(alignment: .leading, spacing: 26) {
                            Capsule().fill(.white.opacity(0.55)).frame(width: 260, height: 34)
                            Capsule().fill(.white.opacity(0.22)).frame(width: 380, height: 28)
                        }
                        .padding(.leading, 58)
                    }
            }
            .frame(maxHeight: .infinity, alignment: .top).padding(.top, 118)
        case .eye:
            // one big island in the middle, its dot the pupil: something keeping an eye on your agents
            Capsule().fill(.black).frame(width: 600, height: 236)
                .overlay { glow.frame(width: 112, height: 112).offset(x: 120) }
        case .islandPulse:
            // the island with a heartbeat of work inside
            Capsule().fill(.black).frame(width: 520, height: 170)
                .overlay {
                    HStack(spacing: 22) {
                        ForEach([0.45, 0.85, 0.6, 1.0, 0.5], id: \.self) { h in
                            Capsule().fill(d.dot).frame(width: 26, height: 96 * h)
                        }
                    }
                    .shadow(color: d.dot.opacity(0.5), radius: 10)
                }
                .frame(maxHeight: .infinity, alignment: .top).padding(.top, 118)
        }
    }
}

/// macOS: an 824 pt squircle centred in 1024 with the system's soft shadow. iOS: full bleed, the system rounds it.
struct Icon: View {
    let d: Design
    var iOS = false
    var body: some View {
        let shape = RoundedRectangle(cornerRadius: iOS ? 0 : 186, style: .continuous)
        ZStack {
            shape.fill(LinearGradient(colors: [d.top, d.bottom], startPoint: .top, endPoint: .bottom))
            Mark(d: d)
            if !iOS {
                // a hairline of light along the top, the only "material" detail
                shape.strokeBorder(LinearGradient(colors: [.white.opacity(0.22), .clear], startPoint: .top, endPoint: .center), lineWidth: 2)
            }
        }
        .frame(width: iOS ? 1024 : 824, height: iOS ? 1024 : 824)
        .clipShape(shape)
        .shadow(color: .black.opacity(iOS ? 0 : 0.28), radius: 18, y: 10)
        .frame(width: 1024, height: 1024)
    }
}

@MainActor func png(_ view: some View, _ url: URL) {
    let r = ImageRenderer(content: view)
    r.scale = 1
    guard let cg = r.cgImage, let data = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:]) else {
        fatalError("render failed: \(url.lastPathComponent)")
    }
    try! data.write(to: url)
}

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".")
MainActor.assumeIsolated {
    for d in designs {
        png(Icon(d: d), out.appendingPathComponent("\(d.name).png"))
        png(Icon(d: d, iOS: true), out.appendingPathComponent("\(d.name)-ios.png"))  // full bleed: iOS rounds it
    }
    // each design large and at Dock size on light and dark, then as an iPhone icon (rounded by the system)
    let sheet = VStack(spacing: 0) {
        ForEach([Color(white: 0.96), Color(white: 0.12)], id: \.self) { bg in
            HStack(spacing: 32) {
                ForEach(designs, id: \.name) { d in
                    VStack(spacing: 16) {
                        Icon(d: d).scaleEffect(0.22).frame(width: 225, height: 225)
                        Icon(d: d).scaleEffect(0.0625).frame(width: 64, height: 64)
                    }
                }
            }
            .padding(32).background(bg)
        }
        HStack(spacing: 32) {
            ForEach(designs, id: \.name) { d in
                VStack(spacing: 10) {
                    Icon(d: d, iOS: true).scaleEffect(0.17).frame(width: 174, height: 174)
                        .clipShape(RoundedRectangle(cornerRadius: 39, style: .continuous))
                    Text(d.name).font(.system(size: 16, weight: .semibold)).foregroundStyle(.white)
                }
                .frame(width: 225)
            }
        }
        .padding(32).frame(maxWidth: .infinity).background(Color(red: 0.16, green: 0.20, blue: 0.30))
    }
    png(sheet, out.appendingPathComponent("preview.png"))
}
print("icons written to \(out.path)")
