import Darwin
import Foundation

/// Owns one long-lived `claude -p --input-format stream-json --output-format stream-json
/// --verbose` child and correlates `control_request`/`control_response` pairs on `request_id`.
/// Mirrors `src/Windows/Data/ClaudeCliChannel.cs`, including the safeguards that implementation
/// learned the hard way and that the first cut of this port had quietly dropped (Codex
/// adversarial review, 2026-09-19).
///
/// This type owns exactly one child for its own lifetime: it does not retry or relaunch. That is
/// a supervisor's job. What it *does* do is fault decisively, so a supervisor has something
/// unambiguous to act on -- `isHealthy` going false is the signal to dispose and relaunch.
public final class ControlChannel: @unchecked Sendable {
    /// Phase 1 §3 measured `get_usage` latency at 16-146 ms cached, 156-1164 ms live, with the
    /// **first reply after spawn taking 1164 ms**. The plan's "10-20 ms with skip_behaviors"
    /// describes only the cached regime. Sized for the cold case with room to spare -- a
    /// too-tight timeout would fault a perfectly healthy channel on every start.
    public static let defaultTimeout: TimeInterval = 15.0

    /// stderr is drained continuously but only this much is kept. An undrained stderr pipe is a
    /// child deadlock: once the 64 KB buffer fills, `claude` blocks writing a diagnostic and
    /// stops answering requests entirely. The Windows channel pumps it for the same reason.
    static let retainedStandardErrorBytes = 64 * 1024

    public enum ChannelError: Error, CustomStringConvertible {
        case timedOut(TimeInterval)
        case closed(String)
        case encodingFailed

        public var description: String {
            switch self {
            case .timedOut(let seconds): return "no control_response within \(seconds) s"
            case .closed(let why): return "channel closed: \(why)"
            case .encodingFailed: return "could not encode the control_request"
            }
        }
    }

    private let child: ChildProcess
    private let framer = LineFramer()
    private let lock = NSCondition()
    private let writeLock = NSLock()

    /// Correlation state. `pending` is the set of ids a caller is actually waiting for; a reply
    /// whose id is not in it is dropped rather than stored. The earlier version stored every
    /// syntactically valid reply, so a late answer to a timed-out request stayed in memory
    /// forever and a child emitting unknown ids could grow the dictionary without bound.
    private var pending: Set<String> = []
    private var responses: [String: [String: Any]] = [:]
    private var nextRequestId = 0
    private var faultReason: String?
    private var stopping = false
    private var standardErrorTail = Data()

    public var childPid: pid_t { child.pid }
    public var childIdentity: ProcessIdentity { child.identity }

    public var isHealthy: Bool {
        lock.lock(); defer { lock.unlock() }
        return faultReason == nil
    }

    public var faultDescription: String? {
        lock.lock(); defer { lock.unlock() }
        return faultReason
    }

    /// Whatever the child last wrote to stderr, for diagnostics after a fault.
    public var recentStandardError: String {
        lock.lock(); defer { lock.unlock() }
        return String(data: standardErrorTail, encoding: .utf8) ?? ""
    }

    public init(child: ChildProcess) {
        self.child = child
        startReader(child.standardOutput, isStandardError: false)
        startReader(child.standardError, isStandardError: true)
    }

    /// Launches a child, after clearing any orphan this app left behind (containment mechanism 3).
    /// The sweep lives here, not in the probe, so every caller of the public launch API gets it --
    /// the earlier version left it to `quotaprobe`, which meant any other entry point silently
    /// skipped containment.
    public static func launch(_ spec: ChildProcessSpec = .default()) throws -> ControlChannel {
        if let pidFileURL = spec.pidFileURL {
            ChildProcess.sweepOrphans(pidFileURLs: [pidFileURL])
        }
        return ControlChannel(child: try ChildProcess.launch(spec))
    }

    // MARK: - Reading

    private func startReader(_ handle: FileHandle, isStandardError: Bool) {
        let thread = Thread { [weak self] in
            // Deliberately NOT `guard let self` around the loop: that promotes the weak capture
            // to a strong reference held for the lifetime of an infinite loop, so the channel
            // could never be deallocated and `deinit` -- which terminates the child -- never ran.
            // Re-acquiring per iteration lets the last external reference actually drop.
            while true {
                let data = handle.availableData
                guard let self else { return }
                if data.isEmpty {
                    if !isStandardError { self.fault("stdout EOF") }
                    return
                }
                if self.isStopping { return }
                if isStandardError {
                    self.appendStandardError(data)
                } else {
                    for line in self.framer.append(data) { self.handle(line: line) }
                }
            }
        }
        thread.name = isStandardError ? "ClaudeControlChannel.stderr" : "ClaudeControlChannel.stdout"
        thread.stackSize = 512 * 1024
        thread.start()
    }

    private var isStopping: Bool {
        lock.lock(); defer { lock.unlock() }
        return stopping
    }

    private func appendStandardError(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        standardErrorTail.append(data)
        if standardErrorTail.count > Self.retainedStandardErrorBytes {
            standardErrorTail.removeFirst(standardErrorTail.count - Self.retainedStandardErrorBytes)
        }
    }

    /// Structurally defensive at every step: a syntactically valid but structurally unexpected
    /// envelope (`{"type":"control_response","response":null}`) must come back as "not for us"
    /// rather than throwing and killing the reader (`src/Windows/Data/EnvelopeParser.cs`).
    private func handle(line: String) {
        guard let data = line.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              root["type"] as? String == "control_response",
              let response = root["response"] as? [String: Any],
              let requestId = Self.requestId(from: response["request_id"]) else { return }

        lock.lock()
        // Only ids someone is actually waiting for are retained; everything else is dropped.
        if pending.contains(requestId) {
            responses[requestId] = root
            lock.broadcast()
        }
        lock.unlock()
    }

    /// A JSON `true` bridges to `NSNumber`, whose `stringValue` is `"1"` -- which would satisfy a
    /// wait on request id 1. Booleans are rejected outright; ids are strings or integers only.
    /// (Verified: `JSONSerialization` turns `{"request_id": true}` into exactly that.)
    static func requestId(from value: Any?) -> String? {
        switch value {
        case let text as String: return text.isEmpty ? nil : text
        case let number as NSNumber:
            if CFGetTypeID(number) == CFBooleanGetTypeID() { return nil }
            return number.stringValue
        default: return nil
        }
    }

    private func fault(_ reason: String) {
        lock.lock()
        if faultReason == nil { faultReason = reason }
        lock.broadcast()
        lock.unlock()
    }

    // MARK: - Requesting

    /// Sends one `get_usage` control request and waits for its reply.
    ///
    /// `skipBehaviors` is what keeps this cheap: the reply omits the behaviours payload. It is
    /// honoured on macOS (`"behaviors": null`, Phase 1 §3). Control requests are unmetered, so
    /// polling does not consume the user's quota.
    ///
    /// The deadline covers the **write as well as the wait**. A blocking write into a full pipe
    /// -- which is what a stopped child produces after enough unanswered requests -- used to hang
    /// forever, because the timeout was only constructed after `write` returned.
    @discardableResult
    public func requestUsage(
        skipBehaviors: Bool = true, timeout: TimeInterval = defaultTimeout
    ) throws -> (root: [String: Any], latency: TimeInterval) {
        let started = DispatchTime.now()
        let deadline = started + timeout

        lock.lock()
        if let faultReason { lock.unlock(); throw ChannelError.closed(faultReason) }
        nextRequestId += 1
        let requestId = String(nextRequestId)
        pending.insert(requestId)
        lock.unlock()

        // Every exit path below must clear the pending entry, or the set leaks one id per
        // failure and the reader starts retaining replies nobody will ever collect.
        func forget() {
            lock.lock()
            pending.remove(requestId)
            responses.removeValue(forKey: requestId)
            lock.unlock()
        }

        var request: [String: Any] = ["subtype": "get_usage"]
        if skipBehaviors { request["skip_behaviors"] = true }
        let envelope: [String: Any] = [
            "type": "control_request", "request_id": requestId, "request": request,
        ]
        guard let body = try? JSONSerialization.data(withJSONObject: envelope) else {
            forget(); throw ChannelError.encodingFailed
        }
        var line = body
        line.append(0x0A)

        do {
            try write(line, by: deadline)
        } catch {
            forget()
            if let channelError = error as? ChannelError {
                if case .timedOut = channelError { fault("stdin write timed out") }
                throw channelError
            }
            fault("stdin write failed: \(error)")
            throw ChannelError.closed("stdin write failed")
        }

        lock.lock()
        defer { lock.unlock() }
        while true {
            if let root = responses.removeValue(forKey: requestId) {
                pending.remove(requestId)
                let elapsed = Double(DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1e9
                return (root, elapsed)
            }
            if let faultReason {
                pending.remove(requestId)
                throw ChannelError.closed(faultReason)
            }
            if !lock.wait(until: Date().addingTimeInterval(remaining(until: deadline))) {
                pending.remove(requestId)
                lock.unlock()
                // A timeout is a fault: the channel has an unanswered request outstanding and no
                // way to know whether a late reply is coming. Saying so lets a supervisor recycle
                // the child instead of leaving `isHealthy` true on a channel that is not.
                fault("request \(requestId) timed out after \(timeout) s")
                lock.lock()
                throw ChannelError.timedOut(timeout)
            }
        }
    }

    private func remaining(until deadline: DispatchTime) -> TimeInterval {
        let now = DispatchTime.now()
        guard deadline > now else { return 0 }
        return Double(deadline.uptimeNanoseconds - now.uptimeNanoseconds) / 1e9
    }

    /// Serialized, non-blocking, deadline-bounded write.
    ///
    /// Two separate problems are solved here. Concurrent callers used to be able to interleave
    /// inside one `FileHandle.write`, corrupting the newline-delimited framing the child parses
    /// (the Windows channel serializes stdin for exactly this reason). And a blocking write into
    /// a full pipe never returns, so the request timeout could not fire.
    private func write(_ data: Data, by deadline: DispatchTime) throws {
        writeLock.lock()
        defer { writeLock.unlock() }

        let fd = child.standardInput.fileDescriptor
        let originalFlags = fcntl(fd, F_GETFL)
        if originalFlags != -1 { _ = fcntl(fd, F_SETFL, originalFlags | O_NONBLOCK) }
        defer { if originalFlags != -1 { _ = fcntl(fd, F_SETFL, originalFlags) } }

        try data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.baseAddress else { return }
            var offset = 0
            while offset < raw.count {
                let written = Darwin.write(fd, base + offset, raw.count - offset)
                if written > 0 { offset += written; continue }
                if written == -1 && (errno == EAGAIN || errno == EWOULDBLOCK) {
                    let left = remaining(until: deadline)
                    if left <= 0 { throw ChannelError.timedOut(0) }
                    var pfd = pollfd(fd: fd, events: Int16(POLLOUT), revents: 0)
                    let ready = poll(&pfd, 1, Int32(min(left, 1.0) * 1000))
                    if ready < 0 && errno != EINTR { throw ChannelError.closed("poll failed") }
                    continue
                }
                if written == -1 && errno == EINTR { continue }
                throw ChannelError.closed("write failed: \(String(cString: strerror(errno)))")
            }
        }
    }

    public func usageSnapshot(
        skipBehaviors: Bool = true, timeout: TimeInterval = defaultTimeout
    ) throws -> (result: UsageParser.ParseResult, latency: TimeInterval) {
        let (root, latency) = try requestUsage(skipBehaviors: skipBehaviors, timeout: timeout)
        return (UsageParser.parse(controlResponse: root), latency)
    }

    public func shutdown() {
        lock.lock()
        stopping = true
        pending.removeAll()
        responses.removeAll()
        lock.broadcast()
        lock.unlock()
        child.terminate()
    }

    deinit {
        lock.lock(); stopping = true; lock.unlock()
        child.terminate(gracePeriod: 0.5)
    }
}
