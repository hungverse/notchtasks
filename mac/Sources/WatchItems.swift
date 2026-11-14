import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - Watch list (edited in the control panel, saved in UserDefaults)

struct WatchItem: Codable, Identifiable, Equatable {
    enum Kind: String, Codable { case process, app, codex, claude, dev }  // dev = the built-in Build & CI row
    var id = UUID()
    var name: String
    var kind: Kind
    var process = ""          // .process: executable name (as in Activity Monitor); "" = any name, matched by `args`
    var bundleID: String?     // .app; also the icon source
    var childTasks = false    // .process: each command it starts is its own row (runners, workers)
    var enabled = true
    // optional so lists saved by older versions still decode
    var args: String?         // .process: command line must contain this (tools run as node/python)
    var threshold: Double?    // CPU % above which it counts as working
    var symbolName: String?   // icon when there is no app to take it from
    var cli: String?          // command that shows the tool is installed (presets)
    var build: Bool?          // shown under Build & CI instead of AI agent
    var icon: Badge?          // the user's own icon, replacing the app's

    var inBuild: Bool { kind == .dev || build == true }

    static func defaultThreshold(_ kind: Kind, childTasks: Bool) -> Double { kind == .app ? 5 : childTasks ? 0.5 : 1.5 }
    var cpuThreshold: Double { threshold ?? Self.defaultThreshold(kind, childTasks: childTasks) }

    var summary: String {
        let cpu = "CPU > \(cpuThreshold.formatted(.number.precision(.fractionLength(0...1))))%"
        switch kind {
        case .process:
            let what = process.isEmpty ? L("Command contains “{0}”", args ?? "") : L("Process “{0}”", process) + (args.map { L(" · command contains “{0}”", $0) } ?? "")
            return [what, childTasks ? L("one line per subprocess") : nil, cpu].compactMap { $0 }.joined(separator: " · ")
        case .app: return L("App · {0}", cpu)
        case .codex, .claude: return L("Tracks each session")
        case .dev: return L("Shows while running")
        }
    }

    /// Icon when there is no app bundle to take it from.
    var symbol: String {
        if let symbolName { return symbolName }
        switch kind {
        case .dev: return "hammer.fill"
        default: return "terminal.fill"
        }
    }

    /// Found on this Mac (app bundle or command on the usual PATH dirs).
    var isInstalled: Bool {
        if let cli {
            let home = FileManager.default.homeDirectoryForCurrentUser.path
            var dirs = ["/opt/homebrew/bin", "/usr/local/bin", "\(home)/.local/bin", "\(home)/.bun/bin", "\(home)/.cargo/bin", "\(home)/.npm-global/bin"]
            dirs += ((try? FileManager.default.contentsOfDirectory(atPath: "\(home)/.nvm/versions/node")) ?? []).map { "\(home)/.nvm/versions/node/\($0)/bin" }
            return dirs.contains { FileManager.default.isExecutableFile(atPath: "\($0)/\(cli)") }
        }
        if kind == .app, let id = bundleID { return NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) != nil }
        return true
    }
}

extension WatchItem {
    func matches(_ p: PInfo, argv: (pid_t) -> [String]) -> Bool {
        let needle = args ?? ""
        guard kind == .process, !(process.isEmpty && needle.isEmpty), p.pid != getpid() else { return false }
        if !process.isEmpty && p.name != process { return false }
        // a shell or grep whose command text merely mentions the tool isn't the tool
        if process.isEmpty && ["zsh", "bash", "sh", "fish", "dash", "grep", "rg", "login", "sudo", "tail", "less"].contains(p.name) { return false }
        return needle.isEmpty || argv(p.pid).joined(separator: " ").contains(needle)
    }

    /// Outermost matching processes: a matching child belongs to its parent's session.
    func roots(_ procs: [PInfo], argv: (pid_t) -> [String]) -> [PInfo] {
        let found = procs.filter { matches($0, argv: argv) }
        let pids = Set(found.map(\.pid))
        return found.filter { !pids.contains($0.ppid) }
    }
}

/// CLI agent: often a node/python script, so matched by its command line; working = its process tree uses CPU.
private func agent(_ name: String, cli: String, process: String = "", args: String? = nil, icon: String? = nil, threshold: Double = 3) -> WatchItem {
    WatchItem(name: name, kind: .process, process: process, bundleID: icon, args: args ?? (process.isEmpty ? cli : nil),
              threshold: threshold, symbolName: "sparkles", cli: cli)
}

/// GUI apps also use CPU for plain editing, so their bar is higher.
private func aiApp(_ name: String, _ bundleID: String, threshold: Double) -> WatchItem {
    WatchItem(name: name, kind: .app, bundleID: bundleID, threshold: threshold)
}

let presetGroups: [(title: String, items: [WatchItem])] = [
    ("Agent trong terminal", [
        WatchItem(name: "Claude", kind: .claude, process: "claude", bundleID: "com.anthropic.claudefordesktop"),
        WatchItem(name: "Codex", kind: .codex, bundleID: "com.openai.codex"),
        agent("Gemini CLI", cli: "gemini", args: "gemini-cli"),
        agent("Antigravity agent", cli: "agy", process: "agy", icon: "com.google.antigravity"),
        agent("Cursor CLI", cli: "cursor-agent", args: "cursor-agent", icon: "com.todesktop.230313mzl4w4u92"),
        agent("GitHub Copilot CLI", cli: "copilot", args: "@github/copilot"),
        agent("Qwen Code", cli: "qwen", args: "qwen-code"),
        agent("OpenCode", cli: "opencode", process: "opencode"),
        agent("Amp", cli: "amp", args: "@sourcegraph/amp"),
        agent("Aider", cli: "aider", args: "bin/aider"),
        agent("Goose", cli: "goose", process: "goose"),
        agent("Crush", cli: "crush", process: "crush"),
        agent("Muse Code", cli: "muse", process: "muse"),
        agent("Hermes Agent", cli: "hermes", args: "bin/hermes"),
        agent("Augment (auggie)", cli: "auggie", args: "@augmentcode/auggie"),
        agent("Kimi CLI", cli: "kimi", args: "bin/kimi"),
        agent("Kiro CLI", cli: "kiro-cli", process: "kiro-cli"),
        agent("OpenClaw", cli: "openclaw", args: "openclaw/dist", threshold: 15),  // its gateway idles at 1–4% CPU
    ]),
    (L("AI apps"), [
        aiApp("ChatGPT", "com.openai.chat", threshold: 15),
        aiApp("Cursor", "com.todesktop.230313mzl4w4u92", threshold: 30),
        aiApp("Windsurf", "com.exafunction.windsurf", threshold: 30),
        aiApp("Antigravity", "com.google.antigravity", threshold: 25),
        aiApp("Kiro", "dev.kiro.desktop", threshold: 30),
        aiApp("Zed", "dev.zed.Zed", threshold: 30),
        aiApp("Perplexity", "ai.perplexity.mac", threshold: 15),
    ]),
    // local model runners: on the notch while they generate, with the model they have loaded
    (L("Model running locally"), [
        aiApp("LM Studio", "ai.elementlabs.lmstudio", threshold: 8),
        WatchItem(name: "Ollama", kind: .process, process: "ollama", bundleID: "com.electron.ollama", threshold: 8, cli: "ollama"),
    ]),
]

let presets: [WatchItem] = presetGroups.flatMap(\.items)

/// Build tools and apps to watch on top of what Build & CI detects by itself.
private func buildApp(_ name: String, _ bundleID: String, threshold: Double) -> WatchItem {
    WatchItem(name: name, kind: .app, bundleID: bundleID, threshold: threshold, build: true)
}

let buildPresets: [WatchItem] = [
    buildApp("Xcode", "com.apple.dt.Xcode", threshold: 30),
    buildApp("Android Studio", "com.google.android.studio", threshold: 40),
    buildApp("Docker", "com.docker.docker", threshold: 20),
    buildApp("Unity Hub", "com.unity3d.unityhub", threshold: 30),
    buildApp("Blender", "org.blenderfoundation.blender", threshold: 60),
    buildApp("DaVinci Resolve", "com.blackmagic-design.DaVinciResolveLite", threshold: 60),
    WatchItem(name: "Gradle daemon", kind: .process, process: "java", args: "GradleDaemon", threshold: 20, symbolName: "hammer.fill", build: true),
]

/// Covered by Build & CI without adding anything: listed so nobody looks for them.

/// The Build & CI module's row: not in the watch list, everything in it is detected.
let buildItem = WatchItem(id: UUID(uuidString: "B0B0B0B0-0000-4000-8000-000000000001")!, name: "Build & CI", kind: .dev)

struct Job: Identifiable, Equatable {
    let id: String
    var title: String
    var detail: String
    var working: Bool
    var url: URL? = nil  // click opens it (e.g. a CI job's live log)
    var progress: Double? = nil  // 0…1
    var estimated = false        // progress guessed from past runs, not reported by the tool
    var icon: String? = nil      // bundle ID for the row's app icon
    var symbol: String? = nil
    var tool: String? = nil      // detected tool (Xcode, npm, GitLab Runner…): the user may give each one its own icon
}

struct ItemState: Identifiable, Equatable {
    let item: WatchItem
    var jobs: [Job]
    var id: UUID { item.id }
    var working: Int { jobs.filter(\.working).count }
}
