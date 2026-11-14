import AppKit

// MARK: - Answering coding agents from the notch
//
// Each agent runs `NotchTasks --hook <agent>` from its own hook system whenever it is about to ask (Claude Code and
// Codex: PermissionRequest; Gemini CLI: BeforeTool; Antigravity: PreToolUse). The hook writes the ask to
// Asks/<id>.json and waits; the notch shows it, and the answer comes back as Asks/<id>.reply. No reply (app closed,
// "Trả lời trong <agent>", or the wait runs out) = the agent asks as usual.

enum AgentKind: String, CaseIterable, Codable, Identifiable {
    case claude, codex, gemini, antigravity, cursor, opencode
    var id: String { rawValue }

    var name: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .gemini: return "Gemini"
        case .antigravity: return "Antigravity"
        case .cursor: return "Cursor"
        case .opencode: return "OpenCode"
        }
    }
    var cli: String {
        switch self {
        case .antigravity: return "agy"
        case .cursor: return "cursor-agent"
        default: return rawValue
        }
    }
    var event: String {
        switch self {
        case .claude, .codex: return "PermissionRequest"
        case .gemini: return "BeforeTool"
        case .antigravity: return "PreToolUse"
        case .cursor: return "beforeShellExecution"
        case .opencode: return "permission.ask"
        }
    }
    /// The agents that call the hook before every tool, asked or not: only the ones that change things, or ask you.
    var matcher: String {
        switch self {
        case .claude, .codex, .cursor, .opencode: return "*"
        case .gemini: return "run_shell_command|write_file|replace|web_fetch|ask_user"
        case .antigravity: return "run_command|write_to_file|replace_file_content|multi_replace_file_content|ask_question"
        }
    }
    var settingsFile: URL {
        let home = FileManager.default.homeDirectoryForCurrentUser
        switch self {
        case .claude: return claudeDir.appendingPathComponent("settings.json")
        case .codex: return home.appendingPathComponent(".codex/hooks.json")
        case .gemini: return home.appendingPathComponent(".gemini/settings.json")
        case .antigravity: return home.appendingPathComponent(".gemini/config/hooks.json")
        case .cursor: return home.appendingPathComponent(".cursor/hooks.json")
        // OpenCode has no hook config: a small plugin of ours asks the notch
        case .opencode: return home.appendingPathComponent(".config/opencode/plugins/notchtasks.js")
        }
    }
    /// Tried end to end on a real Mac; the others follow their published hook format.
    var tested: Bool { [.claude, .codex, .antigravity, .opencode, .cursor].contains(self) }
    /// Installed on this Mac: its command line tool is on one of the usual paths.
    var present: Bool {
        if self == .claude { return true }  // the desktop app reads ~/.claude too, CLI or not
        return AgentAsks.cli(cli) != nil
    }

    /// The watch list row for this agent, so the notch shows the icon you gave it (else the preset's).
    func item(in items: [WatchItem]) -> WatchItem? {
        guard let preset = presetGroups[0].items.first(where: { $0.name.hasPrefix(name) || $0.cli == cli }) else { return nil }
        return items.first { $0.kind == preset.kind && $0.process == preset.process && $0.args == preset.args } ?? preset
    }
}

/// What an agent is waiting on.
struct AgentAsk: Codable, Identifiable, Equatable {
    struct Question: Codable, Equatable {
        var question: String
        var header: String?
        var options: [String]
        var multiSelect: Bool
    }
    var id: String
    var pid: Int32          // the waiting hook: gone means it was answered in the agent, or cancelled
    var session: String     // the session's name, else its folder
    var tool: String
    var summary: String     // the command, file or address it wants, or the first question
    var questions: [Question]
    var at: Date
    var agent: AgentKind?   // nil: written before other agents were supported, so Claude
    var icon: Badge? = nil  // set for the phone: the notch's icon for this agent's app
    var isQuestion: Bool { !questions.isEmpty }
    var kind: AgentKind { agent ?? .claude }

    /// What the tool does, in words a non-developer reads.
    var action: String {
        switch tool {
        case "Bash", "run_shell_command", "run_command", "shell", "exec_command", "local_shell", "Execute", "bash": return L("Run command")
        case "Edit", "Write", "MultiEdit", "NotebookEdit", "write_file", "replace", "write_to_file", "replace_file_content",
             "multi_replace_file_content", "apply_patch", "Create", "ApplyPatch", "edit", "write": return L("Edit file")
        case "Read", "read_file", "view_file": return L("Read file")
        case "WebFetch", "WebSearch", "web_fetch", "FetchUrl", "webfetch": return L("Web access")
        default: return tool.hasPrefix("mcp__") ? L("Use tool ") + (tool.split(separator: "_").last.map(String.init) ?? "") : tool
        }
    }
}

enum AskReply: Codable, Equatable {
    case allow, deny, pass
    case answers([String: String])  // question → chosen label(s), joined with ", " for multi-select
}

enum AgentAsks {
    static let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/NotchTasks/Asks")
    static let notice = Notification.Name("local.notchtasks.ask")
    /// Agents give up on a hook after its timeout (set to this + 10 s when installed), then ask as usual.
    static let wait: TimeInterval = 590
    static let denied = "The user denied this from the NotchTasks notch."

    /// What an agent reads when its question was answered on the notch instead of in its own dialog.
    static func answered(_ a: [String: String]) -> String {
        "The user already answered this on the NotchTasks notch, so the dialog was not shown. Their answers: "
            + a.map { "\"\($0.key)\" → \"\($0.value)\"" }.sorted().joined(separator: "; ")
            + ". Continue with these answers; do not ask again."
    }

    static func file(_ id: String) -> URL { dir.appendingPathComponent("\(id).json") }
    static func replyFile(_ id: String) -> URL { dir.appendingPathComponent("\(id).reply") }

    // MARK: The hook side (`NotchTasks --hook <agent>`, run by the agent)

    static func ask(from input: [String: Any], kind: AgentKind, id: String, pid: Int32, session: String) -> AgentAsk {
        // Antigravity speaks camelCase: {"toolCall": {"name", "args"}}; the others {"tool_name", "tool_input"}
        // Cursor sends the shell command itself at the top level
        let call = input["toolCall"] as? [String: Any]
        // OpenCode sends its permission evaluation: {action: "bash"|"edit"|"webfetch", resources, metadata: {command…}}
        let tool = input["tool_name"] as? String ?? call?["name"] as? String
            ?? (kind == .cursor ? "shell" : kind == .opencode ? input["action"] as? String ?? input["type"] as? String ?? "?" : "?")
        var ti = input["tool_input"] as? [String: Any] ?? call?["args"] as? [String: Any] ?? (kind == .cursor ? input : [:])
        if kind == .opencode {
            ti = input["metadata"] as? [String: Any] ?? [:]
            if ti["command"] == nil, let p = (input["resources"] as? [String])?.joined(separator: " ") ?? input["pattern"] as? String ?? input["title"] as? String {
                ti["description"] = p
            }
        }
        // options come as [{label}] (Claude, Gemini) or plain strings (Antigravity, Codex app)
        let qs = (ti["questions"] as? [[String: Any]] ?? []).map { q in
            AgentAsk.Question(question: q["question"] as? String ?? q["title"] as? String ?? "", header: q["header"] as? String,
                              options: q["options"] as? [String] ?? (q["options"] as? [[String: Any]] ?? []).compactMap { $0["label"] as? String },
                              multiSelect: q["multiSelect"] as? Bool ?? q["is_multi_select"] as? Bool ?? false)
        }
        let keys = ["command", "CommandLine", "file_path", "TargetFile", "AbsolutePath", "url", "Url", "path", "pattern", "query", "description"]
        let field = keys.lazy.compactMap { k -> String? in
            if let s = ti[k] as? String { return s }
            return (ti[k] as? [String]).map { $0.joined(separator: " ") }  // Codex: ["bash", "-lc", "…"]
        }.first
        let compact = (try? JSONSerialization.data(withJSONObject: ti, options: [.withoutEscapingSlashes])).map { String(decoding: $0, as: UTF8.self) }
        let summary = qs.first?.question ?? field ?? compact ?? ""
        return AgentAsk(id: id, pid: pid, session: session, tool: tool, summary: String(summary.prefix(600)), questions: qs, at: Date(), agent: kind)
    }

    /// The hook's stdout for a reply; nil = print nothing, so the agent asks as usual.
    static func output(_ reply: AskReply, input: [String: Any], kind: AgentKind) -> String? {
        let out: [String: Any]
        switch kind {
        case .claude, .codex:
            let decision: [String: Any]
            switch reply {
            case .pass: return nil
            case .allow: decision = ["behavior": "allow"]
            case .deny: decision = ["behavior": "deny", "message": denied]
            case .answers(let a):
                var ti = input["tool_input"] as? [String: Any] ?? [:]
                ti["answers"] = a
                decision = ["behavior": "allow", "updatedInput": ti]
            }
            out = ["hookSpecificOutput": ["hookEventName": "PermissionRequest", "decision": decision]]
        case .gemini, .antigravity:
            switch reply {
            // their question tools take no answer from a hook: block the dialog and hand the answer over in its place
            case .answers(let a): out = ["decision": "deny", "reason": answered(a)]
            case .allow:
                // Antigravity's own permission check still runs after an allow: grant this one call
                let args = (input["toolCall"] as? [String: Any])?["args"] as? [String: Any] ?? [:]
                let grant = (args["CommandLine"] as? String).map { "command(\($0))" } ?? (args["TargetFile"] as? String).map { "write_file(\($0))" }
                out = kind == .antigravity && grant != nil ? ["decision": "allow", "permissionOverrides": [grant!]] : ["decision": "allow"]
            case .deny: out = ["decision": "deny", "reason": denied]
            // Gemini: no output = carry on and ask as usual; Antigravity needs to be told
            case .pass: if kind == .gemini { return nil } else { out = ["decision": "ask"] }
            }
        case .cursor:
            switch reply {
            case .allow, .answers: out = ["permission": "allow"]
            case .deny: out = ["permission": "deny", "user_message": L("Denied on the notch"), "agent_message": denied]
            case .pass: out = ["permission": "ask"]
            }
        case .opencode:
            switch reply {
            case .allow, .answers: out = ["status": "allow"]
            case .deny: out = ["status": "deny"]
            case .pass: out = ["status": "ask"]
            }
        }
        return (try? JSONSerialization.data(withJSONObject: out, options: [.withoutEscapingSlashes])).map { String(decoding: $0, as: UTF8.self) }
    }

    /// The last turn's settings in a Codex session file. Quotes inside recorded output are escaped, so only real entries match.
    static func codexTurn(_ transcript: URL) -> [String: Any]? {
        guard let d = try? Data(contentsOf: transcript, options: .mappedIfSafe),
              let hit = d.range(of: Data(#""type":"turn_context""#.utf8), options: .backwards) else { return nil }
        let start = d[..<hit.lowerBound].lastIndex(of: 10).map { $0 + 1 } ?? d.startIndex
        let end = d[hit.upperBound...].firstIndex(of: 10) ?? d.endIndex
        return ((try? JSONSerialization.jsonObject(with: d[start..<end])) as? [String: Any])?["payload"] as? [String: Any]
    }

    /// How the agent is set to approve: the hook runs ahead of it, and answering here would replace its own approval.
    static func approvalMode(_ kind: AgentKind, input: [String: Any], antigravityConfig: URL) -> String? {
        switch kind {
        case .codex:
            if let m = input["permission_mode"] as? String, m != "default" { return m }
            return (input["transcript_path"] as? String).flatMap { codexTurn(URL(fileURLWithPath: $0))?["approvals_reviewer"] as? String }
        case .antigravity:
            let root = (try? Data(contentsOf: antigravityConfig)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
            return (root?["userSettings"] as? [String: Any])?["permissionPreset"] as? String
        default: return nil
        }
    }

    /// Commands and edits are the agent's to approve in these modes; a question still needs you.
    static func selfApproved(mode: String?, tool: String) -> Bool {
        ["auto_review", "bypassPermissions", "dontAsk", "AGENT_PERMISSION_PRESET_TURBO"].contains(mode ?? "")
            && !["request_user_input", "ask_question"].contains(tool)
    }

    static var appRunning: Bool {
        NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "com.notchtasks.mac")
            .contains { $0.processIdentifier != getpid() }
    }

    static func runHook() -> Never {
        let args = CommandLine.arguments
        let kind = args.firstIndex(of: "--hook").flatMap { $0 + 1 < args.count ? AgentKind(rawValue: args[$0 + 1]) : nil } ?? .claude
        let data = FileHandle.standardInput.readDataToEndOfFile()
        let pass = { (input: [String: Any]) -> Never in
            if let o = output(.pass, input: input, kind: kind) { print(o) }
            exit(0)
        }
        guard let input = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { pass([:]) }
        if let e = input["hook_event_name"] as? String, e != kind.event { pass(input) }
        let antigravityConfig = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini/config/config.json")
        let tool = input["tool_name"] as? String ?? (input["toolCall"] as? [String: Any])?["name"] as? String ?? ""
        if selfApproved(mode: approvalMode(kind, input: input, antigravityConfig: antigravityConfig), tool: tool) {
            // no objection: Antigravity's own permission check still runs, and "ask" would force its dialog
            if kind == .antigravity { print(#"{"decision":"allow"}"#) }
            exit(0)
        }
        guard appRunning else { pass(input) }
        let sid = input["session_id"] as? String
        let s = kind == .claude ? ClaudeSession.all().first { $0.sessionId != nil && $0.sessionId == sid } : nil
        // Cursor may send an empty cwd: then the folder comes from the process that started the hook
        let cwd = (input["cwd"] as? String ?? (input["workspacePaths"] as? [String])?.first).flatMap { $0.isEmpty ? nil : ($0 as NSString).lastPathComponent }
        let id = UUID().uuidString
        // no folder in the input (OpenCode): the folder the agent runs in, from the process that started the hook
        let a = ask(from: input, kind: kind, id: id, pid: getpid(), session: s?.name ?? cwd ?? Proc.folder(getppid()) ?? kind.name)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        guard let json = try? JSONEncoder().encode(a), (try? json.write(to: file(id), options: .atomic)) != nil else { pass(input) }
        DistributedNotificationCenter.default().postNotificationName(notice, object: nil, userInfo: nil, deliverImmediately: true)

        var reply: AskReply?
        let end = Date().addingTimeInterval(wait)
        var checks = 0
        // answered in the agent itself: it moves on without stopping this hook, so watch for that and leave at once
        // (else the ask stays on the notch and the phone until the wait runs out)
        let transcript = (input["transcript_path"] as? String).map { URL(fileURLWithPath: $0) }
        let toolInput = input["tool_input"] as? [String: Any]
        var call: String?  // the id the agent gave this tool call in its log, once found there
        let agent = getppid()
        while reply == nil, Date() < end {
            usleep(200_000)
            reply = (try? Data(contentsOf: replyFile(id))).flatMap { try? JSONDecoder().decode(AskReply.self, from: $0) }
            checks += 1
            if checks % 10 == 0, !appRunning { break }  // the app quit: let the agent ask itself
            if checks % 5 == 0 {
                if getppid() != agent { break }  // the agent is gone (quit, or the ask was cancelled with it)
                if let transcript {
                    let tail = self.tail(transcript)
                    call = call ?? pendingCall(kind, tool: tool, input: toolInput, in: tail)
                    if let call, settled(kind, call: call, in: tail) { break }
                }
            }
        }
        try? FileManager.default.removeItem(at: file(id))
        try? FileManager.default.removeItem(at: replyFile(id))
        if let o = output(reply ?? .pass, input: input, kind: kind) { print(o) }
        exit(0)
    }

    /// The end of the agent's session log (JSON lines), at most the last 256 KB.
    static func tail(_ url: URL) -> [[String: Any]] {
        guard let h = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? h.close() }
        let size = (try? h.seekToEnd()) ?? 0
        try? h.seek(toOffset: size > 262_144 ? size - 262_144 : 0)
        return h.readDataToEndOfFile().split(separator: UInt8(ascii: "\n")).compactMap { try? JSONSerialization.jsonObject(with: Data($0)) as? [String: Any] }
    }

    /// The id of the tool call this ask is about: the newest call of that tool with that input. Claude sends no id, only the
    /// tool's name and input; Codex logs calls as function_call with a call_id. Nil: not found (yet), so no early leave.
    static func pendingCall(_ kind: AgentKind, tool: String, input: [String: Any]?, in lines: [[String: Any]]) -> String? {
        switch kind {
        case .claude:
            let uses = lines.flatMap { (($0["message"] as? [String: Any])?["content"] as? [[String: Any]]) ?? [] }
                .filter { $0["type"] as? String == "tool_use" && $0["name"] as? String == tool }
            let same = uses.filter { u in input.map { NSDictionary(dictionary: (u["input"] as? [String: Any]) ?? [:]).isEqual(to: $0) } ?? true }
            return (same.last ?? (input == nil ? uses.last : nil))?["id"] as? String
        case .codex:
            return lines.compactMap { $0["payload"] as? [String: Any] }.filter { $0["type"] as? String == "function_call" }.last?["call_id"] as? String
        default:
            // Gemini, Antigravity rewrite their whole session file, Cursor and OpenCode send none: only the agent quitting
            // ends their wait early
            return nil
        }
    }

    /// That call has its result in the log: it ran (allowed in the agent) or was refused there, so the ask is settled.
    static func settled(_ kind: AgentKind, call: String, in lines: [[String: Any]]) -> Bool {
        switch kind {
        case .claude:
            return lines.contains { line in
                ((line["message"] as? [String: Any])?["content"] as? [[String: Any]])?.contains { $0["type"] as? String == "tool_result" && $0["tool_use_id"] as? String == call } == true
            }
        case .codex:
            return lines.contains { ($0["payload"] as? [String: Any]).map { $0["type"] as? String == "function_call_output" && $0["call_id"] as? String == call } == true }
        default:
            return false
        }
    }

    // MARK: The app side

    /// Asks still waiting, oldest first. Leftovers of hooks that died are cleared.
    static func pending() -> [AgentAsk] {
        let fm = FileManager.default
        let files = (try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.filter { $0.pathExtension == "json" }.compactMap { url -> AgentAsk? in
            guard let a = (try? Data(contentsOf: url)).flatMap({ try? JSONDecoder().decode(AgentAsk.self, from: $0) }) else { return nil }
            if kill(a.pid, 0) != 0 && errno == ESRCH {
                try? fm.removeItem(at: url)
                try? fm.removeItem(at: replyFile(a.id))
                return nil
            }
            return fm.fileExists(atPath: replyFile(a.id).path) ? nil : a  // answered, the hook hasn't picked it up yet
        }
        .sorted { $0.at < $1.at }
    }

    static func send(_ reply: AskReply, to a: AgentAsk) {
        guard let d = try? JSONEncoder().encode(reply) else { return }
        try? d.write(to: replyFile(a.id), options: .atomic)
    }

    // MARK: Installing the hook in each agent's config

    static let hookName = "notchtasks"  // Antigravity keys hooks by name

    /// OpenCode runs plugins in its own process: this one hands each permission prompt to `NotchTasks --hook opencode`
    /// without blocking OpenCode, and keeps OpenCode's own prompt when the notch has no answer.
    static var openCodePlugin: String {
        """
        // Written by NotchTasks: answers OpenCode's permission prompts from the notch. Turn it off in NotchTasks.
        import { spawn } from "node:child_process"

        const ask = (request) => new Promise((resolve) => {
          const p = spawn(\(String(reflecting: Bundle.main.executablePath ?? "/Applications/NotchTasks.app/Contents/MacOS/NotchTasks")), ["--hook", "opencode"], { stdio: ["pipe", "pipe", "ignore"] })
          let out = ""
          p.stdout.on("data", (d) => (out += d))
          p.on("close", () => resolve(out))
          p.on("error", () => resolve(""))
          p.stdin.end(JSON.stringify(request))
        })

        export default {
          id: "notchtasks",
          async setup(ctx) {
            await ctx.permission.hook("evaluate", async (event) => {
              if (event.effect !== "ask") return  // already allowed by your rules: nothing to ask
              const reply = await ask({ action: event.action, resources: event.resources, metadata: event.metadata, sessionID: event.sessionID, agent: event.agent })
              try {
                const status = JSON.parse(reply).status
                if (status === "allow" || status === "deny") event.effect = status
                if (status === "deny") event.message = "The user denied this from the NotchTasks notch."
              } catch {}
            })
          },
        }

        """
    }

    static func command(_ kind: AgentKind) -> String {
        "'\(Bundle.main.executablePath ?? "/Applications/NotchTasks.app/Contents/MacOS/NotchTasks")' --hook \(kind.rawValue)"
    }

    /// A matcher group holding our command, or (Cursor's flat list) our command itself.
    static func isOurs(_ group: [String: Any]) -> Bool {
        ((group["hooks"] as? [[String: Any]] ?? []) + [group]).contains {
            guard let c = $0["command"] as? String else { return false }
            return c.contains("NotchTasks") && c.contains("--hook")
        }
    }

    static func installed(_ kind: AgentKind, in file: URL? = nil) -> Bool {
        if kind == .opencode {
            return ((try? String(contentsOf: file ?? kind.settingsFile, encoding: .utf8)) ?? "").contains("--hook")
        }
        guard let d = try? Data(contentsOf: file ?? kind.settingsFile),
              let root = (try? JSONSerialization.jsonObject(with: d)) as? [String: Any] else { return false }
        if kind == .antigravity { return root[hookName] != nil }
        return ((root["hooks"] as? [String: Any])?[kind.event] as? [[String: Any]] ?? []).contains(where: isOurs)
    }

    /// Adds or removes only our hook; everything else in the file is kept. The old file is saved beside it first.
    static func setInstalled(_ on: Bool, _ kind: AgentKind, in file: URL? = nil) throws {
        let fm = FileManager.default
        let url = file ?? kind.settingsFile
        if kind == .opencode {
            if on {
                try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                if fm.fileExists(atPath: url.path) {
                    let backup = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".notchtasks-backup")
                    try? fm.removeItem(at: backup)
                    try fm.copyItem(at: url, to: backup)
                }
                try openCodePlugin.write(to: url, atomically: true, encoding: .utf8)
            } else if installed(.opencode, in: url) {
                try fm.removeItem(at: url)  // only ever our own file
            }
            return
        }
        var root: [String: Any] = [:]
        if let d = try? Data(contentsOf: url), !d.isEmpty {
            // a file we can't read is never overwritten
            guard let r = try JSONSerialization.jsonObject(with: d) as? [String: Any] else { throw CocoaError(.fileReadCorruptFile) }
            root = r
            let backup = url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".notchtasks-backup")
            try? fm.removeItem(at: backup)
            try fm.copyItem(at: url, to: backup)
        }
        // Gemini counts the timeout in milliseconds, the rest in seconds
        let timeout = kind == .gemini ? (Int(wait) + 10) * 1000 : Int(wait) + 10
        var handler: [String: Any] = ["type": "command", "command": command(kind), "timeout": timeout]
        if kind == .gemini { handler["name"] = hookName }  // Gemini lists hooks by name; others may reject unknown keys
        let group: [String: Any] = ["matcher": kind.matcher, "hooks": [handler]]
        if kind == .antigravity {
            root[hookName] = on ? [kind.event: [group]] : nil
        } else if kind == .cursor {
            var hooks = root["hooks"] as? [String: Any] ?? [:]
            var list = (hooks[kind.event] as? [[String: Any]] ?? []).filter { !isOurs($0) }
            if on { list.append(["command": command(kind)]) }
            hooks[kind.event] = list.isEmpty ? nil : list
            root["hooks"] = hooks
            root["version"] = root["version"] ?? 1
        } else {
            var hooks = root["hooks"] as? [String: Any] ?? [:]
            var groups = (hooks[kind.event] as? [[String: Any]] ?? []).filter { !isOurs($0) }
            if on { groups.append(group) }
            hooks[kind.event] = groups.isEmpty ? nil : groups
            root["hooks"] = hooks.isEmpty ? nil : hooks
        }
        try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let out = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try out.write(to: url, options: .atomic)
    }
}

// MARK: - One click to whatever is left to do

extension AgentKind {
    /// The step still left before the notch can answer this agent: what's missing, the button, and the command
    /// that does it in Terminal. nil = nothing left.
    var setupStep: (missing: String, button: String, command: String)? {
        switch self {
        case .codex where AgentAsks.installed(.codex) && !AgentAsks.codexTrusted:
            return (L("Not trusted in Codex yet"), L("Open Codex"), "codex")
        case .gemini where present && !AgentAsks.geminiSignedIn:
            return (L("Not signed in"), L("Sign in"), "gemini")
        default: return nil
        }
    }
}

extension AgentAsks {
    static let home = FileManager.default.homeDirectoryForCurrentUser

    /// shortcut: Codex records trust as [hooks.state."<source>:permission_request:i:j"] in config.toml; matched as text,
    /// update if Codex changes the key's shape.
    static var codexTrusted: Bool {
        let toml = (try? String(contentsOf: home.appendingPathComponent(".codex/config.toml"), encoding: .utf8)) ?? ""
        return toml.split(separator: "\n").contains { $0.hasPrefix("[hooks.state.") && $0.contains("hooks.json:permission_request") }
    }

    /// Signed in = an auth type picked in settings (or an API key in the environment). Credentials are never read.
    static var geminiSignedIn: Bool {
        if ProcessInfo.processInfo.environment["GEMINI_API_KEY"] != nil { return true }
        let s = (try? String(contentsOf: home.appendingPathComponent(".gemini/settings.json"), encoding: .utf8)) ?? ""
        return s.contains("selectedType")
    }

    static func cli(_ name: String) -> String? {
        ["\(home.path)/.local/bin", "/opt/homebrew/bin", "/usr/local/bin", "\(home.path)/.npm-global/bin", "\(home.path)/.opencode/bin"].map { "\($0)/\(name)" }
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// Sends your answer into a Codex thread as your next message (`codex queue`), off the main thread.
    static func codexReply(thread: String, _ text: String) {
        guard let codex = cli("codex"), !thread.isEmpty, !text.isEmpty else { return }
        DispatchQueue.global().async {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: codex)
            p.arguments = ["queue", "--thread", thread, "--message", text]
            p.standardInput = FileHandle.nullDevice
            let err = Pipe()
            p.standardError = err
            p.standardOutput = err
            do { try p.run(); p.waitUntilExit() } catch { appLog("asks.log", "codex queue failed to start: \(error)"); return }
            let out = String(decoding: err.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            appLog("asks.log", "codex queue \(thread) → exit \(p.terminationStatus) \(out.prefix(300))")
        }
    }

    /// Opens Terminal running `command` in the home folder. A .command file needs no Automation permission.
    static func openInTerminal(_ command: String) {
        let f = dir.deletingLastPathComponent().appendingPathComponent("\(command)-setup.command")
        try? FileManager.default.createDirectory(at: f.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? "#!/bin/zsh -l\ncd ~\n\(command)\n".write(to: f, atomically: true, encoding: .utf8)
        try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: f.path)
        NSWorkspace.shared.open(f)
    }

    /// Brings forward the window the agent runs in (Terminal, iTerm, VS Code, the Claude app…): the first app up the
    /// waiting hook's parent chain.
    @discardableResult static func focusHost(of pid: pid_t) -> Bool {
        let parent = Dictionary(Proc.all().map { ($0.pid, $0.ppid) }, uniquingKeysWith: { a, _ in a })
        var p = pid
        for _ in 0..<40 where p > 1 {
            if let app = NSRunningApplication(processIdentifier: p), app.activationPolicy == .regular {
                return app.activate()
            }
            p = parent[p] ?? 0
        }
        return false
    }
}
