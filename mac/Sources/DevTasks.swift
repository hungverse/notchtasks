import AppKit
import Foundation
import Darwin

// MARK: - Long dev tasks: builds, tests, installs, containers, renders, transfers

/// What a process is doing, read from its name + argv.
struct DevMatch: Equatable {
    var tool: String
    var action: String
    var symbol: String
    var icon: String? = nil      // bundle ID whose app icon represents the tool
    var subject: String? = nil   // shown instead of the project folder (e.g. the file being rendered)
    var busyOnly = false         // long-lived daemons/apps: a task only while they burn CPU
}

enum DevRules {
    /// Every tool name detection produces, for setting icons before a tool has been seen.
    static let knownTools = [".NET", "App Store", "Bazel", "Blender", "Bundler", "CMake", "Cargo", "Carthage", "CocoaPods", "Conda", "Dart", "Deno",
                             "Django", "Docker", "EAS", "Electron Forge", "Flutter", "Git", "GitHub Actions", "GitLab Runner", "Go", "Gradle",
                             "HandBrake", "Helm", "Homebrew", "Hugging Face", "ImageMagick", "Maven", "Notarize", "Ollama", "Playwright", "Podman",
                             "Poetry", "Prisma", "Pulumi", "RSpec", "SwiftPM", "Terraform", "Tải file", "Unity", "Whisper", "Xcode", "build_runner",
                             "bun", "curl", "esbuild", "fastlane", "ffmpeg", "hdiutil", "jest", "make", "next", "ninja", "npm", "pip", "pnpm",
                             "pytest", "python", "rsync", "rustup", "tsc", "uv", "vite", "vitest", "webpack", "wget", "xcodes", "yarn", "yt-dlp", "zip"]

    /// App whose icon stands for the tool, as detection picks it.
    static func appIcon(_ tool: String) -> String? {
        switch tool {
        case "Xcode", "SwiftPM", "Notarize", "App Store", "xcodes": return "com.apple.dt.Xcode"
        case "Gradle": return "com.google.android.studio"
        case "Docker", "Podman": return "com.docker.docker"
        case "Unity": return "com.unity3d.unityhub"
        case "Blender": return "org.blenderfoundation.blender"
        case "HandBrake": return "fr.handbrake.HandBrake"
        case "Ollama": return "com.electron.ollama"
        default: return nil
        }
    }

    static func defaultSymbol(_ tool: String) -> String {
        switch tool {
        case "GitLab Runner", "GitHub Actions": return "server.rack"
        case "Tải file", "curl", "wget", "yt-dlp": return "arrow.down.circle.fill"
        case "ffmpeg", "HandBrake": return "film.stack"
        case "Git": return "arrow.triangle.branch"
        default: return "hammer.fill"
        }
    }

    /// Process names worth reading argv for (argv costs a sysctl).
    static let names: Set<String> = [
        "xcodebuild", "swift", "swift-build", "swift-test", "swift-package", "java", "dart", "dartvm", "node", "bun", "deno",
        "cargo", "go", "make", "gmake", "ninja", "cmake", "bazel", "bazelisk", "ruby", "pod", "fastlane", "brew", "carthage",
        "uv", "docker", "com.docker.cli", "podman", "git", "rsync", "scp", "ditto", "rclone", "aria2c", "yt-dlp", "wget", "curl",
        "zip", "unzip", "tar", "bsdtar", "7z", "7zz", "xz", "zstd", "hdiutil", "ffmpeg", "HandBrakeCLI", "blender", "Blender",
        "magick", "convert", "mogrify", "whisper", "whisper-cli", "ollama", "terraform", "tofu", "pulumi", "helm", "notarytool",
        "altool", "Unity", "dotnet", "rustup", "xcodes", "flutter", "gradle", "mvn", "eas", "expo",
    ]

    static func isPython(_ name: String) -> Bool { name == "Python" || name.hasPrefix("python") }

    static func base(_ s: String) -> String {
        let b = (s as NSString).lastPathComponent
        for ext in [".js", ".cjs", ".mjs", ".rb", ".py", ".blend"] where b.hasSuffix(ext) { return String(b.dropLast(ext.count)) }
        return b
    }

    /// Dev servers and watchers never "finish": they are apps running, not tasks.
    static let endless: Set<String> = ["dev", "start", "serve", "watch", "preview", "--watch", "-w", "storybook", "daemon", "attach", "run-server"]

    static func symbol(_ action: String) -> String {
        let s = action.lowercased()
        func any(_ words: [String]) -> Bool { words.contains { s.contains($0) } }
        if any(["test", "e2e", "check", "lint", "analyze", "clippy", "vet", "typecheck"]) { return "checkmark.diamond.fill" }
        if any(["deploy", "push", "publish", "upload", "release", "submit", "apply", "export"]) { return "icloud.and.arrow.up.fill" }
        if any(["install", "ci", "add", "update", "upgrade", "sync", "get", "fetch", "pull", "resolve", "bootstrap", "download", "restore", "clone"]) {
            return "shippingbox.fill"
        }
        return "hammer.fill"
    }

    static func classify(_ name: String, _ argv: [String]) -> DevMatch? {
        let a = Array(argv.dropFirst())
        let words = a.filter { !$0.hasPrefix("-") }
        func opt(_ flags: String...) -> String? {
            for f in flags { if let i = a.firstIndex(of: f), i + 1 < a.count { return a[i + 1] } }
            return nil
        }
        func verb(_ allowed: Set<String>, tool: String, icon: String? = nil, words w: [String]? = nil) -> DevMatch? {
            let w = w ?? words
            guard let v = w.first, allowed.contains(v) else { return nil }
            let action = w.prefix(2).joined(separator: " ")
            return DevMatch(tool: tool, action: action, symbol: symbol(action), icon: icon)
        }
        if isPython(name) { return python(a) }
        switch name {
        case "xcodebuild":
            if a.contains(where: { ["-list", "-version", "-showsdks", "-showBuildSettings", "-showdestinations", "-help"].contains($0) }) { return nil }
            let acts = ["archive", "test", "build", "build-for-testing", "test-without-building", "analyze", "docbuild", "clean"]
            let act = a.first { acts.contains($0) } ?? (a.contains("-exportArchive") ? "export" : a.contains("-resolvePackageDependencies") ? "resolve packages" : "build")
            let target = opt("-workspace", "-project").map { project(($0 as NSString).deletingLastPathComponent) }
            return DevMatch(tool: "Xcode", action: [act, opt("-scheme")].compactMap { $0 }.joined(separator: " · "), symbol: symbol(act),
                            icon: "com.apple.dt.Xcode", subject: target)
        case "swift-build", "swift-test", "swift-package":
            let act = name == "swift-test" ? "test" : name == "swift-package" ? (words.first ?? "package") : "build"
            if name == "swift-package" && !["resolve", "update", "archive-source"].contains(act) { return nil }
            return DevMatch(tool: "SwiftPM", action: act, symbol: symbol(act), icon: "com.apple.dt.Xcode")
        case "swift": return verb(["build", "test"], tool: "SwiftPM", icon: "com.apple.dt.Xcode")
        case "java":
            if let i = a.firstIndex(where: { $0.hasSuffix("GradleWrapperMain") || $0 == "org.gradle.launcher.GradleMain" }) {
                let tasks = a[(i + 1)...].filter { !$0.hasPrefix("-") }
                guard tasks.allSatisfy({ !endless.contains($0) && $0 != "--continuous" }) else { return nil }
                let act = tasks.prefix(2).joined(separator: " ")
                return DevMatch(tool: "Gradle", action: act.isEmpty ? "build" : act, symbol: symbol(act), icon: "com.google.android.studio")
            }
            // Android Studio builds through the daemon (no client process): busy daemon = build
            if a.contains("org.gradle.launcher.daemon.bootstrap.GradleDaemon") {
                return DevMatch(tool: "Gradle", action: "build (Android Studio)", symbol: "hammer.fill", icon: "com.google.android.studio", busyOnly: true)
            }
            if let i = a.firstIndex(of: "org.codehaus.plexus.classworlds.launcher.Launcher") {
                let goals = a[(i + 1)...].filter { !$0.hasPrefix("-") }.prefix(2).joined(separator: " ")
                return DevMatch(tool: "Maven", action: goals.isEmpty ? "build" : goals, symbol: symbol(goals))
            }
            return nil
        case "dart", "dartvm":
            if let i = a.firstIndex(where: { $0.hasSuffix("flutter_tools.snapshot") }) {
                let rest = a[(i + 1)...].filter { !$0.hasPrefix("-") }
                return verb(["build", "test", "pub", "precache", "analyze", "drive", "install", "clean", "gen-l10n", "packages", "create", "upgrade"],
                            tool: "Flutter", words: Array(rest))
            }
            if a.contains(where: { $0.contains("build_runner") }), a.contains("build") {
                return DevMatch(tool: "build_runner", action: "build", symbol: "hammer.fill")
            }
            return verb(["compile", "test", "pub", "analyze"], tool: "Dart")
        case "node": return node(a)
        case "bun":
            guard let v = words.first else { return nil }
            if ["install", "i", "add", "update", "build", "test"].contains(v) { return DevMatch(tool: "bun", action: v, symbol: symbol(v)) }
            return v == "run" ? script(tool: "bun", words.dropFirst().first) : nil
        case "deno": return verb(["compile", "test", "install", "bundle"], tool: "Deno")
        case "cargo": return verb(["build", "b", "test", "t", "install", "check", "c", "clippy", "bench", "doc", "publish", "fetch", "update"], tool: "Cargo")
        case "rustup": return verb(["update", "install", "toolchain"], tool: "rustup")
        case "go": return verb(["build", "test", "install", "generate", "vet", "mod", "get"], tool: "Go")
        case "make", "gmake":
            guard !a.contains("-n") && !a.contains("-q") else { return nil }
            let t = words.prefix(2).joined(separator: " ")
            return DevMatch(tool: "make", action: t.isEmpty ? "build" : t, symbol: symbol(t))
        case "ninja": return DevMatch(tool: "ninja", action: words.first ?? "build", symbol: "hammer.fill")
        case "cmake":
            guard a.contains("--build") || a.contains("--install") else { return nil }
            return DevMatch(tool: "CMake", action: a.contains("--install") ? "install" : "build", symbol: "hammer.fill")
        case "bazel", "bazelisk": return verb(["build", "test", "fetch", "sync"], tool: "Bazel")
        case "dotnet": return verb(["build", "test", "publish", "restore", "pack"], tool: ".NET")
        case "ruby": return ruby(a)
        case "pod": return verb(["install", "update", "repo", "deintegrate", "outdated"], tool: "CocoaPods")
        case "fastlane": return words.isEmpty ? nil : DevMatch(tool: "fastlane", action: words.prefix(2).joined(separator: " "), symbol: "airplane")
        case "brew": return verb(["install", "upgrade", "reinstall", "update", "bundle", "fetch"], tool: "Homebrew")
        case "carthage": return verb(["update", "bootstrap", "build"], tool: "Carthage")
        case "uv":
            // `uv tool uvx …` / `uv run` start servers (MCP…); only installs count
            if words.first == "tool" { return ["install", "upgrade"].contains(words.dropFirst().first ?? "") ? DevMatch(tool: "uv", action: "tool install", symbol: symbol("install")) : nil }
            return verb(["sync", "pip", "add", "lock", "build", "python"], tool: "uv")
        case "docker", "com.docker.cli", "podman":
            let tool = name == "podman" ? "Podman" : "Docker"
            if let v = words.first, ["compose", "buildx", "image"].contains(v) {
                return verb(["build", "pull", "push", "bake"], tool: tool, icon: "com.docker.docker", words: Array(words.dropFirst()))
                    .map { var m = $0; m.action = v + " " + m.action; return m }
            }
            return verb(["build", "pull", "push", "save", "load"], tool: tool, icon: "com.docker.docker")
        case "git":
            guard let v = words.first, ["clone", "push", "pull", "fetch", "lfs", "submodule", "gc", "repack"].contains(v) else { return nil }
            let subject = v == "clone" ? words.dropFirst().first.map { base($0).replacingOccurrences(of: ".git", with: "") } : nil
            return DevMatch(tool: "Git", action: v, symbol: symbol(v), subject: subject)
        case "rsync", "scp", "rclone", "ditto":
            return DevMatch(tool: name, action: L("sao chép"), symbol: "arrow.left.arrow.right", subject: words.last.map(base))
        case "aria2c", "yt-dlp", "wget":
            return DevMatch(tool: name, action: L("tải xuống"), symbol: "arrow.down.circle.fill", subject: words.last.map(base))
        case "curl":
            // only real downloads to a file; API calls from scripts are filtered by the minimum duration too
            guard let out = opt("-o", "--output") ?? (a.contains("-O") || a.contains("-LO") ? words.last.map(base) : nil) else { return nil }
            return DevMatch(tool: "curl", action: L("tải xuống"), symbol: "arrow.down.circle.fill", subject: base(out))
        case "zip", "unzip", "tar", "bsdtar", "7z", "7zz", "xz", "zstd":
            return DevMatch(tool: name, action: L("nén / giải nén"), symbol: "archivebox.fill", subject: words.last.map(base))
        case "hdiutil": return verb(["create", "convert", "makehybrid", "compact"], tool: "hdiutil")
        case "ffmpeg":
            let input = opt("-i").flatMap { $0.contains("=") ? nil : base($0) }  // lavfi/stream inputs aren't file names
            return DevMatch(tool: "ffmpeg", action: L("xử lý video"), symbol: "film.stack", subject: [input, a.last.map(base)].compactMap { $0 }.joined(separator: " → "))
        case "HandBrakeCLI":
            return DevMatch(tool: "HandBrake", action: "encode", symbol: "film.stack", icon: "fr.handbrake.HandBrake", subject: opt("-i", "--input").map(base))
        case "blender", "Blender":
            guard a.contains("-b") || a.contains("--background") else { return nil }
            let frames = a.contains("-a") || a.contains("--render-anim") ? "render animation" : "render"
            return DevMatch(tool: "Blender", action: frames, symbol: "cube.transparent.fill", icon: "org.blenderfoundation.blender",
                            subject: a.first { $0.hasSuffix(".blend") }.map(base))
        case "magick", "convert", "mogrify":
            return DevMatch(tool: "ImageMagick", action: L("xử lý ảnh"), symbol: "photo.stack.fill", subject: words.last.map(base))
        case "whisper", "whisper-cli":
            return DevMatch(tool: "Whisper", action: L("chép lời"), symbol: "waveform", subject: words.last.map(base))
        case "ollama": return verb(["pull", "create", "push"], tool: "Ollama", icon: "com.electron.ollama")
        case "terraform", "tofu": return verb(["apply", "plan", "init", "destroy"], tool: "Terraform")
        case "pulumi": return verb(["up", "preview", "destroy", "refresh"], tool: "Pulumi")
        case "helm": return verb(["install", "upgrade"], tool: "Helm")
        case "notarytool": return verb(["submit"], tool: "Notarize", icon: "com.apple.dt.Xcode")
        case "altool": return DevMatch(tool: "App Store", action: "upload", symbol: "icloud.and.arrow.up.fill", icon: "com.apple.dt.Xcode")
        case "xcodes": return verb(["install", "download"], tool: "xcodes", icon: "com.apple.dt.Xcode")
        case "Unity":
            guard a.contains("-batchmode") else { return nil }
            return DevMatch(tool: "Unity", action: opt("-buildTarget").map { "build " + $0 } ?? "batch", symbol: "hammer.fill", icon: "com.unity3d.unityhub")
        case "eas": return verb(["build", "submit", "update"], tool: "EAS")
        default: return nil
        }
    }

    /// node <script> …: the package owning the script is the tool (".../node_modules/next/dist/bin/next" → next).
    static func node(_ a: [String]) -> DevMatch? {
        guard let script = a.first(where: { !$0.hasPrefix("-") }) else { return nil }
        var tool = base(script)
        if let r = script.range(of: "node_modules/", options: .backwards) {
            let parts = script[r.upperBound...].split(separator: "/")
            if let first = parts.first { tool = first.hasPrefix("@") && parts.count > 1 ? "\(first)/\(parts[1])" : String(first) }
        }
        if tool == "npm-cli" { tool = "npm" }
        let rest = Array(a.drop { $0 != script }.dropFirst().filter { !$0.hasPrefix("-") })
        let flags = Set(a)
        guard rest.allSatisfy({ !endless.contains($0) }), flags.isDisjoint(with: ["--watch", "-w"]) else { return nil }
        switch tool {
        case "npm", "pnpm", "yarn", "corepack":
            let v = rest.first ?? (tool == "yarn" ? "install" : "")
            if ["install", "i", "ci", "add", "update", "upgrade", "remove", "rebuild", "dedupe", "publish", "pack"].contains(v) {
                return DevMatch(tool: tool, action: v, symbol: symbol(v))
            }
            if ["test", "t"].contains(v) { return DevMatch(tool: tool, action: "test", symbol: symbol("test")) }
            return DevRules.script(tool: tool, ["run", "run-script"].contains(v) ? rest.dropFirst().first : v)
        case "next", "vite", "astro", "nuxt", "nuxi", "svelte-kit", "gatsby", "remix", "parcel", "ng", "react-native", "expo", "turbo", "nx", "lerna",
             "@expo/cli", "@angular/cli", "@react-native-community/cli":
            guard let v = rest.first, ["build", "export", "generate", "test", "lint", "prebuild", "run-ios", "run-android", "run:ios", "run:android",
                                       "bundle", "run", "run-many", "affected"].contains(v) else { return nil }
            return DevMatch(tool: tool, action: rest.prefix(2).joined(separator: " "), symbol: symbol(v))
        case "typescript", "tsc", "webpack", "webpack-cli", "esbuild", "rollup", "tsup", "jest", "eslint", "prettier", "electron-builder", "mocha":
            return DevMatch(tool: tool == "typescript" ? "tsc" : tool, action: rest.first ?? (["jest", "mocha"].contains(tool) ? "test" : "build"),
                            symbol: symbol(["jest", "eslint", "mocha"].contains(tool) ? "test" : "build"))
        case "vitest": return flags.contains("run") || flags.contains("--run") ? DevMatch(tool: "vitest", action: "test", symbol: symbol("test")) : nil
        case "@playwright/test", "playwright": return rest.first == "test" ? DevMatch(tool: "Playwright", action: "test", symbol: symbol("test")) : nil
        case "@electron-forge/cli": return rest.first.map { DevMatch(tool: "Electron Forge", action: $0, symbol: symbol($0)) }
        case "eas-cli": return rest.first.flatMap { ["build", "submit", "update"].contains($0) ? DevMatch(tool: "EAS", action: $0, symbol: symbol($0)) : nil }
        case "firebase-tools", "vercel", "netlify-cli", "wrangler", "aws-cdk", "serverless":
            guard let v = rest.first ?? (tool == "vercel" ? "deploy" : nil), ["deploy", "publish", "synth", "build"].contains(v) else { return nil }
            return DevMatch(tool: tool, action: v, symbol: symbol(v))
        case "prisma": return rest.first.map { DevMatch(tool: "Prisma", action: rest.prefix(2).joined(separator: " "), symbol: symbol($0)) }
        default: return nil
        }
    }

    /// package.json scripts: only ones that end (build, test…), never dev servers.
    static func script(tool: String, _ name: String?) -> DevMatch? {
        guard let s = name, !s.isEmpty, !endless.contains(s) else { return nil }
        let finite = ["build", "test", "lint", "check", "type", "e2e", "deploy", "release", "export", "generate", "compile", "bundle", "package",
                      "dist", "ci", "format", "migrate", "seed", "codegen", "prebuild", "android", "ios"]
        guard finite.contains(where: s.lowercased().contains), !["dev", "start", "serve", "watch"].contains(where: s.lowercased().contains) else { return nil }
        return DevMatch(tool: tool, action: "run " + s, symbol: symbol(s))
    }

    static func ruby(_ a: [String]) -> DevMatch? {
        guard let script = a.first(where: { !$0.hasPrefix("-") }) else { return nil }
        let rest = Array(a.drop { $0 != script }.dropFirst().filter { !$0.hasPrefix("-") })
        switch base(script) {
        case "pod": return classify("pod", ["pod"] + rest)
        case "fastlane": return classify("fastlane", ["fastlane"] + rest)
        case "brew": return classify("brew", ["brew"] + rest)
        case "bundle": return rest.first == "install" || rest.isEmpty ? DevMatch(tool: "Bundler", action: "install", symbol: symbol("install")) : nil
        case "rspec": return DevMatch(tool: "RSpec", action: "test", symbol: symbol("test"))
        default: return nil
        }
    }

    static func python(_ a: [String]) -> DevMatch? {
        var tool: String?
        var rest: [String] = []
        if let i = a.firstIndex(of: "-m"), i + 1 < a.count {
            tool = a[i + 1]
            rest = a[(i + 2)...].filter { !$0.hasPrefix("-") }
        } else if let script = a.first(where: { !$0.hasPrefix("-") }) {
            tool = base(script)
            rest = Array(a.drop { $0 != script }.dropFirst().filter { !$0.hasPrefix("-") })
        }
        let v = rest.first ?? ""
        switch tool ?? "" {
        case "pip", "pip3": return ["install", "download", "wheel"].contains(v) ? DevMatch(tool: "pip", action: v, symbol: symbol(v)) : nil
        case "poetry": return ["install", "update", "add", "lock", "build"].contains(v) ? DevMatch(tool: "Poetry", action: v, symbol: symbol(v)) : nil
        case "conda", "mamba": return ["install", "create", "update", "env"].contains(v) ? DevMatch(tool: "Conda", action: v, symbol: symbol(v)) : nil
        case "pytest", "py.test": return DevMatch(tool: "pytest", action: "test", symbol: symbol("test"))
        case "manage": return v == "test" ? DevMatch(tool: "Django", action: "test", symbol: symbol("test")) : nil
        case "build": return DevMatch(tool: "python", action: "build", symbol: "hammer.fill")
        case "hf", "huggingface-cli": return ["download", "upload"].contains(v) ? DevMatch(tool: "Hugging Face", action: v, symbol: symbol(v)) : nil
        case "tox", "nox": return DevMatch(tool: tool!, action: "test", symbol: symbol("test"))
        default: return nil
        }
    }

    /// "…/mobile-app/ios" → "mobile-app": platform subfolders say less than the project they belong to.
    static func project(_ path: String) -> String {
        var url = URL(fileURLWithPath: path)
        while ["ios", "macos", "android", "app", "web", "linux", "windows"].contains(url.lastPathComponent.lowercased()), url.pathComponents.count > 2 {
            url.deleteLastPathComponent()
        }
        return url.lastPathComponent
    }
}

/// Past run times per task, for "≈ 2 phút nữa" and a progress estimate.
struct TaskHistory {
    private var runs: [String: [Double]] = (UserDefaults.standard.dictionary(forKey: "taskHistory") as? [String: [Double]]) ?? [:]

    func expected(_ key: String) -> Double? {
        guard let r = runs[key], !r.isEmpty else { return nil }
        return r.sorted()[r.count / 2]  // median: one odd run doesn't skew it
    }

    mutating func record(_ key: String, _ seconds: Double) {
        guard seconds >= 5 else { return }
        runs[key] = Array(((runs[key] ?? []) + [seconds]).suffix(5))
        if runs.count > 300 { runs = Dictionary(uniqueKeysWithValues: runs.shuffled().prefix(250).map { ($0.key, $0.value) }) }
        UserDefaults.standard.set(runs, forKey: "taskHistory")
    }

    /// (progress 0…0.99, label) from elapsed vs the usual duration.
    static func estimate(elapsed: Double, expected: Double?) -> (Double?, String) {
        let time = mmss(elapsed)
        guard let e = expected, e >= 10 else { return (nil, time) }
        let left = e - elapsed
        if left <= 0 { return (0.99, time + L(" · lâu hơn thường lệ")) }
        return (min(elapsed / e, 0.99), time + " · ≈ " + (left < 60 ? "\(Int(left))s" : L("{0} phút", Int(left / 60) + 1)) + L(" nữa"))
    }
}

/// Finds running dev tasks in the process table every tick.
final class DevScanner {
    struct Running { var match: DevMatch; var title: String; var since: Date; var key: String; var lastBusy: Date? }

    private var argvCache: [pid_t: (name: String, match: DevMatch?)] = [:]
    private var running: [String: Running] = [:]  // by job id
    private var history = TaskHistory()
    private var seenGit: Set<pid_t> = []
    /// git push seen: the repo folder, for GitHub Actions tracking.
    var onPush: (String) -> Void = { _ in }

    static let compilers: Set<String> = ["swift-frontend", "clang", "clang++", "ld", "swift-driver", "actool", "ibtool", "swiftc", "libtool",
                                         "dsymutil", "codesign", "swift-build", "ibtoold", "momc", "mapc", "lipo", "swift-plugin-server"]
    /// GUI editors/renderers: a task while they work hard (exports, renders, game builds).
    static let renderApps: [(prefix: String, name: String)] = [
        ("com.blackmagic-design.DaVinciResolve", "DaVinci Resolve"), ("com.apple.FinalCut", "Final Cut Pro"), ("com.apple.Compressor", "Compressor"),
        ("com.adobe.PremierePro", "Premiere Pro"), ("com.adobe.AfterEffects", "After Effects"), ("com.adobe.ame", "Media Encoder"),
        ("org.blenderfoundation.blender", "Blender"), ("fr.handbrake.HandBrake", "HandBrake"), ("com.lemon.lvoverseas", "CapCut"),
        ("com.topazlabs", "Topaz"), ("com.adobe.LightroomClassic", "Lightroom"), ("com.unity3d.UnityEditor", "Unity"),
        ("com.epicgames.UnrealEditor", "Unreal"), ("com.apple.motionapp", "Motion"), ("com.apple.iMovieApp", "iMovie"),
    ]

    func scan(_ procs: [PInfo], kids: [pid_t: [PInfo]], cpu: (pid_t) -> Double, now: Date) -> [Job] {
        let byPid = Dictionary(procs.map { ($0.pid, $0) }, uniquingKeysWith: { a, _ in a })
        argvCache = argvCache.filter { byPid[$0.key]?.name == $0.value.name }
        seenGit.formIntersection(byPid.keys)

        func match(_ p: PInfo) -> DevMatch? {
            if let c = argvCache[p.pid] { return c.match }
            let m = DevRules.names.contains(p.name) || DevRules.isPython(p.name) ? DevRules.classify(p.name, Proc.argv(p.pid)) : nil
            argvCache[p.pid] = (p.name, m)
            return m
        }
        // CI jobs are shown by their runner; nested tools belong to the outermost one (npm → node next build)
        func ancestors(_ p: PInfo) -> [PInfo] {
            var out: [PInfo] = []
            var cur = byPid[p.ppid]
            while let c = cur, c.pid > 1, out.count < 40 { out.append(c); cur = byPid[c.ppid] }
            return out
        }
        func treeCPU(_ root: pid_t) -> Double {
            var total = cpu(root), stack = [root]
            while let p = stack.popLast() { for c in kids[p] ?? [] { total += cpu(c.pid); stack.append(c.pid) } }
            return total
        }

        var found: [String: Running] = [:]
        func keep(_ id: String, _ m: DevMatch, title: String, since: Date, key: String, busy: Bool) {
            var r = running[id] ?? Running(match: m, title: title, since: since, key: key, lastBusy: nil)
            r.match = m
            if busy { r.lastBusy = now }
            found[id] = r
        }

        for p in procs {
            if p.name == "git", seenGit.insert(p.pid).inserted, Proc.argv(p.pid).dropFirst().first == "push", let dir = Proc.cwd(p.pid) {
                onPush(dir)
            }
            // Xcode's own builds: the build service has compilers as children only while it builds
            if p.name == "SWBBuildService" || p.name == "XCBBuildService" {
                let compiling = (kids[p.pid] ?? []).filter { Self.compilers.contains($0.name) }
                let up = ancestors(p)
                guard !up.contains(where: { match($0) != nil || $0.name == "gitlab-runner" || $0.name == "Runner.Worker" }) else { continue }
                let id = "xcode:\(p.pid)"
                if let c = compiling.first {
                    let name = Self.derivedDataProject(Proc.argv(c.pid)) ?? running[id]?.title ?? "Xcode"
                    keep(id, DevMatch(tool: "Xcode", action: "build", symbol: "hammer.fill", icon: "com.apple.dt.Xcode"),
                         title: name, since: running[id]?.since ?? now, key: "xcode|\(name)", busy: true)
                } else if let r = running[id], let b = r.lastBusy, now.timeIntervalSince(b) < 4 {
                    found[id] = r  // between build phases nothing compiles for a moment
                }
                continue
            }
            guard let m = match(p) else { continue }
            let up = ancestors(p)
            if up.contains(where: { $0.name == "gitlab-runner" || $0.name == "Runner.Worker" || match($0) != nil }) { continue }
            let id = "dev:\(p.pid)"
            let folder = Proc.cwd(p.pid).map(DevRules.project) ?? m.tool
            let title = m.subject.flatMap { $0.isEmpty ? nil : $0 } ?? folder
            let key = [m.tool, m.action, folder].joined(separator: "|")
            if m.busyOnly {
                let busy = treeCPU(p.pid) > 40
                // the Gradle daemon only counts when no gradle client is already shown
                if busy || (running[id]?.lastBusy.map { now.timeIntervalSince($0) < 8 } ?? false) {
                    keep(id, m, title: title, since: running[id]?.since ?? now, key: key, busy: busy)
                }
            } else if now.timeIntervalSince(p.start) >= 3 {  // quick commands are not tasks
                keep(id, m, title: title, since: p.start, key: key, busy: true)
            }
        }
        if found.values.contains(where: { $0.match.tool == "Gradle" && !$0.match.busyOnly }) {
            found = found.filter { !($0.value.match.tool == "Gradle" && $0.value.match.busyOnly) }
        }
        // heavy work in GUI render/edit apps
        for app in NSWorkspace.shared.runningApplications {
            guard let bid = app.bundleIdentifier, let r = Self.renderApps.first(where: { bid.hasPrefix($0.prefix) }) else { continue }
            let id = "app:\(app.processIdentifier)"
            let load = treeCPU(app.processIdentifier)
            let prev = running[id]
            let busy = load > 150
            // sustained 10 s before it counts, so scrubbing a timeline doesn't
            let heavySince = busy ? (prev?.since ?? now) : prev?.since
            guard let since = heavySince, busy || (prev?.lastBusy.map { now.timeIntervalSince($0) < 8 } ?? false) else { continue }
            let m = DevMatch(tool: r.name, action: L("đang xử lý · {0}% CPU", Int(load)), symbol: "film.stack", icon: bid, busyOnly: true)
            keep(id, m, title: r.name, since: since, key: "app|\(bid)", busy: busy)
        }

        for (id, r) in running where found[id] == nil { history.record(r.key, now.timeIntervalSince(r.since)) }
        running = found
        return found.map { id, r in
            let elapsed = now.timeIntervalSince(r.since)
            let shown = !r.match.busyOnly || id.hasPrefix("dev:") || elapsed >= 10
            let (progress, time) = TaskHistory.estimate(elapsed: elapsed, expected: history.expected(r.key))
            return Job(id: id, title: "\(r.match.tool) · \(r.title)", detail: "\(r.match.action) · \(time)", working: shown,
                       progress: progress, estimated: true, icon: r.match.icon, symbol: r.match.symbol, tool: r.match.tool)
        }
        .filter(\.working)
        .sorted { $0.id < $1.id }
    }

    /// ".../DerivedData/MobileApp-abcdef/..." → "MobileApp"
    static func derivedDataProject(_ argv: [String]) -> String? {
        for s in argv {
            guard let r = s.range(of: "/DerivedData/") else { continue }
            let folder = s[r.upperBound...].prefix { $0 != "/" }
            guard let dash = folder.lastIndex(of: "-") else { continue }
            return String(folder[..<dash])
        }
        return nil
    }
}

// MARK: - Downloads & copies with real progress (Safari, Chrome, Finder, AirDrop publish NSProgress)

final class FileProgressWatcher {
    private final class Box: @unchecked Sendable { let p: Progress; init(_ p: Progress) { self.p = p } }
    private var active: [ObjectIdentifier: Progress] = [:]
    private var tokens: [Any] = []

    func start() {
        guard tokens.isEmpty else { return }
        // progress reaches subscribers of the file's folder, not of every ancestor: subscribe where transfers land
        // shortcut: files in subfolders of these aren't seen, add their folders here if needed
        let home = FileManager.default.homeDirectoryForCurrentUser
        tokens = ["Downloads", "Desktop", "Documents", "Movies", "Pictures", "Music"].map { folder in
            Progress.addSubscriber(forFileURL: home.appendingPathComponent(folder)) { [weak self] p in
                let box = Box(p)
                DispatchQueue.main.async { self?.active[ObjectIdentifier(box.p)] = box.p }
                return { DispatchQueue.main.async { self?.active[ObjectIdentifier(box.p)] = nil } }
            }
        }
    }

    func stop() {
        tokens.forEach(Progress.removeSubscriber)
        tokens = []
        active = [:]
    }

    func jobs() -> [Job] {
        active.values.compactMap { p in
            guard !p.isFinished, !p.isCancelled, p.totalUnitCount >= 5_000_000 else { return nil }  // small files finish before anyone looks
            let url = p.userInfo[.fileURLKey] as? URL
            let kind = p.fileOperationKind
            let verb = kind == .downloading ? L("Tải xuống") : kind == .copying ? L("Sao chép") : kind == .receiving ? L("Nhận") :
                       kind == .decompressingAfterDownloading ? L("Giải nén") : L("Truyền file")
            let size = ByteCountFormatter.string(fromByteCount: p.totalUnitCount, countStyle: .file)
            let eta = p.estimatedTimeRemaining.map { L(" · còn ") + ($0 < 60 ? "\(Int($0))s" : L("{0} phút", Int($0 / 60) + 1)) } ?? ""
            return Job(id: "file:\(ObjectIdentifier(p).hashValue)", title: "\(verb) · \(url?.lastPathComponent ?? "file")",
                       detail: "\(Int(p.fractionCompleted * 100))% · \(size)\(eta)", working: true, progress: p.fractionCompleted,
                       symbol: kind == .downloading ? "arrow.down.circle.fill" : "doc.on.doc.fill", tool: "Tải file")
        }
    }
}
