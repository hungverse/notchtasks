import AppKit
import Foundation

// MARK: - GitHub Actions: runs triggered by a git push from this Mac, plus self-hosted runner jobs

struct GHRun: Equatable {
    let id: Int
    let repo: String       // owner/name
    let workflow: String
    let title: String      // commit message / PR title
    let status: String     // queued, in_progress, completed…
    let conclusion: String?
    let url: URL?
    let started: Date
    var step: String?      // the step running now
    var progress: Double?  // finished steps / all steps
}

enum GitHub {
    /// "git@github.com:o/r.git" / "https://github.com/o/r" → "o/r"
    static func repo(fromRemote url: String) -> String? {
        guard let r = url.range(of: "github.com[:/]", options: .regularExpression) else { return nil }
        var path = String(url[r.upperBound...])
        if path.hasSuffix(".git") { path.removeLast(4) }
        let parts = path.split(separator: "/")
        return parts.count >= 2 ? "\(parts[0])/\(parts[1])" : nil
    }

    /// Repo (from origin) and current branch of the checkout containing `dir`.
    static func checkout(_ dir: String) -> (repo: String, branch: String)? {
        var url = URL(fileURLWithPath: dir)
        while url.pathComponents.count > 1 {
            let git = url.appendingPathComponent(".git")
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: git.path, isDirectory: &isDir) {
                // shortcut: worktrees (.git is a file) are skipped, upgrade by following its gitdir if needed
                guard isDir.boolValue, let config = try? String(contentsOf: git.appendingPathComponent("config"), encoding: .utf8),
                      let head = try? String(contentsOf: git.appendingPathComponent("HEAD"), encoding: .utf8) else { return nil }
                return parse(config: config, head: head)
            }
            url.deleteLastPathComponent()
        }
        return nil
    }

    static func parse(config: String, head: String) -> (repo: String, branch: String)? {
        var inOrigin = false, remote: String?
        for line in config.split(separator: "\n").map({ $0.trimmingCharacters(in: .whitespaces) }) {
            if line.hasPrefix("[") { inOrigin = line == #"[remote "origin"]"# }
            else if inOrigin, line.hasPrefix("url"), let eq = line.firstIndex(of: "=") {
                remote = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            }
        }
        guard let repo = remote.flatMap(repo(fromRemote:)), head.hasPrefix("ref: refs/heads/") else { return nil }
        return (repo, head.dropFirst("ref: refs/heads/".count).trimmingCharacters(in: .whitespacesAndNewlines))
    }

    /// GitHub REST through `gh` (the user's login, private repos work); without gh, public repos via the open API.
    static func get(_ path: String) -> Any? {
        for gh in ["/opt/homebrew/bin/gh", "/usr/local/bin/gh"] where FileManager.default.isExecutableFile(atPath: gh) {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: gh)
            p.arguments = ["api", path]
            let out = Pipe()
            p.standardOutput = out
            p.standardError = FileHandle.nullDevice
            guard (try? p.run()) != nil else { break }
            let data = out.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            if p.terminationStatus == 0 { return try? JSONSerialization.jsonObject(with: data) }
            break
        }
        guard let url = URL(string: "https://api.github.com/" + path) else { return nil }
        var req = URLRequest(url: url, timeoutInterval: 10)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let sem = DispatchSemaphore(value: 0)
        nonisolated(unsafe) var result: Any?
        URLSession.shared.dataTask(with: req) { data, resp, _ in
            if (resp as? HTTPURLResponse)?.statusCode == 200, let data { result = try? JSONSerialization.jsonObject(with: data) }
            sem.signal()
        }.resume()
        sem.wait()
        return result
    }

    static let iso = ISO8601DateFormatter()

    static func runs(_ json: Any?) -> [GHRun] {
        guard let list = (json as? [String: Any])?["workflow_runs"] as? [[String: Any]] else { return [] }
        return list.compactMap { r in
            guard let id = r["id"] as? Int, let repo = (r["repository"] as? [String: Any])?["full_name"] as? String else { return nil }
            let started = (r["run_started_at"] as? String ?? r["created_at"] as? String).flatMap { iso.date(from: $0) } ?? .distantPast
            return GHRun(id: id, repo: repo, workflow: r["name"] as? String ?? "workflow", title: r["display_title"] as? String ?? "",
                         status: r["status"] as? String ?? "", conclusion: r["conclusion"] as? String,
                         url: (r["html_url"] as? String).flatMap(URL.init(string:)), started: started)
        }
    }

    /// Current step and step-based progress over all jobs of a run.
    static func steps(_ json: Any?) -> (step: String?, progress: Double?) {
        guard let jobs = (json as? [String: Any])?["jobs"] as? [[String: Any]] else { return (nil, nil) }
        let steps = jobs.flatMap { $0["steps"] as? [[String: Any]] ?? [] }
        let job = jobs.first { $0["status"] as? String == "in_progress" }?["name"] as? String
        guard !steps.isEmpty else { return (job, nil) }
        let done = steps.filter { $0["status"] as? String == "completed" }.count
        let now = steps.first { $0["status"] as? String == "in_progress" }?["name"] as? String
        let label = [job, now].compactMap { $0 }.joined(separator: " › ")
        return (label.isEmpty ? nil : label, Double(done) / Double(steps.count))
    }
}

/// Watches each pushed branch for up to 1 h: runs, live step, pass/fail.
final class GitHubWatcher {
    private var watched: [String: (branch: String, since: Date)] = [:]  // repo → push
    private(set) var runs: [Int: GHRun] = [:]
    private var busy = false

    func pushed(from dir: String, at date: Date = Date()) {
        guard let c = GitHub.checkout(dir) else { return }
        watched[c.repo] = (c.branch, date)
        NSLog("NotchTasks: watching GitHub Actions for %@ @ %@", c.repo, c.branch)
    }

    /// Polls off the main thread; `done` gets runs that just finished.
    func poll(done: @escaping @MainActor ([GHRun]) -> Void) {
        guard !busy, !watched.isEmpty else { return }
        busy = true
        let targets = watched
        let previous = runs
        Task.detached {
            var next: [Int: GHRun] = [:]
            for (repo, w) in targets {
                let branch = w.branch.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? w.branch
                for var r in GitHub.runs(GitHub.get("repos/\(repo)/actions/runs?branch=\(branch)&per_page=10"))
                where r.started >= w.since.addingTimeInterval(-90) {
                    if r.status != "completed" { (r.step, r.progress) = GitHub.steps(GitHub.get("repos/\(repo)/actions/runs/\(r.id)/jobs")) }
                    next[r.id] = r
                }
            }
            let found = next
            let finished = found.values.filter { $0.status == "completed" && previous[$0.id].map { $0.status != "completed" } ?? false }
            await MainActor.run {
                self.busy = false
                self.runs = found
                // stop watching a repo once its runs are all done, or after an hour
                for (repo, w) in targets {
                    let mine = found.values.filter { $0.repo.caseInsensitiveCompare(repo) == .orderedSame }
                    let settled = !mine.isEmpty && mine.allSatisfy { $0.status == "completed" } && Date().timeIntervalSince(w.since) > 120
                    if settled || Date().timeIntervalSince(w.since) > 3600 { self.watched[repo] = nil }
                }
                if !finished.isEmpty { done(finished) }
            }
        }
    }

    var jobs: [Job] {
        runs.values.filter { $0.status != "completed" }.sorted { $0.id < $1.id }.map { r in
            let waiting = ["queued", "waiting", "pending", "requested"].contains(r.status)
            return Job(id: "gh:\(r.id)", title: "\(r.repo) · \(r.workflow)",
                       detail: waiting ? L("Waiting for a runner") : "\(r.step ?? L("running")) · \(mmss(Date().timeIntervalSince(r.started)))",
                       working: true, url: r.url, progress: r.progress, tool: "GitHub Actions")
        }
    }
}

/// Self-hosted GitHub runner on this Mac: one Runner.Worker per running job.
/// shortcut: job name/result come from the runner's _diag/Worker_*.log text; upgrade if the runner changes its log lines.
enum GHSelfHosted {
    static func job(worker pid: pid_t, kids: [pid_t: [PInfo]], log url: URL?) -> Job {
        // the job's steps run inside _work/<repo>/<repo>
        var repo = "GitHub runner", stack = [pid]
        while let p = stack.popLast() {
            if let cwd = Proc.cwd(p), let r = cwd.range(of: "/_work/") {
                repo = String(cwd[r.upperBound...].split(separator: "/").first ?? "")
                break
            }
            stack += (kids[p] ?? []).map(\.pid)
        }
        let name = url.map { readTail($0, 2_000_000) }.flatMap { text in
            text.range(of: #""jobDisplayName": ""#, options: .backwards).map { r in String(text[r.upperBound...].prefix { $0 != "\"" }) }
        } ?? "job"
        return Job(id: "ghw:\(pid)", title: repo, detail: name, working: true, symbol: "server.rack", tool: "GitHub Actions")
    }

    /// Newest worker log of the runner this process belongs to (…/bin/Runner.Worker → …/_diag).
    static func logURL(_ pid: pid_t) -> URL? {
        var buf = [CChar](repeating: 0, count: 4096)
        guard proc_pidpath(pid, &buf, 4096) > 0 else { return nil }
        let diag = URL(fileURLWithPath: String(cString: buf)).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("_diag")
        let logs = (try? FileManager.default.contentsOfDirectory(at: diag, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        func mtime(_ u: URL) -> Date { (try? u.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast }
        return logs.filter { $0.lastPathComponent.hasPrefix("Worker_") }.max { mtime($0) < mtime($1) }
    }

    static func result(_ log: String?) -> String? {
        guard let log, let r = log.range(of: "Job result after all job steps finish: ", options: .backwards) else { return nil }
        return String(log[r.upperBound...].prefix { $0.isLetter })
    }
}
