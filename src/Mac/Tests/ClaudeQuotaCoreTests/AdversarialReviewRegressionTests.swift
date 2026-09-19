import Darwin
import Foundation
import Testing

@testable import ClaudeQuotaCore

/// One test per finding that survived verification in the Codex adversarial review of
/// 2026-09-19. Each names the failure it prevents, so nobody re-introduces it by "simplifying".
@Suite("Adversarial review regressions")
struct AdversarialReviewRegressionTests {

    // MARK: - Finding 1 (critical): pipe descriptors must not leak into other children

    /// Without `FD_CLOEXEC`, a second account's child inherits the first child's stdin *write*
    /// end. The first `claude` then never sees EOF when the app dies, which defeats the primary
    /// containment mechanism entirely — and it only shows up with two or more accounts, which is
    /// why the single-child Phase 1 measurement missed it.
    @Test("every descriptor the parent retains is close-on-exec")
    func retainedDescriptorsAreCloseOnExec() throws {
        let spec = fakeChildSpec(script: "cat > /dev/null")
        let child = try ChildProcess.launch(spec)
        defer { child.terminate(gracePeriod: 0.3) }

        for (name, handle) in [("stdin", child.standardInput),
                               ("stdout", child.standardOutput),
                               ("stderr", child.standardError)] {
            let flags = fcntl(handle.fileDescriptor, F_GETFD)
            #expect(flags != -1, "F_GETFD failed for \(name)")
            #expect(flags & FD_CLOEXEC == FD_CLOEXEC, "\(name) is not close-on-exec")
        }
    }

    /// The behavioural half of the same finding: a second child must not hold the first child's
    /// stdin open. Proven by closing our own copy and observing that the first child reaches EOF
    /// — if the second child held a duplicate, it never would.
    @Test("a second child does not keep the first child's stdin alive")
    func secondChildDoesNotInheritFirstStdin() throws {
        // `cat` exits at EOF, so "did it exit?" answers "did every writer close?".
        let first = try ChildProcess.launch(fakeChildSpec(script: "cat > /dev/null"))
        let second = try ChildProcess.launch(fakeChildSpec(script: "sleep 30"))
        defer { second.terminate(gracePeriod: 0.3) }

        try? first.standardInput.close()

        var exited = false
        for _ in 0..<100 {
            if !first.isRunning { exited = true; break }
            usleep(50_000)
        }
        #expect(exited, "the first child never saw EOF — its stdin write end leaked into the second child")
    }

    // MARK: - Finding 3 (critical): pid identity after reaping

    @Test("a reaped child never reports running again")
    func reapedChildStaysDead() throws {
        let child = try ChildProcess.launch(fakeChildSpec(script: "exit 0"))
        for _ in 0..<100 where child.isRunning { usleep(20_000) }
        #expect(child.isRunning == false)
        child.terminate(gracePeriod: 0.2)
        // Even repeatedly, and even though the pid may now belong to something else.
        #expect(child.isRunning == false)
        #expect(child.isRunning == false)
    }

    @Test("terminate is idempotent and never signals twice")
    func terminateIsIdempotent() throws {
        let child = try ChildProcess.launch(fakeChildSpec(script: "sleep 30"))
        child.terminate(gracePeriod: 0.3)
        #expect(child.isRunning == false)
        child.terminate(gracePeriod: 0.3)   // must be a no-op, not a second kill of a reused pid
        #expect(child.isRunning == false)
    }

    // MARK: - Finding 2 (critical): the sweep must only ever touch what it recorded

    @Test("a pid record whose start time no longer matches is never signalled")
    func sweepIgnoresRecycledPid() throws {
        let directory = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let record = directory.appendingPathComponent("stale.pid")

        // Our own pid, with a deliberately wrong start time: the pid is unquestionably alive, so
        // only the start-time check can save us. If it does not, this test kills the test runner.
        let wrong = ProcessIdentity(pid: getpid(), startSeconds: 1, startMicroseconds: 1)
        try Data(wrong.fileContents.utf8).write(to: record)

        let killed = ChildProcess.sweepOrphans(pidFileURLs: [record])
        #expect(killed.isEmpty)
        #expect(FileManager.default.fileExists(atPath: record.path) == false, "the stale record should be cleaned up")
    }

    /// A live process whose pid and start time match the record, but whose executable is not the
    /// one we launched, must be left alone: that is a pid the kernel re-issued to something else
    /// between our record being written and the sweep running.
    @Test("a live pid running a different executable is never signalled")
    func sweepIgnoresForeignExecutable() throws {
        let directory = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let record = directory.appendingPathComponent("foreign.pid")

        // A real, live, unrelated process — not us, so the self-guard cannot be what saves it.
        let bystander = try ChildProcess.launch(fakeChildSpec(script: "sleep 20"))
        defer { bystander.terminate(gracePeriod: 0.3) }

        // Correct pid and start time, but the executable path of something else entirely.
        let mismatched = ProcessIdentity(
            pid: bystander.pid,
            startSeconds: bystander.identity.startSeconds,
            startMicroseconds: bystander.identity.startMicroseconds,
            executablePath: "/usr/bin/definitely-not-what-we-launched")
        try Data(mismatched.fileContents.utf8).write(to: record)

        #expect(ChildProcess.sweepOrphans(pidFileURLs: [record]).isEmpty)
        #expect(bystander.isRunning, "the bystander was killed despite a mismatched executable path")
    }

    /// The other direction: a record that matches in every respect IS swept, group and all.
    /// Without this, "nothing was killed" would pass even if the sweep were dead code — which is
    /// exactly the state an earlier executable-name check left it in.
    @Test("a matching record is swept, together with the process group")
    func sweepKillsItsOwnOrphan() throws {
        let directory = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let record = directory.appendingPathComponent("ours.pid")

        let orphan = try ChildProcess.launch(fakeChildSpec(script: "sleep 20"))
        let pid = orphan.pid
        try Data(orphan.identity.fileContents.utf8).write(to: record)

        let killed = ChildProcess.sweepOrphans(pidFileURLs: [record])
        #expect(killed.count == 1)
        #expect(killed.first?.pid == pid)

        var gone = false
        for _ in 0..<100 {
            if kill(pid, 0) != 0 { gone = true; break }
            var status: Int32 = 0
            if waitpid(pid, &status, WNOHANG) == pid { gone = true; break }
            usleep(20_000)
        }
        #expect(gone, "the sweep reported a kill but the process is still there")
    }

    @Test("a malformed or truncated pid record is discarded, not guessed at", arguments: [
        "", "\n", "0 0 0", "1 2", "notapid 1 2", "1 1 1", String(repeating: "9", count: 40),
    ])
    func sweepRejectsMalformedRecords(_ contents: String) throws {
        let directory = temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let record = directory.appendingPathComponent("junk.pid")
        try Data(contents.utf8).write(to: record)
        #expect(ChildProcess.sweepOrphans(pidFileURLs: [record]).isEmpty)
    }

    @Test("pid records round-trip, including a path with spaces")
    func pidRecordRoundTrip() {
        let identity = ProcessIdentity(pid: 4321, startSeconds: 1_789_000_000, startMicroseconds: 123_456,
                                       executablePath: "/Applications/Some App/bin/claude")
        let parsed = try! #require(ProcessIdentity.parse(identity.fileContents))
        #expect(parsed == identity)
        #expect(parsed.executablePath == "/Applications/Some App/bin/claude")
        // A record from before the path field existed must still parse.
        let legacy = try! #require(ProcessIdentity.parse("4321 1789000000 123456\n"))
        #expect(legacy.pid == 4321)
        #expect(legacy.executablePath.isEmpty)
    }

    /// The kernel resolves a symlinked launch path, so the recorded path must be the kernel's
    /// own view — `~/.local/bin/claude` is reported as `.../versions/<version>`. Checking for a
    /// trailing "claude" matched nothing and silently disabled the entire sweep.
    @Test("the recorded executable path is what the kernel reports, not the launch path")
    func recordedPathIsTheKernelsView() throws {
        let child = try ChildProcess.launch(fakeChildSpec(script: "sleep 5"))
        defer { child.terminate(gracePeriod: 0.3) }
        let live = try #require(ProcessIdentity.executablePath(of: child.pid))
        #expect(child.identity.executablePath == live)
        #expect(child.identity.executablePath.isEmpty == false)
    }

    @Test("this process's own identity is readable and stable")
    func ownIdentityIsStable() throws {
        let a = try #require(ProcessIdentity.current(of: getpid()))
        let b = try #require(ProcessIdentity.current(of: getpid()))
        #expect(a == b)
        #expect(a.isStillAlive)
        #expect(ProcessIdentity.current(of: pid_t(Int32.max)) == nil)
    }

    // MARK: - Finding 7 (high): the secure-storage variable outranks CLAUDE_CONFIG_DIR

    /// docs/mac-port.md §2: Claude Code reads `CLAUDE_SECURESTORAGE_CONFIG_DIR` *in preference*
    /// to `CLAUDE_CONFIG_DIR` when picking the keychain namespace. Inheriting a stray one would
    /// collapse every account onto a single login while each still looked isolated.
    @Test("a pinned account pins the secure-storage directory too")
    func pinnedAccountPinsSecureStorage() {
        let spec = ChildProcessSpec.forAccount(slot: "1", configDirectory: "/tmp/acct-1/")
        #expect(spec.environmentOverrides[ChildProcessSpec.configDirVariable] == "/tmp/acct-1")
        #expect(spec.environmentOverrides[ChildProcessSpec.secureStorageVariable] == "/tmp/acct-1")
    }

    @Test("the default account still inherits the user's environment untouched")
    func defaultAccountInheritsEnvironment() {
        let spec = ChildProcessSpec.forAccount(slot: "default", configDirectory: nil)
        #expect(spec.environmentOverrides.isEmpty)
        #expect(spec.environmentRemovals.isEmpty)
    }

    // MARK: - Malformed input: JSON booleans must not become numbers

    /// `JSONSerialization` bridges `true` to an `NSNumber` whose `doubleValue` is 1.0, so
    /// `{"percent": true}` would render as "1 % used" — a confident wrong number.
    @Test("a boolean utilization is rejected, not read as 1 %")
    func booleanPercentageRejected() {
        #expect(UsageParser.number(true as Any) == nil)
        #expect(UsageParser.number(false as Any) == nil)
        #expect(UsageParser.number(76 as Any) == 76)
        #expect(UsageParser.number(76.4 as Any) == 76.4)

        let line = envelope(rateLimits: #"{"five_hour":{"utilization":true,"resets_at":"2026-09-18T16:10:00.1+00:00"}}"#)
        #expect(UsageParser.parse(jsonLine: line).snapshot == nil)
    }

    /// The same bridge makes `{"request_id": true}` stringify to "1", which would satisfy a
    /// caller waiting on request 1 and hand it an unrelated payload.
    @Test("a boolean request_id never matches a pending request")
    func booleanRequestIdRejected() {
        #expect(ControlChannel.requestId(from: true) == nil)
        #expect(ControlChannel.requestId(from: false) == nil)
        #expect(ControlChannel.requestId(from: "7") == "7")
        #expect(ControlChannel.requestId(from: 7 as NSNumber) == "7")
        #expect(ControlChannel.requestId(from: "") == nil)
        #expect(ControlChannel.requestId(from: nil) == nil)
    }

    // MARK: - Parser: ambiguity must not become a number

    @Test("duplicate limits[] kinds fall through instead of picking the first")
    func duplicateKindsFallThrough() {
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"R1"},
         "seven_day":{"utilization":41,"resets_at":"R2"},
         "limits":[{"kind":"session","percent":76,"resets_at":"R1"},
                   {"kind":"session","percent":12,"resets_at":"R1"},
                   {"kind":"weekly_all","percent":41,"resets_at":"R2"}]}
        """)
        let snapshot = try! #require(UsageParser.parse(jsonLine: line).snapshot)
        #expect(snapshot.source == .exactKeys)      // limits[] was ambiguous, so it was not used
        #expect(snapshot.sessionUtilization == 76)
    }

    /// A reset that only one encoding carries must survive. Preferring `limits[]` wholesale used
    /// to drop it, which makes a live window look closed and costs the forecast its clock.
    @Test("a reset present only in the exact keys is merged into the limits[] reading")
    func limitsMissingResetIsCompletedFromExactKeys() {
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"R1"},
         "limits":[{"kind":"session","percent":76}]}
        """)
        let snapshot = try! #require(UsageParser.parse(jsonLine: line).snapshot)
        #expect(snapshot.sessionResetsAt == "R1")
        #expect(snapshot.sessionIsActive, "a reset in either encoding means the window is open")
        #expect(snapshot.sessionUtilization == 76)
    }

    /// Two encodings of one number that disagree mean the response is in a state this parser does
    /// not understand. Silence beats a wrong verdict.
    @Test("limits[] disagreeing with the exact keys yields no snapshot")
    func crossCheckDisagreement() {
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"R1"},
         "seven_day":{"utilization":41,"resets_at":"R2"},
         "limits":[{"kind":"session","percent":12,"resets_at":"R1"},
                   {"kind":"weekly_all","percent":41,"resets_at":"R2"}]}
        """)
        let result = UsageParser.parse(jsonLine: line)
        #expect(result.snapshot == nil)
        #expect(result.error?.contains("disagree") == true)
    }

    @Test("a differing reset fingerprint also counts as disagreement")
    func crossCheckFingerprintDisagreement() {
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
         "limits":[{"kind":"session","percent":76,"resets_at":"2026-09-18T16:10:00.999999+00:00"}]}
        """)
        #expect(UsageParser.parse(jsonLine: line).snapshot == nil)
    }

    /// `JSONSerialization` returns an unordered dictionary, so the Windows parser's
    /// "first hit in wire order" tie-break cannot be reproduced. With two live unqualified
    /// candidates there is no principled choice — so it refuses rather than picking one.
    @Test("an ambiguous substring fallback yields no snapshot")
    func ambiguousSubstringFallback() {
        let line = envelope(rateLimits: """
        {"five_hour_v2":{"utilization":76,"resets_at":"R1"},
         "seven_day_v2":{"utilization":41,"resets_at":"R2"},
         "seven_day_cowork":{"utilization":3,"resets_at":"R3"}}
        """)
        let result = UsageParser.parse(jsonLine: line)
        #expect(result.snapshot == nil)
        #expect(result.error?.contains("ambiguous") == true)
    }

    @Test("an unambiguous substring fallback still works, and says so")
    func unambiguousSubstringFallback() {
        let line = envelope(rateLimits: """
        {"five_hour_v2":{"utilization":76,"resets_at":"R1"},
         "seven_day_v2":{"utilization":41,"resets_at":"R2"}}
        """)
        let result = UsageParser.parse(jsonLine: line)
        let snapshot = try! #require(result.snapshot)
        #expect(snapshot.source == .substringSearch)
        #expect(result.error != nil)
    }

    @Test("an out-of-range weekly does not travel inside a good session snapshot")
    func weeklyRangeCheckedAtTheBoundary() {
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"R1"},
         "seven_day":{"utilization":940,"resets_at":"R2"}}
        """)
        let snapshot = try! #require(UsageParser.parse(jsonLine: line).snapshot)
        #expect(snapshot.sessionUtilization == 76)
        #expect(snapshot.weeklyUtilization == nil)
        #expect(snapshot.hasValidWeekly == false)
    }

    // MARK: - Helpers

    private func envelope(rateLimits: String) -> String {
        """
        {"type":"control_response","response":{"request_id":"1","subtype":"success","response":{
          "rate_limits":\(rateLimits),"rate_limits_available":true,"subscription_type":"max"}}}
        """
    }

    private func temporaryDirectory() -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("cqc-\(UUID().uuidString)")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    /// A stand-in child: `/bin/sh -c <script>`, launched through exactly the same spawn path as
    /// the real `claude`, so these tests exercise the production code rather than a copy of it.
    private func fakeChildSpec(script: String) -> ChildProcessSpec {
        ChildProcessSpec(
            resolveExecutablePath: { "/bin/sh" },
            arguments: ["-c", script],
            workingDirectory: NSTemporaryDirectory(),
            pidFileURL: nil)
    }
}

/// The bug that put an exclamation mark in the menu bar for five hours on a healthy account
/// (2026-09-19). Found by running the real app, not by any test.
@Suite("Inactive window")
struct InactiveWindowTests {

    /// Verbatim shape of a real reply when the 5 h window has simply expired: nothing has been
    /// used for five hours, so there is no open session and therefore no reset time.
    private let inactiveSession = """
    {"five_hour":{"utilization":0,"resets_at":null,"limit_dollars":null,"locked_reason":null},
     "seven_day":{"utilization":55,"resets_at":"2026-09-20T06:00:00.273321+00:00"},
     "limits":[{"kind":"session","group":"session","percent":0,"resets_at":null,"is_active":false},
               {"kind":"weekly_all","group":"weekly","percent":55,
                "resets_at":"2026-09-20T06:00:00.273321+00:00","is_active":true}]}
    """

    private func envelope(_ rateLimits: String) -> String {
        """
        {"type":"control_response","response":{"request_id":"1","subtype":"success","response":{
          "rate_limits":\(rateLimits),"rate_limits_available":true,"subscription_type":"team"}}}
        """
    }

    @Test("an expired session yields a snapshot, not a parse failure")
    func expiredSessionIsReadable() throws {
        let result = UsageParser.parse(jsonLine: envelope(inactiveSession))
        let snapshot = try #require(result.snapshot, "an inactive window must not fail to parse")
        #expect(result.error == nil)
        #expect(snapshot.sessionUtilization == 0)
        #expect(snapshot.sessionResetsAt == nil)
        #expect(snapshot.sessionIsActive == false)
        #expect(snapshot.weeklyUtilization == 55)
        #expect(snapshot.weeklyIsActive == true)
    }

    /// The distinction the icon depends on: the reading is *usable* (so show 0 %), but it is not
    /// *forecastable* (so no verdict), and those are different questions.
    @Test("an inactive window is usable but not forecastable")
    func usableButNotForecastable() throws {
        let snapshot = try #require(UsageParser.parse(jsonLine: envelope(inactiveSession)).snapshot)
        #expect(snapshot.hasValidSession)
        #expect(snapshot.sessionIsForecastable == false)
        #expect(snapshot.weeklyIsForecastable)
    }

    @Test("the exact-key path handles a null reset too")
    func exactKeysHandleNullReset() throws {
        let noLimitsArray = """
        {"five_hour":{"utilization":0,"resets_at":null},
         "seven_day":{"utilization":55,"resets_at":"2026-09-20T06:00:00.273321+00:00"}}
        """
        let snapshot = try #require(UsageParser.parse(jsonLine: envelope(noLimitsArray)).snapshot)
        #expect(snapshot.source == .exactKeys)
        #expect(snapshot.sessionIsActive == false)
        #expect(snapshot.weeklyUtilization == 55)
    }

    /// Both encodings agreeing that the window is closed must not read as a disagreement.
    @Test("two null resets are not a cross-check disagreement")
    func nullResetsAgree() throws {
        let snapshot = try #require(UsageParser.parse(jsonLine: envelope(inactiveSession)).snapshot)
        #expect(snapshot.source == .limitsArray)
    }

    /// An invalid *percentage* must still be rejected — the fix loosened the reset requirement,
    /// not the value check.
    @Test("a null reset does not excuse an invalid percentage")
    func invalidPercentageStillRejected() {
        let bad = """
        {"five_hour":{"utilization":940,"resets_at":null},
         "limits":[{"kind":"session","percent":940,"resets_at":null,"is_active":false}]}
        """
        #expect(UsageParser.parse(jsonLine: envelope(bad)).snapshot == nil)
    }
}
