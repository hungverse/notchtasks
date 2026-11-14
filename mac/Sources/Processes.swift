import AppKit
import SwiftUI
import Darwin
import ServiceManagement
import CoreAudio

// MARK: - Whole-machine load, as Activity Monitor counts it

struct SystemLoad: Equatable {
    var cpu: Double     // 0…1, busy share of all cores since the previous sample
    var memory: Double  // 0…1, "Memory Used" (app + wired + compressed) of physical RAM

    /// Cumulative CPU ticks: user, system, idle, nice.
    static func cpuTicks() -> [UInt32]? {
        var info = host_cpu_load_info()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count) }
        }
        guard kr == KERN_SUCCESS else { return nil }
        return [info.cpu_ticks.0, info.cpu_ticks.1, info.cpu_ticks.2, info.cpu_ticks.3]
    }

    static func cpu(from a: [UInt32], to b: [UInt32]) -> Double {
        let d = zip(a, b).map { Double($1 &- $0) }  // &-: the counters wrap
        let total = d.reduce(0, +)
        return total > 0 ? (total - d[2]) / total : 0
    }

    static func memory() -> Double? {
        var vm = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &vm) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count) }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let pages = Double(vm.internal_page_count) - Double(vm.purgeable_count) + Double(vm.wire_count) + Double(vm.compressor_page_count)
        return min(1, max(0, pages * Double(vm_kernel_page_size) / Double(ProcessInfo.processInfo.physicalMemory)))
    }
}

// MARK: - Process sampling (libproc, no subprocesses)

struct PInfo { let pid: pid_t; let ppid: pid_t; let name: String; var start = Date.distantPast }

enum Proc {
    static let timebase: Double = {
        var t = mach_timebase_info()
        mach_timebase_info(&t)
        return Double(t.numer) / Double(t.denom)
    }()

    static func cstr<T>(_ t: T) -> String {
        withUnsafeBytes(of: t) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }

    static func all() -> [PInfo] {
        var pids = [pid_t](repeating: 0, count: Int(proc_listallpids(nil, 0)) + 64)
        let count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        let size = Int32(MemoryLayout<proc_bsdinfo>.size)
        return pids.prefix(Int(max(count, 0))).compactMap { pid in
            var info = proc_bsdinfo()
            guard pid > 0, proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, size) == size else { return nil }
            let name = cstr(info.pbi_name)
            return PInfo(pid: pid, ppid: pid_t(info.pbi_ppid), name: name.isEmpty ? cstr(info.pbi_comm) : name,
                         start: Date(timeIntervalSince1970: Double(info.pbi_start_tvsec) + Double(info.pbi_start_tvusec) / 1e6))
        }
    }

    static func cwd(_ pid: pid_t) -> String? {
        var info = proc_vnodepathinfo()
        let size = Int32(MemoryLayout<proc_vnodepathinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDVNODEPATHINFO, 0, &info, size) == size else { return nil }
        return cstr(info.pvi_cdir.vip_path)
    }

    /// Last path component of the working directory (project folder), nil for "/".
    static func folder(_ pid: pid_t) -> String? {
        guard let name = cwd(pid).map({ ($0 as NSString).lastPathComponent }), !name.isEmpty, name != "/" else { return nil }
        return name
    }

    /// GitLab shell executor layout: ".../builds/<token>/<n>/<group>/<project>[/sub]" → "group/project".
    /// shortcut: nested groups lose their parent group; CI_PROJECT_PATH (when readable) is exact.
    static func buildsProject(_ path: String) -> String? {
        let parts = path.split(separator: "/")
        guard let i = parts.lastIndex(of: "builds"), parts.count > i + 4 else { return nil }
        return parts[(i + 3)...(i + 4)].joined(separator: "/").replacingOccurrences(of: ".tmp", with: "")
    }

    /// argv then envp of a process (readable for the user's own processes).
    private static func procArgs(_ pid: pid_t) -> (argc: Int, strings: [String]) {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 4 else { return (0, []) }
        var buf = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buf, &size, nil, 0) == 0, size > 4 else { return (0, []) }
        let argc = buf.withUnsafeBytes { $0.load(as: Int32.self) }
        // layout: argc, exec path, NUL padding, argv[argc], envp…
        return (Int(argc), Array(buf[4..<size].split(separator: 0).map { String(decoding: $0, as: UTF8.self) }.dropFirst()))
    }

    static func argv(_ pid: pid_t) -> [String] {
        let (argc, strings) = procArgs(pid)
        return Array(strings.prefix(argc))
    }

    /// CI_* environment variables a process started with.
    static func ciEnv(_ pid: pid_t) -> [String: String] {
        let (argc, strings) = procArgs(pid)
        var env: [String: String] = [:]
        for s in strings.dropFirst(argc) where s.hasPrefix("CI_") {
            if let eq = s.firstIndex(of: "=") { env[String(s[..<eq])] = String(s[s.index(after: eq)...]) }
        }
        return env
    }

    /// Total CPU time in ns, nil if the process is gone.
    static func cpuTime(_ pid: pid_t) -> Double? {
        var info = proc_taskinfo()
        let size = Int32(MemoryLayout<proc_taskinfo>.size)
        guard proc_pidinfo(pid, PROC_PIDTASKINFO, 0, &info, size) == size else { return nil }
        return Double(info.pti_total_user + info.pti_total_system) * timebase
    }
}

func ago(_ s: TimeInterval) -> String {
    s < 60 ? L("{0} giây", Int(s)) : s < 3600 ? L("{0} phút", Int(s / 60)) : L("{0} giờ", Int(s / 3600))
}
