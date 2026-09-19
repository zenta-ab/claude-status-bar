import Darwin
import Foundation

/// One spawned `claude` child, contained the way docs/mac-port.md §6 measured it.
///
/// macOS has no job objects, so containment is three mechanisms, in order of who covers what:
///
///  1. **The stdin pipe is the primary mechanism.** When this process dies -- including a force
///     quit, which runs no cleanup code at all -- the write end closes, `claude` reads EOF and
///     exits. Measured: child and its `node` descendant both gone within 2 s of the parent being
///     SIGKILLed. This is the only mechanism that covers an abnormal exit.
///
///     **This only holds if THIS PROCESS owns every copy of that write end.** Pipes are not
///     close-on-exec by default, so without `FD_CLOEXEC` a second account's child inherits the
///     first child's stdin writer, and the first `claude` then never sees EOF no matter what
///     happens to the app. Every descriptor here is therefore marked `FD_CLOEXEC` the instant it
///     is created; the three that the child is meant to get are handed over through `dup2` in the
///     spawn file actions, and `dup2` clears the flag on the copy, which is exactly the wanted
///     behaviour. (Codex adversarial review, 2026-09-19, finding 1.)
///
///  2. **`POSIX_SPAWN_SETSID` + `kill(-pgid)` for a clean exit.** The child leads its own session
///     and process group, which is what makes a group kill safe: killing *this* process's group
///     would kill the app.
///
///  3. **An orphan sweep at launch**, for the one measured hole: a *hung* child (simulated with
///     SIGSTOP) survives a force quit as an orphan on launchd, since a stopped process never
///     reads the EOF. See `sweepOrphans` for why it is driven by a recorded
///     pid+start-time record rather than by scanning the process table.
///
/// `Foundation.Process` has no hook for `POSIX_SPAWN_SETSID`, so the spawn goes through
/// `posix_spawn` directly. Every step of that is checked: a partially-configured spawn could
/// otherwise launch a child with no redirection, the wrong working directory, or -- worst of all
/// for multi-account -- a truncated environment missing `CLAUDE_CONFIG_DIR`, which silently
/// points it at the wrong login.
public final class ChildProcess: @unchecked Sendable {
    public let identity: ProcessIdentity
    public var pid: pid_t { identity.pid }
    public let standardInput: FileHandle
    public let standardOutput: FileHandle
    public let standardError: FileHandle

    private let lock = NSLock()
    private var reaped = false
    private let pidFileURL: URL?

    public enum LaunchError: Error, CustomStringConvertible {
        case executableNotFound(String)
        case spawnFailed(String, Int32)
        case setupFailed(String, Int32)

        public var description: String {
            switch self {
            case .executableNotFound(let path):
                return "claude not found at \(path)"
            case .spawnFailed(let path, let code):
                return "posix_spawn failed for \(path): \(String(cString: strerror(code))) (\(code))"
            case .setupFailed(let what, let code):
                return "refusing to launch an uncontained claude child: \(what) failed: "
                     + "\(String(cString: strerror(code))) (\(code))"
            }
        }
    }

    // MARK: - Launch

    public static func launch(_ spec: ChildProcessSpec) throws -> ChildProcess {
        // Re-resolved here, on every launch, never cached -- see ChildProcessSpec.
        let executable = spec.resolveExecutablePath()
        guard FileManager.default.isExecutableFile(atPath: executable) else {
            throw LaunchError.executableNotFound(executable)
        }

        // A working directory we cannot create is a hard failure, not something to shrug at: the
        // child would otherwise inherit OUR cwd and start a real Claude Code session there,
        // firing SessionStart hooks inside whatever repo the app happened to be launched from.
        do {
            try FileManager.default.createDirectory(
                atPath: spec.workingDirectory, withIntermediateDirectories: true)
        } catch {
            throw LaunchError.setupFailed("createDirectory(\(spec.workingDirectory))", errno)
        }

        // Every descriptor opened below is registered here, so any failure path closes all of
        // them. The previous version threw on a failed second `pipe()` and leaked the first,
        // which under descriptor pressure made the exhaustion permanent across retries.
        var opened: [Int32] = []
        func closeAllOpened() { for fd in opened { close(fd) } ; opened.removeAll() }

        func makePipe(_ label: String) throws -> (read: Int32, write: Int32) {
            var fds: [Int32] = [-1, -1]
            guard pipe(&fds) == 0 else {
                let code = errno; closeAllOpened()
                throw LaunchError.setupFailed("pipe(\(label))", code)
            }
            opened.append(fds[0]); opened.append(fds[1])
            // Mechanism 1 depends on this; see the type's doc comment.
            for fd in fds where fcntl(fd, F_SETFD, FD_CLOEXEC) == -1 {
                let code = errno; closeAllOpened()
                throw LaunchError.setupFailed("fcntl(FD_CLOEXEC, \(label))", code)
            }
            return (fds[0], fds[1])
        }

        let stdinPipe = try makePipe("stdin")
        let stdoutPipe = try makePipe("stdout")
        let stderrPipe = try makePipe("stderr")

        var attr = posix_spawnattr_t(nil as OpaquePointer?)
        guard posix_spawnattr_init(&attr) == 0 else {
            let code = errno; closeAllOpened()
            throw LaunchError.setupFailed("posix_spawnattr_init", code)
        }
        defer { posix_spawnattr_destroy(&attr) }

        // POSIX_SPAWN_SETSID: the child leads its own session and process group. Mechanism 2
        // depends on it, and so does never signalling our own group by accident. Failing here
        // means no child is launched at all, rather than an uncontained one.
        let setFlags = posix_spawnattr_setflags(&attr, Int16(POSIX_SPAWN_SETSID))
        guard setFlags == 0 else {
            closeAllOpened()
            throw LaunchError.setupFailed("posix_spawnattr_setflags(POSIX_SPAWN_SETSID)", setFlags)
        }

        var actions = posix_spawn_file_actions_t(nil as OpaquePointer?)
        guard posix_spawn_file_actions_init(&actions) == 0 else {
            let code = errno; closeAllOpened()
            throw LaunchError.setupFailed("posix_spawn_file_actions_init", code)
        }
        defer { posix_spawn_file_actions_destroy(&actions) }

        func addAction(_ label: String, _ rc: Int32) throws {
            guard rc == 0 else { closeAllOpened(); throw LaunchError.setupFailed(label, rc) }
        }
        // dup2 clears FD_CLOEXEC on the copy, so these three -- and only these three -- survive
        // exec in the child.
        try addAction("adddup2(stdin)", posix_spawn_file_actions_adddup2(&actions, stdinPipe.read, STDIN_FILENO))
        try addAction("adddup2(stdout)", posix_spawn_file_actions_adddup2(&actions, stdoutPipe.write, STDOUT_FILENO))
        try addAction("adddup2(stderr)", posix_spawn_file_actions_adddup2(&actions, stderrPipe.write, STDERR_FILENO))
        try addAction("addchdir_np", posix_spawn_file_actions_addchdir_np(&actions, spec.workingDirectory))

        var environment = ProcessInfo.processInfo.environment
        for (key, value) in spec.environmentOverrides { environment[key] = value }
        for key in spec.environmentRemovals { environment.removeValue(forKey: key) }

        let argv: [String] = [executable] + spec.arguments
        var cArgs: [UnsafeMutablePointer<CChar>?] = []
        var cEnv: [UnsafeMutablePointer<CChar>?] = []
        func freeCStrings() {
            for p in cArgs where p != nil { free(p) }
            for p in cEnv where p != nil { free(p) }
            cArgs.removeAll(); cEnv.removeAll()
        }
        // A failed strdup used to be invisible: the array kept a nil in the middle, which
        // truncates argv/envp at that point. A truncated envp can drop CLAUDE_CONFIG_DIR and
        // silently point the child at the default login.
        for argument in argv {
            guard let duplicated = strdup(argument) else {
                freeCStrings(); closeAllOpened()
                throw LaunchError.setupFailed("strdup(argument)", ENOMEM)
            }
            cArgs.append(duplicated)
        }
        cArgs.append(nil)
        for (key, value) in environment {
            guard let duplicated = strdup("\(key)=\(value)") else {
                freeCStrings(); closeAllOpened()
                throw LaunchError.setupFailed("strdup(environment)", ENOMEM)
            }
            cEnv.append(duplicated)
        }
        cEnv.append(nil)
        defer { freeCStrings() }

        var childPid: pid_t = 0
        let rc = posix_spawn(&childPid, executable, &actions, &attr, cArgs, cEnv)

        // The child owns its ends now; this process must close them or it never sees EOF.
        close(stdinPipe.read); close(stdoutPipe.write); close(stderrPipe.write)
        opened.removeAll { $0 == stdinPipe.read || $0 == stdoutPipe.write || $0 == stderrPipe.write }

        guard rc == 0 else {
            closeAllOpened()
            throw LaunchError.spawnFailed(executable, rc)
        }

        // Read the start time immediately, so the identity belongs to the process we just made
        // and not to whatever might later inherit the pid. If the child is so short-lived that
        // it is already gone, fall back to a zero start time -- the record is then simply never
        // going to match anything, which is the safe direction.
        let identity = ProcessIdentity.current(of: childPid)
            ?? ProcessIdentity(pid: childPid, startSeconds: 0, startMicroseconds: 0, executablePath: "")

        let child = ChildProcess(
            identity: identity,
            pidFileURL: spec.pidFileURL,
            standardInput: FileHandle(fileDescriptor: stdinPipe.write, closeOnDealloc: true),
            standardOutput: FileHandle(fileDescriptor: stdoutPipe.read, closeOnDealloc: true),
            standardError: FileHandle(fileDescriptor: stderrPipe.read, closeOnDealloc: true))
        child.writePidFile()
        return child
    }

    private init(identity: ProcessIdentity, pidFileURL: URL?,
                 standardInput: FileHandle, standardOutput: FileHandle, standardError: FileHandle) {
        self.identity = identity
        self.pidFileURL = pidFileURL
        self.standardInput = standardInput
        self.standardOutput = standardOutput
        self.standardError = standardError
    }

    // MARK: - Lifetime

    /// True only while this exact process instance is alive and has not been reaped.
    ///
    /// Both halves matter. `kill(pid, 0)` succeeds for a zombie, which is what made an earlier
    /// reading of the Phase 1 containment test conclude the child had "survived SIGTERM" when it
    /// had already exited. And once reaped, the pid may belong to something else entirely, so
    /// this must never answer true again for it.
    public var isRunning: Bool {
        lock.lock()
        let alreadyReaped = reaped
        lock.unlock()
        if alreadyReaped { return false }

        var status: Int32 = 0
        let waited = waitpid(pid, &status, WNOHANG)
        if waited == pid {
            lock.lock(); reaped = true; lock.unlock()
            removePidFile()
            return false
        }
        if waited == -1 && errno == ECHILD {
            // Not our child any more (already reaped elsewhere, or never ours).
            lock.lock(); reaped = true; lock.unlock()
            return false
        }
        // waited == 0: still running. Confirm it is still the same instance.
        return identity.isStillAlive
    }

    /// The clean-exit path: close stdin (mechanism 1, which is what the child actually responds
    /// to), then signal its whole group (mechanism 2), escalating to SIGKILL.
    ///
    /// Durations here are monotonic. Wall-clock deadlines would either hang or escalate instantly
    /// when the clock steps, and this project already treats every other duration as monotonic
    /// for exactly that reason (docs/forecast-and-states.md, round-1 decision 6).
    public func terminate(gracePeriod: TimeInterval = 2.0) {
        lock.lock()
        let alreadyReaped = reaped
        lock.unlock()
        if alreadyReaped { return }

        try? standardInput.close()
        if waitForExit(within: gracePeriod) { finishReap(); return }

        // Negative pid = the whole process group. Safe only because of POSIX_SPAWN_SETSID: the
        // child leads its own group, so this can never reach the app itself. Guarded on the
        // instance still being alive, so a recycled pid is never signalled.
        if identity.isStillAlive { kill(-pid, SIGTERM) }
        if waitForExit(within: 1.0) { finishReap(); return }
        if identity.isStillAlive { kill(-pid, SIGKILL) }
        _ = waitForExit(within: 1.0)
        finishReap()
    }

    private func waitForExit(within seconds: TimeInterval) -> Bool {
        let deadline = DispatchTime.now() + seconds
        while DispatchTime.now() < deadline {
            if !isRunning { return true }
            usleep(25_000)
        }
        return !isRunning
    }

    /// Reaps only when the child has genuinely exited. The earlier version set `reaped` after a
    /// `WNOHANG` call regardless of its result, so a still-running child was recorded as reaped
    /// and then leaked as a zombie, while its pid stayed eligible for signalling after reuse.
    private func finishReap() {
        lock.lock()
        if reaped { lock.unlock(); return }
        var status: Int32 = 0
        let waited = waitpid(pid, &status, WNOHANG)
        if waited == pid || (waited == -1 && errno == ECHILD) {
            reaped = true
            lock.unlock()
            removePidFile()
            return
        }
        lock.unlock()
    }

    // MARK: - The orphan record (containment mechanism 3)

    private func writePidFile() {
        guard let pidFileURL else { return }
        try? FileManager.default.createDirectory(
            at: pidFileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data(identity.fileContents.utf8).write(to: pidFileURL, options: .atomic)
    }

    private func removePidFile() {
        guard let pidFileURL else { return }
        try? FileManager.default.removeItem(at: pidFileURL)
    }

    /// Kills a child this app left behind, using the pid+start-time record written at launch.
    ///
    /// The first version of this scanned the process table for `claude` processes with ppid 1 and
    /// a working directory under the agent root. That was rejected in review, correctly, for two
    /// reasons: `hasPrefix` made `<agent>-copy` match `<agent>`, so a user's own `claude` in a
    /// neighbouring directory could be killed; and the pid was observed by `ps`, inspected by
    /// `lsof`, then signalled — three separate moments, with pid reuse possible in between.
    ///
    /// This version never enumerates anything and never guesses from paths. It only ever
    /// considers a pid this app itself recorded, and only kills it when the start time still
    /// matches to the microsecond and the executable is still `claude`. Anything else is left
    /// alone and the stale record is removed.
    ///
    /// The kill targets the process GROUP, matching `terminate`: killing just the stopped leader
    /// leaves its `node` descendant reparented and running, and nothing would ever collect it.
    @discardableResult
    public static func sweepOrphans(pidFileURLs: [URL]) -> [ProcessIdentity] {
        var killed: [ProcessIdentity] = []
        for url in pidFileURLs {
            guard let text = try? String(contentsOf: url, encoding: .utf8),
                  let recorded = ProcessIdentity.parse(text) else {
                try? FileManager.default.removeItem(at: url)
                continue
            }
            defer { try? FileManager.default.removeItem(at: url) }

            guard recorded.isStillAlive else { continue }          // exited cleanly, or pid reused
            guard recorded.pid != getpid() else { continue }       // never signal ourselves
            // Compare the kernel's view now against the kernel's view at launch. A record with
            // no path predates this field and is carried by pid+start time alone, which is
            // already conclusive; an empty live path means the process is going away.
            if !recorded.executablePath.isEmpty {
                guard let livePath = ProcessIdentity.executablePath(of: recorded.pid),
                      livePath == recorded.executablePath else { continue }
            }

            // Re-check immediately before signalling: this is the last moment the identity can
            // be confirmed, which is as tight as this race can be made without a pidfd.
            guard recorded.isStillAlive else { continue }
            if kill(-recorded.pid, SIGKILL) == 0 || kill(recorded.pid, SIGKILL) == 0 {
                killed.append(recorded)
            }
        }
        return killed
    }

    /// Sweeps every pid record under the app's agent directory.
    @discardableResult
    public static func sweepOrphans() -> [ProcessIdentity] {
        let agentRoot = ChildProcessSpec.agentRootURL()
        guard let entries = try? FileManager.default.contentsOfDirectory(
            at: agentRoot, includingPropertiesForKeys: nil) else { return [] }
        return sweepOrphans(pidFileURLs: entries.filter { $0.pathExtension == "pid" })
    }
}
