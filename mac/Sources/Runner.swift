import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - GitLab Runner: job results from the runner's own log

struct RunnerJob: Equatable {
    let id: String
    let name: String
    let project: String
    let url: URL?
}

struct JobAlert: Identifiable, Equatable {
    let id: String   // GitLab job id
    let name: String
    let project: String
    let reason: String
    let url: URL?    // the job page = its terminal log
}

enum RunnerLog {
    static let home = FileManager.default.homeDirectoryForCurrentUser

    /// Where the launchd service sends gitlab-runner's output ("Job failed" / "Job succeeded" lines).
    static func files() -> [URL] {
        let agents = home.appendingPathComponent("Library/LaunchAgents")
        return ["gitlab-runner.plist", "homebrew.mxcl.gitlab-runner.plist"].flatMap { name -> [URL] in
            guard let plist = NSDictionary(contentsOf: agents.appendingPathComponent(name)) else { return [] }
            return ["StandardErrorPath", "StandardOutPath"].compactMap { (plist[$0] as? String).map(URL.init(fileURLWithPath:)) }
        }
    }

    static let server: String = {
        let cfg = (try? String(contentsOf: home.appendingPathComponent(".gitlab-runner/config.toml"), encoding: .utf8)) ?? ""
        let m = cfg.firstMatch(of: try! Regex(#"url\s*=\s*"([^"]+)""#))
        return (m?.output[1].substring.map(String.init) ?? "https://gitlab.com").trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }()

    static let field = try! Regex(#"([\w-]+)=("([^"]*)"|\S+)"#)

    static func jobURL(_ project: String?, _ job: String) -> URL? {
        project.flatMap { URL(string: "\(server)/\($0)/-/jobs/\(job)") }
    }

    /// Running = "received" with no "Job succeeded/failed" after it; failed = "Job failed" lines.
    static func parse(_ text: String) -> (running: [RunnerJob], failed: [JobAlert], succeeded: Set<String>) {
        var running: [RunnerJob] = []
        var failed: [JobAlert] = []
        var succeededIDs: Set<String> = []
        for raw in text.split(separator: "\n") {
            let received = raw.contains("received"), succeeded = raw.contains("Job succeeded"), fail = raw.contains("Job failed")
            guard received || succeeded || fail else { continue }
            let line = String(raw).replacingOccurrences(of: "\u{1B}\\[[0-9;]*m", with: "", options: .regularExpression)
            var f: [String: String] = [:]
            for m in line.matches(of: field) {
                if let k = m.output[1].substring, let v = m.output[3].substring ?? m.output[2].substring { f[String(k)] = String(v) }
            }
            guard let job = f["job"] else { continue }
            if received {
                // repo_url "https://gitlab.com/group/project.git" → "group/project"
                let project = f["repo_url"].flatMap { URL(string: $0)?.path }
                    .map { String($0.dropFirst()).replacingOccurrences(of: ".git", with: "") } ?? f["project_full_path"]
                running.append(RunnerJob(id: job, name: f["job_name"] ?? "job \(job)", project: project ?? "?", url: jobURL(project, job)))
                continue
            }
            running.removeAll { $0.id == job }
            if succeeded { succeededIDs.insert(job) }
            if fail {
                let project = f["project_full_path"]
                failed.append(JobAlert(id: job, name: f["job_name"] ?? "job \(job)", project: project ?? "?",
                                       reason: [f["failure_reason"], f["exit_code"].map { "exit \($0)" }].compactMap { $0 }.joined(separator: " · "),
                                       url: jobURL(project, job)))
            }
        }
        return (running, failed, succeededIDs)
    }
}
