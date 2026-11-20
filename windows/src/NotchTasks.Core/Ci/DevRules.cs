using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Ci;

/// Long dev tasks (builds, tests, installs, containers, renders, transfers). Port of electron/src/core/devscan.js
/// lines 10-379 (Mac: Sources/DevTasks.swift). Pure: reads only the process name and its arguments.
public static class DevRules
{
    private static readonly string[] ScriptExtensions = { ".js", ".cjs", ".mjs", ".rb", ".py", ".blend" };

    private static readonly string[] XcodeInfoFlags = { "-list", "-version", "-showsdks", "-showbuildsettings", "-showdestinations", "-help" };

    /// Commands that never finish on their own (dev servers, watchers).
    public static IReadOnlySet<string> Endless { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "dev", "start", "serve", "watch", "preview", "--watch", "-w", "storybook", "daemon", "attach", "run-server",
    };

    /// Process names (normalized: lower case, no .exe) worth reading arguments for.
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "xcodebuild", "swift", "swift-build", "swift-test", "swift-package", "java", "dart", "dartvm", "node", "bun", "deno",
        "cargo", "go", "make", "gmake", "ninja", "cmake", "bazel", "bazelisk", "ruby", "pod", "fastlane", "brew", "carthage",
        "uv", "docker", "com.docker.cli", "podman", "git", "rsync", "scp", "ditto", "rclone", "aria2c", "yt-dlp", "wget", "curl",
        "zip", "unzip", "tar", "bsdtar", "7z", "7zz", "xz", "zstd", "hdiutil", "ffmpeg", "handbrakecli", "blender",
        "magick", "convert", "mogrify", "whisper", "whisper-cli", "ollama", "terraform", "tofu", "pulumi", "helm", "notarytool",
        "altool", "unity", "dotnet", "rustup", "xcodes", "flutter", "gradle", "mvn", "eas", "expo",
        // Windows
        "msbuild", "robocopy", "winget",
    };

    /// Tool names shown in settings (the Mac's list, plus the Windows tools).
    public static IReadOnlyList<string> KnownTools { get; } = new[]
    {
        ".NET", "App Store", "Bazel", "Blender", "Bundler", "CMake", "Cargo", "Carthage", "CocoaPods", "Conda", "Dart", "Deno",
        "Django", "Docker", "EAS", "Electron Forge", "Flutter", "Git", "GitHub Actions", "GitLab Runner", "Go", "Gradle",
        "HandBrake", "Helm", "Homebrew", "Hugging Face", "ImageMagick", "Maven", "MSBuild", "Notarize", "Ollama", "Playwright", "Podman",
        "Poetry", "Prisma", "Pulumi", "RSpec", "SwiftPM", "Terraform", "Tải file", "Unity", "Whisper", "Xcode", "build_runner",
        "bun", "curl", "esbuild", "fastlane", "ffmpeg", "hdiutil", "jest", "make", "next", "ninja", "npm", "pip", "pnpm",
        "pytest", "python", "robocopy", "rsync", "rustup", "tsc", "uv", "vite", "vitest", "webpack", "wget", "winget", "xcodes", "yarn", "yt-dlp", "zip",
    };

    /// GUI editors and renderers: a task while they work hard. Matched by process name (no .exe).
    public static IReadOnlyList<(string Exe, string Name)> RenderApps { get; } = new (string Exe, string Name)[]
    {
        ("resolve", "DaVinci Resolve"),
        ("adobe premiere pro", "Premiere Pro"),
        ("afterfx", "After Effects"),
        ("adobe media encoder", "Media Encoder"),
        ("blender", "Blender"),
        ("handbrake", "HandBrake"),
        ("capcut", "CapCut"),
        ("topaz video ai", "Topaz"),
        ("lightroom", "Lightroom"),
        ("unity", "Unity"),
        ("unrealeditor", "Unreal"),
    };

    /// "m:ss" or "h:mm:ss", rounded up like the Mac.
    public static string Mmss(double seconds)
    {
        var t = (long)Math.Ceiling(Math.Max(0, seconds));
        return t >= 3600
            ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}"
            : $"{t / 60}:{t % 60:00}";
    }

    /// Last path component, either separator, without a script extension.
    public static string Base(string s)
    {
        var trimmed = s.TrimEnd('\\', '/');
        var b = trimmed.Split('\\', '/')[^1];
        foreach (var ext in ScriptExtensions)
        {
            if (b.EndsWith(ext, StringComparison.Ordinal))
            {
                return b[..^ext.Length];
            }
        }
        return b;
    }

    /// The symbol for a task's action: by the words it contains.
    public static string Symbol(string action)
    {
        var s = action.ToLowerInvariant();
        bool Any(params string[] words) => words.Any(w => s.Contains(w, StringComparison.Ordinal));

        if (Any("test", "e2e", "check", "lint", "analyze", "clippy", "vet", "typecheck"))
        {
            return "checkmark.diamond.fill";
        }
        if (Any("deploy", "push", "publish", "upload", "release", "submit", "apply", "export"))
        {
            return "icloud.and.arrow.up.fill";
        }
        if (Any("install", "ci", "add", "update", "upgrade", "sync", "get", "fetch", "pull", "resolve", "bootstrap", "download", "restore", "clone"))
        {
            return "shippingbox.fill";
        }
        return "hammer.fill";
    }

    /// The symbol for a tool when its action gives none.
    public static string DefaultSymbol(string tool)
    {
        return tool switch
        {
            "GitLab Runner" or "GitHub Actions" => "server.rack",
            "Tải file" or "curl" or "wget" or "yt-dlp" => "arrow.down.circle.fill",
            "ffmpeg" or "HandBrake" => "film.stack",
            "Git" => "arrow.triangle.branch",
            _ => "hammer.fill",
        };
    }

    /// True for "python", "python3", "python3.12"...
    public static bool IsPython(string name) => name == "python" || name.StartsWith("python", StringComparison.Ordinal);

    /// What a process is doing, read from its name and arguments. The name is normalized (lower case, no .exe).
    /// argv[0] is the program itself and is skipped. Null when the process is not a finite dev task.
    public static DevMatch? Classify(string name, IReadOnlyList<string> argv)
    {
        var a = argv.Skip(1).ToList();
        var words = a.Where(x => !x.StartsWith('-')).ToList();
        if (IsPython(name))
        {
            return Python(a);
        }

        // The value after the first of these flags that has one.
        string? Opt(params string[] flags)
        {
            foreach (var f in flags)
            {
                var i = a.IndexOf(f);
                if (i >= 0 && i + 1 < a.Count)
                {
                    return a[i + 1];
                }
            }
            return null;
        }

        // A tool whose first word is one of `allowed`; the action is the first two words.
        DevMatch? Verb(string[] allowed, string tool, List<string>? w = null)
        {
            var list = w ?? words;
            if (list.Count == 0 || !allowed.Contains(list[0]))
            {
                return null;
            }
            var action = string.Join(" ", list.Take(2));
            return new DevMatch(tool, action, Symbol(action));
        }

        switch (name)
        {
            case "xcodebuild":
            {
                if (a.Any(x => XcodeInfoFlags.Contains(x.ToLowerInvariant())))
                {
                    return null;
                }
                string[] acts = { "archive", "test", "build", "build-for-testing", "test-without-building", "analyze", "docbuild", "clean" };
                var act = a.FirstOrDefault(x => acts.Contains(x))
                    ?? (a.Contains("-exportArchive") ? "export"
                        : a.Contains("-resolvePackageDependencies") ? "resolve packages" : "build");
                var ws = Opt("-workspace", "-project");
                var scheme = Opt("-scheme");
                var action = string.Join(" · ", new[] { act, scheme }.Where(s => !string.IsNullOrEmpty(s)));
                return new DevMatch("Xcode", action, Symbol(act),
                    string.IsNullOrEmpty(ws) ? null : Folders.Project(WithoutLastComponent(ws)));
            }
            case "msbuild":
            {
                // MSBuild /t:Restore /p:Configuration=Release App.sln
                var targets = a.Where(IsTargetSwitch).Select(x => x[3..]).ToList();
                var act = targets.Count > 0 ? string.Join(" ", targets) : "build";
                var proj = a.FirstOrDefault(IsProjectFile);
                return new DevMatch("MSBuild", act, Symbol(act), proj is null ? null : WithoutWordExtension(Base(proj)));
            }
            case "swift-build":
            case "swift-test":
            case "swift-package":
            {
                var act = name == "swift-test" ? "test"
                    : name == "swift-package" ? (string.IsNullOrEmpty(At(words, 0)) ? "package" : words[0])
                    : "build";
                if (name == "swift-package" && !new[] { "resolve", "update", "archive-source" }.Contains(act))
                {
                    return null;
                }
                return new DevMatch("SwiftPM", act, Symbol(act));
            }
            case "swift":
                return Verb(new[] { "build", "test" }, "SwiftPM");
            case "java":
            {
                var i = a.FindIndex(x => x.EndsWith("GradleWrapperMain", StringComparison.Ordinal) || x == "org.gradle.launcher.GradleMain");
                if (i >= 0)
                {
                    var tasks = a.Skip(i + 1).Where(x => !x.StartsWith('-')).ToList();
                    if (!tasks.All(t => !Endless.Contains(t) && t != "--continuous"))
                    {
                        return null;
                    }
                    var act = string.Join(" ", tasks.Take(2));
                    return new DevMatch("Gradle", string.IsNullOrEmpty(act) ? "build" : act, Symbol(act));
                }
                // Android Studio builds through the daemon (no client process): a busy daemon is a build.
                if (a.Contains("org.gradle.launcher.daemon.bootstrap.GradleDaemon"))
                {
                    return new DevMatch("Gradle", "build (Android Studio)", "hammer.fill", null, BusyOnly: true);
                }
                var j = a.IndexOf("org.codehaus.plexus.classworlds.launcher.Launcher");
                if (j >= 0)
                {
                    var goals = string.Join(" ", a.Skip(j + 1).Where(x => !x.StartsWith('-')).Take(2));
                    return new DevMatch("Maven", string.IsNullOrEmpty(goals) ? "build" : goals, Symbol(goals));
                }
                return null;
            }
            case "dart":
            case "dartvm":
            {
                var i = a.FindIndex(x => x.EndsWith("flutter_tools.snapshot", StringComparison.Ordinal));
                if (i >= 0)
                {
                    var rest = a.Skip(i + 1).Where(x => !x.StartsWith('-')).ToList();
                    return Verb(new[] { "build", "test", "pub", "precache", "analyze", "drive", "install", "clean", "gen-l10n", "packages", "create", "upgrade" },
                        "Flutter", rest);
                }
                if (a.Any(x => x.Contains("build_runner", StringComparison.Ordinal)) && a.Contains("build"))
                {
                    return new DevMatch("build_runner", "build", "hammer.fill");
                }
                return Verb(new[] { "compile", "test", "pub", "analyze" }, "Dart");
            }
            case "node":
                return Node(a);
            case "bun":
            {
                var v = At(words, 0);
                if (v is null)
                {
                    return null;
                }
                if (new[] { "install", "i", "add", "update", "build", "test" }.Contains(v))
                {
                    return new DevMatch("bun", v, Symbol(v));
                }
                return v == "run" ? ScriptTask("bun", At(words, 1)) : null;
            }
            case "deno":
                return Verb(new[] { "compile", "test", "install", "bundle" }, "Deno");
            case "cargo":
                return Verb(new[] { "build", "b", "test", "t", "install", "check", "c", "clippy", "bench", "doc", "publish", "fetch", "update" }, "Cargo");
            case "rustup":
                return Verb(new[] { "update", "install", "toolchain" }, "rustup");
            case "go":
                return Verb(new[] { "build", "test", "install", "generate", "vet", "mod", "get" }, "Go");
            case "make":
            case "gmake":
            {
                if (a.Contains("-n") || a.Contains("-q"))
                {
                    return null;
                }
                var t = string.Join(" ", words.Take(2));
                return new DevMatch("make", string.IsNullOrEmpty(t) ? "build" : t, Symbol(t));
            }
            case "ninja":
            {
                var v = At(words, 0);
                return new DevMatch("ninja", string.IsNullOrEmpty(v) ? "build" : v, "hammer.fill");
            }
            case "cmake":
            {
                if (!a.Contains("--build") && !a.Contains("--install"))
                {
                    return null;
                }
                return new DevMatch("CMake", a.Contains("--install") ? "install" : "build", "hammer.fill");
            }
            case "bazel":
            case "bazelisk":
                return Verb(new[] { "build", "test", "fetch", "sync" }, "Bazel");
            case "dotnet":
                return Verb(new[] { "build", "test", "publish", "restore", "pack" }, ".NET");
            case "ruby":
                return Ruby(a);
            case "pod":
                return Verb(new[] { "install", "update", "repo", "deintegrate", "outdated" }, "CocoaPods");
            case "fastlane":
                return words.Count > 0 ? new DevMatch("fastlane", string.Join(" ", words.Take(2)), "airplane") : null;
            case "brew":
                return Verb(new[] { "install", "upgrade", "reinstall", "update", "bundle", "fetch" }, "Homebrew");
            case "carthage":
                return Verb(new[] { "update", "bootstrap", "build" }, "Carthage");
            case "winget":
                return Verb(new[] { "install", "upgrade", "update", "uninstall" }, "winget");
            case "uv":
            {
                // `uv tool uvx …` and `uv run` start servers (MCP…); only installs count.
                if (At(words, 0) == "tool")
                {
                    var v1 = At(words, 1) ?? string.Empty;
                    return v1 is "install" or "upgrade" ? new DevMatch("uv", "tool install", Symbol("install")) : null;
                }
                return Verb(new[] { "sync", "pip", "add", "lock", "build", "python" }, "uv");
            }
            case "docker":
            case "com.docker.cli":
            case "podman":
            {
                var tool = name == "podman" ? "Podman" : "Docker";
                var v = At(words, 0);
                if (v is not null && new[] { "compose", "buildx", "image" }.Contains(v))
                {
                    var m = Verb(new[] { "build", "pull", "push", "bake" }, tool, words.Skip(1).ToList());
                    return m is null ? null : m with { Action = $"{v} {m.Action}" };
                }
                return Verb(new[] { "build", "pull", "push", "save", "load" }, tool);
            }
            case "git":
            {
                var v = At(words, 0);
                if (v is null || !new[] { "clone", "push", "pull", "fetch", "lfs", "submodule", "gc", "repack" }.Contains(v))
                {
                    return null;
                }
                string? subject = null;
                if (v == "clone" && words.Count > 1 && !string.IsNullOrEmpty(words[1]))
                {
                    subject = Base(words[1]);
                    if (subject.EndsWith(".git", StringComparison.Ordinal))
                    {
                        subject = subject[..^4];
                    }
                }
                return new DevMatch("Git", v, Symbol(v), subject);
            }
            case "rsync":
            case "scp":
            case "rclone":
            case "ditto":
            case "robocopy":
                return new DevMatch(name, L10n.L("copying"), "arrow.left.arrow.right", LastBase(words));
            case "aria2c":
            case "yt-dlp":
            case "wget":
                return new DevMatch(name, L10n.L("downloading"), "arrow.down.circle.fill", LastBase(words));
            case "curl":
            {
                // Only real downloads to a file; API calls from scripts are filtered by the minimum duration too.
                var o = Opt("-o", "--output");
                string? outp = !string.IsNullOrEmpty(o) ? o
                    : (((a.Contains("-O") || a.Contains("-LO")) && words.Count > 0) ? Base(words[^1]) : null);
                if (string.IsNullOrEmpty(outp))
                {
                    return null;
                }
                return new DevMatch("curl", L10n.L("downloading"), "arrow.down.circle.fill", Base(outp));
            }
            case "zip":
            case "unzip":
            case "tar":
            case "bsdtar":
            case "7z":
            case "7zz":
            case "xz":
            case "zstd":
                return new DevMatch(name, L10n.L("compressing / extracting"), "archivebox.fill", LastBase(words));
            case "hdiutil":
                return Verb(new[] { "create", "convert", "makehybrid", "compact" }, "hdiutil");
            case "ffmpeg":
            {
                var inp = Opt("-i");
                var input = !string.IsNullOrEmpty(inp) && !inp.Contains('=') ? Base(inp) : null;  // lavfi/stream inputs are not file names
                var last = a.Count > 0 ? Base(a[^1]) : null;
                var subject = string.Join(" → ", new[] { input, last }.Where(s => !string.IsNullOrEmpty(s)));
                return new DevMatch("ffmpeg", L10n.L("processing video"), "film.stack", subject);
            }
            case "handbrakecli":
            {
                var input = Opt("-i", "--input");
                return new DevMatch("HandBrake", "encode", "film.stack", string.IsNullOrEmpty(input) ? null : Base(input));
            }
            case "blender":
            {
                if (!a.Contains("-b") && !a.Contains("--background"))
                {
                    return null;
                }
                var frames = a.Contains("-a") || a.Contains("--render-anim") ? "render animation" : "render";
                var blend = a.FirstOrDefault(x => x.EndsWith(".blend", StringComparison.Ordinal));
                return new DevMatch("Blender", frames, "cube.transparent.fill", blend is null ? null : Base(blend));
            }
            case "magick":
            case "convert":
            case "mogrify":
                return new DevMatch("ImageMagick", L10n.L("processing images"), "photo.stack.fill", LastBase(words));
            case "whisper":
            case "whisper-cli":
                return new DevMatch("Whisper", L10n.L("transcribing"), "waveform", LastBase(words));
            case "ollama":
                return Verb(new[] { "pull", "create", "push" }, "Ollama");
            case "terraform":
            case "tofu":
                return Verb(new[] { "apply", "plan", "init", "destroy" }, "Terraform");
            case "pulumi":
                return Verb(new[] { "up", "preview", "destroy", "refresh" }, "Pulumi");
            case "helm":
                return Verb(new[] { "install", "upgrade" }, "Helm");
            case "notarytool":
                return Verb(new[] { "submit" }, "Notarize");
            case "altool":
                return new DevMatch("App Store", "upload", "icloud.and.arrow.up.fill");
            case "xcodes":
                return Verb(new[] { "install", "download" }, "xcodes");
            case "unity":
            {
                if (!a.Contains("-batchmode"))
                {
                    return null;
                }
                var bt = Opt("-buildTarget");
                return new DevMatch("Unity", !string.IsNullOrEmpty(bt) ? $"build {bt}" : "batch", "hammer.fill");
            }
            case "eas":
                return Verb(new[] { "build", "submit", "update" }, "EAS");
            default:
                return null;
        }
    }

    // ---- helpers ----

    private static string? At(IReadOnlyList<string> xs, int i) => i >= 0 && i < xs.Count ? xs[i] : null;

    private static string? LastBase(List<string> words) => words.Count > 0 ? Base(words[^1]) : null;

    /// A switch such as "/t:Build" or "-t:Build": a slash or dash, t, colon.
    private static bool IsTargetSwitch(string x) =>
        x.Length >= 3 && (x[0] == '/' || x[0] == '-') && (x[1] == 't' || x[1] == 'T') && x[2] == ':';

    private static bool IsProjectFile(string x) =>
        x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || x.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase);

    /// The path without its last component ("C:\src\Shop\App.xcworkspace" -> "C:\src\Shop").
    private static string WithoutLastComponent(string p)
    {
        var k = p.LastIndexOfAny(new[] { '\\', '/' });
        return k >= 0 ? p[..k] : p;
    }

    /// The name without a final ".word" ("App.sln" -> "App"), as the regex \.\w+$ strips it.
    private static string WithoutWordExtension(string s)
    {
        var dot = s.LastIndexOf('.');
        if (dot < 0 || dot == s.Length - 1)
        {
            return s;
        }
        for (var i = dot + 1; i < s.Length; i++)
        {
            if (!IsWordChar(s[i]))
            {
                return s;
            }
        }
        return s[..dot];
    }

    private static bool IsWordChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    /// node <script> ...: the package owning the script is the tool (".../node_modules/next/dist/bin/next" -> next).
    private static DevMatch? Node(List<string> a)
    {
        var script = a.FirstOrDefault(x => !x.StartsWith('-'));
        if (script is null)
        {
            return null;
        }
        var tool = Base(script);
        var norm = script.Replace('\\', '/');
        const string modules = "node_modules/";
        var at = norm.LastIndexOf(modules, StringComparison.Ordinal);
        if (at >= 0)
        {
            var parts = norm[(at + modules.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                tool = parts[0].StartsWith('@') && parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : parts[0];
            }
        }
        if (tool == "npm-cli")
        {
            tool = "npm";
        }
        var i = a.IndexOf(script);
        var rest = a.Skip(i + 1).Where(x => !x.StartsWith('-')).ToList();
        var flags = a.ToHashSet(StringComparer.Ordinal);
        if (!rest.All(x => !Endless.Contains(x)) || flags.Contains("--watch") || flags.Contains("-w"))
        {
            return null;
        }

        switch (tool)
        {
            case "npm":
            case "pnpm":
            case "yarn":
            case "corepack":
            {
                var v = At(rest, 0) ?? (tool == "yarn" ? "install" : string.Empty);
                if (new[] { "install", "i", "ci", "add", "update", "upgrade", "remove", "rebuild", "dedupe", "publish", "pack" }.Contains(v))
                {
                    return new DevMatch(tool, v, Symbol(v));
                }
                if (v is "test" or "t")
                {
                    return new DevMatch(tool, "test", Symbol("test"));
                }
                return ScriptTask(tool, v is "run" or "run-script" ? At(rest, 1) : v);
            }
            case "next":
            case "vite":
            case "astro":
            case "nuxt":
            case "nuxi":
            case "svelte-kit":
            case "gatsby":
            case "remix":
            case "parcel":
            case "ng":
            case "react-native":
            case "expo":
            case "turbo":
            case "nx":
            case "lerna":
            case "@expo/cli":
            case "@angular/cli":
            case "@react-native-community/cli":
            {
                var v = At(rest, 0);
                if (v is null || !new[]
                    {
                        "build", "export", "generate", "test", "lint", "prebuild", "run-ios", "run-android", "run:ios", "run:android",
                        "bundle", "run", "run-many", "affected",
                    }.Contains(v))
                {
                    return null;
                }
                return new DevMatch(tool, string.Join(" ", rest.Take(2)), Symbol(v));
            }
            case "typescript":
            case "tsc":
            case "webpack":
            case "webpack-cli":
            case "esbuild":
            case "rollup":
            case "tsup":
            case "jest":
            case "eslint":
            case "prettier":
            case "electron-builder":
            case "mocha":
            {
                var testy = tool is "jest" or "mocha" or "eslint";
                var action = At(rest, 0) ?? (tool is "jest" or "mocha" ? "test" : "build");
                return new DevMatch(tool == "typescript" ? "tsc" : tool, action, Symbol(testy ? "test" : "build"));
            }
            case "vitest":
                return flags.Contains("run") || flags.Contains("--run") ? new DevMatch("vitest", "test", Symbol("test")) : null;
            case "@playwright/test":
            case "playwright":
                return At(rest, 0) == "test" ? new DevMatch("Playwright", "test", Symbol("test")) : null;
            case "@electron-forge/cli":
            {
                var v = At(rest, 0);
                return string.IsNullOrEmpty(v) ? null : new DevMatch("Electron Forge", v, Symbol(v));
            }
            case "eas-cli":
            {
                var v = At(rest, 0);
                return v is "build" or "submit" or "update" ? new DevMatch("EAS", v, Symbol(v)) : null;
            }
            case "firebase-tools":
            case "vercel":
            case "netlify-cli":
            case "wrangler":
            case "aws-cdk":
            case "serverless":
            {
                var v = At(rest, 0) ?? (tool == "vercel" ? "deploy" : null);
                if (v is null || !new[] { "deploy", "publish", "synth", "build" }.Contains(v))
                {
                    return null;
                }
                return new DevMatch(tool, v, Symbol(v));
            }
            case "prisma":
            {
                var v = At(rest, 0);
                return string.IsNullOrEmpty(v) ? null : new DevMatch("Prisma", string.Join(" ", rest.Take(2)), Symbol(v));
            }
            default:
                return null;
        }
    }

    /// package.json scripts: only ones that end (build, test...), never dev servers.
    private static DevMatch? ScriptTask(string tool, string? name)
    {
        if (string.IsNullOrEmpty(name) || Endless.Contains(name))
        {
            return null;
        }
        var s = name.ToLowerInvariant();
        string[] finite =
        {
            "build", "test", "lint", "check", "type", "e2e", "deploy", "release", "export", "generate", "compile", "bundle", "package",
            "dist", "ci", "format", "migrate", "seed", "codegen", "prebuild", "android", "ios",
        };
        string[] servers = { "dev", "start", "serve", "watch" };
        if (!finite.Any(w => s.Contains(w, StringComparison.Ordinal)) || servers.Any(w => s.Contains(w, StringComparison.Ordinal)))
        {
            return null;
        }
        return new DevMatch(tool, $"run {name}", Symbol(name));
    }

    private static DevMatch? Ruby(List<string> a)
    {
        var script = a.FirstOrDefault(x => !x.StartsWith('-'));
        if (script is null)
        {
            return null;
        }
        var rest = a.Skip(a.IndexOf(script) + 1).Where(x => !x.StartsWith('-')).ToList();
        switch (Base(script))
        {
            case "pod":
                return Classify("pod", Prepend("pod", rest));
            case "fastlane":
                return Classify("fastlane", Prepend("fastlane", rest));
            case "brew":
                return Classify("brew", Prepend("brew", rest));
            case "bundle":
                return rest.Count == 0 || rest[0] == "install" ? new DevMatch("Bundler", "install", Symbol("install")) : null;
            case "rspec":
                return new DevMatch("RSpec", "test", Symbol("test"));
            default:
                return null;
        }
    }

    private static List<string> Prepend(string head, List<string> rest)
    {
        var list = new List<string> { head };
        list.AddRange(rest);
        return list;
    }

    private static DevMatch? Python(List<string> a)
    {
        string? tool = null;
        var rest = new List<string>();
        var m = a.IndexOf("-m");
        if (m >= 0 && m + 1 < a.Count)
        {
            tool = a[m + 1];
            rest = a.Skip(m + 2).Where(x => !x.StartsWith('-')).ToList();
        }
        else
        {
            var script = a.FirstOrDefault(x => !x.StartsWith('-'));
            if (script is not null)
            {
                tool = Base(script);
                rest = a.Skip(a.IndexOf(script) + 1).Where(x => !x.StartsWith('-')).ToList();
            }
        }
        var v = At(rest, 0) ?? string.Empty;
        switch (tool ?? string.Empty)
        {
            case "pip":
            case "pip3":
                return new[] { "install", "download", "wheel" }.Contains(v) ? new DevMatch("pip", v, Symbol(v)) : null;
            case "poetry":
                return new[] { "install", "update", "add", "lock", "build" }.Contains(v) ? new DevMatch("Poetry", v, Symbol(v)) : null;
            case "conda":
            case "mamba":
                return new[] { "install", "create", "update", "env" }.Contains(v) ? new DevMatch("Conda", v, Symbol(v)) : null;
            case "pytest":
            case "py.test":
                return new DevMatch("pytest", "test", Symbol("test"));
            case "manage":
                return v == "test" ? new DevMatch("Django", "test", Symbol("test")) : null;
            case "build":
                return new DevMatch("python", "build", "hammer.fill");
            case "hf":
            case "huggingface-cli":
                return new[] { "download", "upload" }.Contains(v) ? new DevMatch("Hugging Face", v, Symbol(v)) : null;
            case "tox":
            case "nox":
                return new DevMatch(tool!, "test", Symbol("test"));
            default:
                return null;
        }
    }
}
