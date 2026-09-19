import Darwin
import Foundation

/// A pid plus the process's start time, which together identify a process *instance* rather than
/// just a slot in the pid table.
///
/// This exists because of a race the first cut of the orphan sweep had: it found a pid by running
/// `ps`, then ran `lsof` on it, then sent `SIGKILL` — and a pid that exits between the first and
/// last step can be recycled onto an unrelated process, which then receives the kill. macOS
/// recycles pids aggressively, so this is not theoretical. Start time is what disambiguates:
/// a recycled pid has a different one, to the microsecond.
public struct ProcessIdentity: Equatable, Sendable, CustomStringConvertible {
    public let pid: pid_t
    public let startSeconds: Int64
    public let startMicroseconds: Int32
    /// The executable path as the KERNEL reports it (`proc_pidpath`), captured at launch.
    ///
    /// This is deliberately not the path we asked to launch. `~/.local/bin/claude` is a symlink
    /// into `~/.local/share/claude/versions/<version>`, and `proc_pidpath` reports the resolved
    /// target — so a check for a trailing "claude" matches nothing, and comparing against the
    /// launch path fails too. That mistake silently disabled the whole orphan sweep until a live
    /// test caught it. Recording what the kernel says, and comparing like with like, is the fix.
    public let executablePath: String

    public init(pid: pid_t, startSeconds: Int64, startMicroseconds: Int32, executablePath: String = "") {
        self.pid = pid
        self.startSeconds = startSeconds
        self.startMicroseconds = startMicroseconds
        self.executablePath = executablePath
    }

    public var description: String { "\(pid)@\(startSeconds).\(startMicroseconds)" }

    /// Identity for signalling purposes: pid and start time must match exactly. The executable
    /// path is compared separately by the sweep, so that a record written before this field
    /// existed still behaves sensibly.
    public static func == (a: ProcessIdentity, b: ProcessIdentity) -> Bool {
        a.pid == b.pid && a.startSeconds == b.startSeconds && a.startMicroseconds == b.startMicroseconds
    }

    /// Reads the live start time for `pid` via `sysctl(KERN_PROC_PID)`. Returns nil when the
    /// process does not exist or cannot be inspected.
    public static func current(of pid: pid_t) -> ProcessIdentity? {
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        let rc = sysctl(&mib, u_int(mib.count), &info, &size, nil, 0)
        // A dead pid yields rc == 0 with size == 0 rather than an error, so size is the real check.
        guard rc == 0, size > 0, info.kp_proc.p_pid == pid else { return nil }
        let start = info.kp_proc.p_un.__p_starttime
        return ProcessIdentity(pid: pid,
                               startSeconds: Int64(start.tv_sec),
                               startMicroseconds: Int32(start.tv_usec),
                               executablePath: executablePath(of: pid) ?? "")
    }

    /// True when this exact process instance is still alive — same pid *and* same start time.
    public var isStillAlive: Bool { Self.current(of: pid) == self }

    /// The absolute path of the running executable, used as a second, cheap sanity check before
    /// signalling anything. Empty when it cannot be read.
    public static func executablePath(of pid: pid_t) -> String? {
        var buffer = [CChar](repeating: 0, count: Int(4 * MAXPATHLEN))
        let length = proc_pidpath(pid, &buffer, UInt32(buffer.count))
        guard length > 0 else { return nil }
        let bytes = buffer.prefix(Int(length)).map { UInt8(bitPattern: $0) }
        return String(decoding: bytes, as: UTF8.self)
    }

    // MARK: - On-disk record

    /// `<pid> <sec> <usec> <executable path>` — trivial to parse, and safe to read from a file
    /// written by a process that was killed mid-write (a short or malformed record yields nil).
    /// The path goes last because it may contain spaces.
    public var fileContents: String {
        "\(pid) \(startSeconds) \(startMicroseconds) \(executablePath)\n"
    }

    public static func parse(_ text: String) -> ProcessIdentity? {
        let line = text.split(whereSeparator: { $0 == "\n" || $0 == "\r" }).first ?? ""
        let parts = line.split(separator: " ", maxSplits: 3, omittingEmptySubsequences: false)
        guard parts.count >= 3,
              let pid = pid_t(parts[0]), let sec = Int64(parts[1]), let usec = Int32(parts[2]),
              pid > 1 else { return nil }
        let path = parts.count == 4 ? String(parts[3]) : ""
        return ProcessIdentity(pid: pid, startSeconds: sec, startMicroseconds: usec, executablePath: path)
    }
}
