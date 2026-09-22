import Foundation
import Testing

@testable import ClaudeQuotaCore

/// The poll-cadence table and the clock guard, end to end through `QuotaModel`. Each test names
/// the real failure it prevents — these are the round-1/round-2 regressions the Windows
/// implementation paid for, and a fresh port is exactly where they would come back.
@Suite("Poll cadence")
struct PollCadenceTests {
    static let base = utc("2026-01-01T12:00:00.000000+00:00")

    private func snapshot(sessionPct: Double, resetsAt: Date, weeklyPct: Double? = nil,
                          weeklyResetsAt: Date? = nil) -> UsageSnapshot {
        UsageSnapshot.make(session: sessionPct, sessionResets: wireFormat(resetsAt),
                           weekly: weeklyPct, weeklyResets: weeklyResetsAt.map(wireFormat))
    }

    @Test("idle polling is 150 s")
    func idleCadence() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(4 * 3600)
        model.ingest(snapshot(sessionPct: 20, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)
        // A rise is what makes it "burning"; the first sample counts as one, so step past the
        // 10-minute burning window.
        let later = Self.base.addingTimeInterval(11 * 60)
        _ = model.evaluate(utcNow: later, monoMs: 11 * 60 * 1000)
        #expect(model.nextPollDelay(utcNow: later) == QuotaModel.idleInterval)
    }

    @Test("a rise within the last 10 minutes polls at 30 s")
    func burningCadence() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(4 * 3600)
        model.ingest(snapshot(sessionPct: 20, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)
        _ = model.evaluate(utcNow: Self.base, monoMs: 0)
        #expect(model.nextPollDelay(utcNow: Self.base) == QuotaModel.burningInterval)
    }

    /// Decision 4 (round 1): the hot loop. Capping down to an already-past deadline forever is
    /// what used to turn into an endless 1 s poll loop after a reset whose new window had not
    /// shown up in cached data yet.
    @Test("a passed deadline stops capping and settles at the awaiting-reset cadence")
    func noEndlessOneSecondLoop() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(60)
        model.ingest(snapshot(sessionPct: 20, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)

        // Before the deadline: capped down to land one poll on reset + 3 s.
        let beforeDelay = model.nextPollDelay(utcNow: Self.base)
        #expect(beforeDelay > 0 && beforeDelay <= 63, "expected a cap to reset+3s, got \(beforeDelay)")

        // After it: 30 s awaiting-reset, never 1 s forever.
        let after = resetsAt.addingTimeInterval(30)
        _ = model.evaluate(utcNow: after, monoMs: 90_000)
        #expect(model.nextPollDelay(utcNow: after) == QuotaModel.awaitingResetInterval)
    }

    @Test("the poll delay is always positive")
    func delayAlwaysPositive() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(1)
        model.ingest(snapshot(sessionPct: 20, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)
        for offset in [0.0, 0.5, 1.0, 2.0, 3.0, 4.0, 60.0] {
            let now = Self.base.addingTimeInterval(offset)
            #expect(model.nextPollDelay(utcNow: now) >= QuotaModel.minPollInterval)
        }
    }

    /// Round-2 decision 8: awaiting-reset is checked BEFORE Spent. Checking Spent first used to
    /// pin recovery polling at 300 s for up to five minutes after a reset that would have
    /// cleared the block.
    @Test("an expired Spent window recovers at 30 s, not the 300 s Spent cadence")
    func expiredSpentRecoversFast() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(600)
        model.ingest(snapshot(sessionPct: 100, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)
        _ = model.evaluate(utcNow: Self.base, monoMs: 0)

        // Still current and Spent: the slow cadence is correct here, subject to the reset cap.
        #expect(model.nextPollDelay(utcNow: Self.base) <= QuotaModel.spentInterval)

        // Past its own deadline: must switch to the fast recovery cadence.
        let after = resetsAt.addingTimeInterval(10)
        _ = model.evaluate(utcNow: after, monoMs: 610_000)
        #expect(model.nextPollDelay(utcNow: after) == QuotaModel.awaitingResetInterval)
    }

    @Test("failures back off 60 → 120 → 240 → 480 → 600 and cap there")
    func failureBackoff() {
        let model = QuotaModel()
        var expected: [TimeInterval] = [60, 120, 240, 480, 600, 600]
        for (index, want) in expected.enumerated() {
            model.ingestFailure("boom", utcNow: Self.base, monoMs: Int64(index) * 1000)
            #expect(model.nextPollDelay(utcNow: Self.base) == want,
                    "failure \(index + 1) gave \(model.nextPollDelay(utcNow: Self.base)), wanted \(want)")
        }
        expected.removeAll()
    }

    /// Decision 4: a past deadline must never override a failure's backoff interval.
    @Test("a past deadline does not override failure backoff")
    func pastDeadlineDoesNotOverrideBackoff() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(60)
        model.ingest(snapshot(sessionPct: 20, resetsAt: resetsAt), utcNow: Self.base, monoMs: 0)
        let after = resetsAt.addingTimeInterval(120)
        model.ingestFailure("boom", utcNow: after, monoMs: 180_000)
        #expect(model.nextPollDelay(utcNow: after) == 60, "backoff was overridden by the stale deadline")
    }
}

@Suite("Clock guard")
struct ClockGuardTests {
    static let base = utc("2026-01-01T12:00:00.000000+00:00")

    /// Decision 6: if UTC and monotonic time disagree by more than 60 s, the clock jumped
    /// (sleep, manual change, VM pause). Verdicts are forced to Measuring — bypassing grace
    /// entirely, so a reset that only *appears* close cannot be read as Safe — and freshness
    /// drops to Stale until the next accepted sample re-anchors.
    @Test("a UTC jump forces Measuring and Stale, in both directions")
    func clockJumpForcesMeasuring() throws {
        for jump in [200.0, -200.0] {
            let model = QuotaModel()
            let resetsAt = Self.base.addingTimeInterval(168 * 60)
            model.ingest(UsageSnapshot.make(session: 56, sessionResets: wireFormat(resetsAt)),
                         utcNow: Self.base, monoMs: 0)

            let healthy = model.evaluate(utcNow: Self.base, monoMs: 0)
            #expect(healthy.session.state != .measuring, "setup: expected a real verdict first")

            // Monotonic time advanced 10 s; UTC claims something very different.
            let jumped = model.evaluate(utcNow: Self.base.addingTimeInterval(10 + jump), monoMs: 10_000)
            #expect(jumped.session.state == .measuring, "jump of \(jump) s was not detected")
            #expect(jumped.session.measuringReason == "Klockan ändrades, mäter om…")
            #expect(jumped.freshness == .stale)
            // Decision 12: every forecast field is nil while Measuring.
            #expect(jumped.session.ratePctPerMin == nil)
            #expect(jumped.session.projectedPctAtReset == nil)
            #expect(jumped.session.depletesAt == nil)
        }
    }

    @Test("a consistent clock is not a jump")
    func consistentClockIsFine() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(168 * 60)
        model.ingest(UsageSnapshot.make(session: 56, sessionResets: wireFormat(resetsAt)),
                     utcNow: Self.base, monoMs: 0)
        let view = model.evaluate(utcNow: Self.base.addingTimeInterval(30), monoMs: 30_000)
        #expect(view.session.state != .measuring)
    }
}

@Suite("Refusal rules")
struct RefusalRuleTests {
    static let base = utc("2026-01-01T12:00:00.000000+00:00")

    /// The weekly window refuses until a full day/night cycle has elapsed. A Friday-morning-only
    /// pace extrapolated straight through the coming nights and weekend fired a false DryEarly at
    /// ~11 % used, ~340 min after a 07:00 reset — just past the old W/30 line.
    @Test("weekly refuses below 24 h elapsed, with its own reason")
    func weeklyRefusesBeforeADay() throws {
        let tracker = WindowTracker(windowMinutes: QuotaWindows.weeklyMinutes,
                                    halfLifeMinutes: 8 * 60, isWeekly: true)
        // E = 342 min: past W/30 (336) but far short of 24 h.
        let resetsAt = Self.base.addingTimeInterval((QuotaWindows.weeklyMinutes - 342) * 60)
        tracker.ingest(pct: 11, resetsAtRaw: wireFormat(resetsAt), utcNow: Self.base, monoMs: 0)

        let snap = tracker.computeSnapshot(utcNow: Self.base, monoMs: 0, isWeekly: true)
        #expect(snap.refused)
        #expect(snap.reasonCode == .tooEarlyInWeek)
        #expect(QuotaModel.reasonText(.tooEarlyInWeek) == "För tidigt i veckan — väntar på ett helt dygn")
    }

    @Test("a window under 3 % used refuses with too-little-usage")
    func tooLittleUsage() {
        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        let resetsAt = Self.base.addingTimeInterval(168 * 60)
        tracker.ingest(pct: 2, resetsAtRaw: wireFormat(resetsAt), utcNow: Self.base, monoMs: 0)
        let snap = tracker.computeSnapshot(utcNow: Self.base, monoMs: 0, isWeekly: false)
        #expect(snap.refused)
        #expect(snap.reasonCode == .tooLittleUsage)
    }

    /// Decision 13: a window missing from an otherwise successful poll is "data saknas", never
    /// silently treated as 0 %. Global Live also requires a valid session window.
    @Test("a missing weekly window reads as data missing, not 0 %")
    func missingWeeklyIsDataMissing() throws {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(168 * 60)
        model.ingest(UsageSnapshot.make(session: 56, sessionResets: wireFormat(resetsAt)),
                     utcNow: Self.base, monoMs: 0)
        let view = model.evaluate(utcNow: Self.base, monoMs: 0)
        #expect(view.weekly.state == .measuring)
        #expect(view.weekly.measuringReason == "Data saknas")
        #expect(view.weekly.usedPct == nil)
    }

    @Test("an invalid session percentage loses both the verdict and Live")
    func invalidSessionLosesLive() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(168 * 60)
        model.ingest(UsageSnapshot.make(session: 56, sessionResets: wireFormat(resetsAt)),
                     utcNow: Self.base, monoMs: 0)
        #expect(model.evaluate(utcNow: Self.base, monoMs: 0).freshness == .live)

        // Round-2 decision 3: session validity requires a valid percentage, exactly like weekly.
        model.ingest(UsageSnapshot.make(session: 940, sessionResets: wireFormat(resetsAt)),
                     utcNow: Self.base.addingTimeInterval(30), monoMs: 30_000)
        let view = model.evaluate(utcNow: Self.base.addingTimeInterval(30), monoMs: 30_000)
        #expect(view.session.state == .measuring)
        #expect(view.freshness != .live)
    }
}

/// The bug the real app showed: a committed DryEarly held by hysteresis, whose CURRENT forecast
/// has no shortfall, rendered as *"⚠ Kvoten tar slut sön kl 00:52"* over *"0 s före reset kl
/// 23:20"* — a depletion after the reset, paired with a shortfall of zero.
@Suite("No-shortfall clamp")
struct NoShortfallClampTests {

    private func forecast(shortfall: Double, remaining: Double) -> ForecastResult {
        ForecastResult(usedPct: 100 - remaining, elapsedMinutes: 80, ratePctPerMin: 0.2,
                       remainingPct: remaining, minutesToReset: 218, minutesToDeplete: 311,
                       shortfallMinutes: shortfall, slackMinutes: 93,
                       projectedPctAtReset: 68, pace: 0.6)
    }

    @Test("a DryEarly with no shortfall presents as Tight, never as Safe")
    func clampsToTight() {
        let f = forecast(shortfall: 0, remaining: 76)
        #expect(GraceCap.applyNoShortfall(.dryEarly, forecast: f) == .tight)
    }

    @Test("a real shortfall leaves DryEarly untouched")
    func realShortfallSurvives() {
        #expect(GraceCap.applyNoShortfall(.dryEarly, forecast: forecast(shortfall: 64, remaining: 44)) == .dryEarly)
        // Even a small one: the clamp is for "none at all", not "not much".
        #expect(GraceCap.applyNoShortfall(.dryEarly, forecast: forecast(shortfall: 1.5, remaining: 44)) == .dryEarly)
    }

    /// `rem <= 3` is DryEarly's other trigger and has nothing to do with shortfall, so the clamp
    /// must not quietly downgrade a nearly-exhausted window.
    @Test("three percent or less remaining stays DryEarly even with no shortfall")
    func nearlyExhaustedStaysDryEarly() {
        #expect(GraceCap.applyNoShortfall(.dryEarly, forecast: forecast(shortfall: 0, remaining: 3)) == .dryEarly)
    }

    @Test("the clamp touches nothing but DryEarly")
    func onlyDryEarly() {
        let f = forecast(shortfall: 0, remaining: 76)
        for state in [QuotaState.measuring, .safe, .tight, .spent] {
            #expect(GraceCap.applyNoShortfall(state, forecast: f) == state)
        }
    }

    /// End to end: the panel must never print a zero shortfall as if it were a real one.
    @Test("the status box stops claiming a shortfall that is not there")
    func statusBoxNoLongerClaimsZeroShortfall() throws {
        let now = utc("2026-09-19T21:42:00+00:00")
        let tz = TimeZone(secondsFromGMT: 3600)!
        // The real numbers from the screenshot: 24 % used, reset in 3 h 38 min, depletion AFTER it.
        let session = WindowView(kind: .session, windowMinutes: QuotaWindows.sessionMinutes,
                                 usedPct: 24, resetsAt: now.addingTimeInterval(218 * 60),
                                 state: .tight, ratePctPerMin: 0.2, paceMultiple: 0.6,
                                 projectedPctAtReset: 68,
                                 depletesAt: now.addingTimeInterval(311 * 60),
                                 shortfallMinutes: 0, measuringReason: nil)
        let weekly = WindowView(kind: .weekly, windowMinutes: QuotaWindows.weeklyMinutes,
                                usedPct: 58, resetsAt: now.addingTimeInterval(12 * 3600),
                                state: .safe, ratePctPerMin: 0.01, paceMultiple: 0.9,
                                projectedPctAtReset: 63, depletesAt: nil,
                                shortfallMinutes: 0, measuringReason: nil)
        let view = QuotaView(session: session, weekly: weekly, freshness: .live,
                             lastChangedAt: now.addingTimeInterval(-20),
                             lastPollAt: now.addingTimeInterval(-20), pollInterval: 30,
                             error: nil, iconSeverity: .tight, blockedUntil: nil)
        let box = PanelText.compose(view, now: now, timeZone: tz).statusBox
        #expect(!box.line2.hasPrefix("0 s före reset"), "still claiming a zero shortfall")
        #expect(box.line1 == "Tajt — kvoten räcker precis")
    }
}

/// The bug that put "!" on an idle account after two days: every window inactive, so nothing
/// could ever be *accepted*, so the freshness deadlines froze at the last accept and decayed to
/// Unknown — while polling succeeded every 150 s and the honest answer was "0 % used, nothing
/// running". Freshness is about whether the reading is CURRENT, not whether the tracker could
/// track it.
@Suite("Idle account freshness")
struct IdleAccountFreshnessTests {
    static let base = utc("2026-09-21T10:00:00.000000+00:00")

    /// Verbatim shape of the real replies: no session, no weekly, nothing consumed.
    private func idleSnapshot() -> UsageSnapshot {
        UsageSnapshot(sessionUtilization: 0, sessionResetsAt: nil,
                      weeklyUtilization: 0, weeklyResetsAt: nil,
                      subscriptionType: "team", sessionIsActive: false, weeklyIsActive: false,
                      observedAt: Self.base, source: .limitsArray)
    }

    @Test("an account whose windows are all inactive stays Live while it is polling")
    func idleAccountStaysLive() {
        let model = QuotaModel()
        model.ingest(idleSnapshot(), utcNow: Self.base, monoMs: 0)
        #expect(model.evaluate(utcNow: Self.base, monoMs: 0).freshness == .live)

        // Two days of successful polls at the idle cadence. Before the fix, freshness went
        // Unknown ~25 minutes in and never came back.
        var mono: Int64 = 0
        var now = Self.base
        for _ in 0..<1000 {
            now = now.addingTimeInterval(QuotaModel.idleInterval)
            mono += Int64(QuotaModel.idleInterval * 1000)
            model.ingest(idleSnapshot(), utcNow: now, monoMs: mono)
        }
        let view = model.evaluate(utcNow: now, monoMs: mono)
        #expect(view.freshness == .live, "an idle but healthy account decayed to \(view.freshness)")
        #expect(view.iconSeverity == .measuring, "there is no verdict to give without a window")
        #expect(view.session.usedPct == nil, "no window means no percentage to show, not 0 %")
    }

    /// The protection that motivated tying freshness to acceptance must survive: a genuinely
    /// stale channel — polls stop entirely — still decays.
    @Test("freshness still decays when the polls actually stop")
    func stoppedPollingStillDecays() {
        let model = QuotaModel()
        model.ingest(idleSnapshot(), utcNow: Self.base, monoMs: 0)
        #expect(model.evaluate(utcNow: Self.base, monoMs: 0).freshness == .live)

        // No further ingest at all — only the UI tick. 10 × the idle policy interval.
        let later = Self.base.addingTimeInterval(QuotaModel.idleInterval * 11)
        let mono = Int64(QuotaModel.idleInterval * 11 * 1000)
        #expect(model.evaluate(utcNow: later, monoMs: mono).freshness == .unknown)
    }

    /// And a cached duplicate on an ACTIVE window must still not renew freshness — that is
    /// round-2 decision 2, and widening "usable" must not have widened it away.
    @Test("a deduped duplicate on an active window does not renew freshness")
    func duplicateStillDoesNotRenew() {
        let model = QuotaModel()
        let resetsAt = Self.base.addingTimeInterval(168 * 60)
        let live = UsageSnapshot.make(session: 56, sessionResets: wireFormat(resetsAt))
        model.ingest(live, utcNow: Self.base, monoMs: 0)

        // The same fingerprint, over and over, for well past the Unknown deadline.
        var mono: Int64 = 0
        var now = Self.base
        for _ in 0..<20 {
            now = now.addingTimeInterval(QuotaModel.idleInterval)
            mono += Int64(QuotaModel.idleInterval * 1000)
            model.ingest(live, utcNow: now, monoMs: mono)
        }
        #expect(model.evaluate(utcNow: now, monoMs: mono).freshness != .live,
                "a cached duplicate renewed freshness on data that was not new")
    }

    /// A half-idle account — session closed, weekly running — is the common case after a session
    /// expires mid-week, and must behave like the fully idle one.
    @Test("a closed session with a live weekly window stays Live")
    func halfIdleStaysLive() {
        let model = QuotaModel()
        let weeklyResets = Self.base.addingTimeInterval(3 * 86_400)
        var mono: Int64 = 0
        var now = Self.base
        for step in 0..<20 {
            now = now.addingTimeInterval(QuotaModel.idleInterval)
            mono += Int64(QuotaModel.idleInterval * 1000)
            // Weekly keeps a novel fingerprint; session is closed.
            let fingerprint = wireFormat(weeklyResets.addingTimeInterval(Double(step) * 0.000017))
            model.ingest(UsageSnapshot(sessionUtilization: 0, sessionResetsAt: nil,
                                       weeklyUtilization: 17, weeklyResetsAt: fingerprint,
                                       subscriptionType: "max", sessionIsActive: false,
                                       weeklyIsActive: true, observedAt: now,
                                       source: .limitsArray),
                         utcNow: now, monoMs: mono)
        }
        #expect(model.evaluate(utcNow: now, monoMs: mono).freshness == .live)
    }
}

/// 2026-09-22: transport liveness vs. data age — the live "false stale alarm".
///
/// While the user was working the panel header turned orange and read "Datan kan vara inaktuell"
/// with nothing actually wrong. `get_usage` produces a NOVEL sample roughly every 5 min and
/// returns a byte-identical cached reply to every poll in between, but the transport deadlines
/// used to re-freeze only on a novel accepted sample — conflating "are polls succeeding" (which a
/// duplicate answers exactly as well) with "is the data moving" (which only a novel sample does).
/// At the 30 s burning cadence `stale_at` sat at last-novel + 90 s while novel samples arrived
/// ~300 s apart, so the panel read Stale for most of every refresh cycle.
///
/// Ported from the C# `QuotaModelTests` added in the same commit.
@Suite("Transport liveness vs data age")
struct TransportVsDataAgeTests {

    /// The wire format, with a jitter knob so a "novel" sample is a genuinely distinct
    /// fingerprint and a "cached" one is byte-identical.
    private func raw(_ instant: Date, jitter: Int = 0) -> String {
        wireFormat(instant.addingTimeInterval(Double(jitter) * 0.000_017))
    }

    @Test("burning cadence with a server refresh every five minutes stays Live for thirty")
    func burningCadenceStaysLive() {
        let model = QuotaModel()
        let resetsAt = utc("2026-09-22T14:00:00.000000+00:00")
        let t0 = resetsAt.addingTimeInterval(-5 * 3600)
        var pct = 10.0
        var jitter = 0
        var fingerprint = raw(resetsAt, jitter: jitter)

        model.ingest(UsageSnapshot.make(session: pct, sessionResets: fingerprint), utcNow: t0, monoMs: 0)
        #expect(model.evaluate(utcNow: t0, monoMs: 0).freshness == .live)

        var lastNovelAt = t0
        var mono: Int64 = 0
        var now = t0
        for poll in 1...60 {          // 60 × 30 s = 30 minutes of burning-cadence polling
            now = now.addingTimeInterval(30)
            mono += 30_000

            let serverRefreshed = poll % 10 == 0   // a novel sample every 5 min
            if serverRefreshed {
                pct += 1
                jitter += 1
                fingerprint = raw(resetsAt, jitter: jitter)
            }
            // Every other poll repeats the exact same fingerprint: the cached reply get_usage
            // actually returns between refreshes.
            model.ingest(UsageSnapshot.make(session: pct, sessionResets: fingerprint),
                         utcNow: now, monoMs: mono)
            if serverRefreshed { lastNovelAt = now }

            let view = model.evaluate(utcNow: now, monoMs: mono)
            #expect(view.freshness == .live, "went \(view.freshness) at poll \(poll)")
            // The header's own data age is untouched by this fix: it holds at the last NOVEL
            // sample and grows between refreshes.
            #expect(view.lastChangedAt == lastNovelAt, "data age moved on a duplicate, at poll \(poll)")
        }
    }

    @Test("nothing but cached duplicates goes Stale after twenty minutes, via the fingerprint rule")
    func duplicatesOnlyGoStaleAfterTwentyMinutes() {
        let model = QuotaModel()
        let resetsAt = utc("2026-09-22T20:00:00.000000+00:00")
        let t0 = resetsAt.addingTimeInterval(-5 * 3600)
        let fingerprint = raw(resetsAt)

        model.ingest(UsageSnapshot.make(session: 42, sessionResets: fingerprint), utcNow: t0, monoMs: 0)
        #expect(model.evaluate(utcNow: t0, monoMs: 0).freshness == .live)

        // Polled at 30 s — fast enough that, if this were only about transport liveness, the
        // 90 s/300 s deadlines would never lapse. Only the 20-minute fingerprint rule, which a
        // duplicate cannot renew, can explain going Stale here.
        var mono: Int64 = 0
        var now = t0
        for _ in 0..<42 {            // 42 × 30 s = 21 min, just past the threshold
            now = now.addingTimeInterval(30)
            mono += 30_000
            model.ingest(UsageSnapshot.make(session: 42, sessionResets: fingerprint),
                         utcNow: now, monoMs: mono)
        }
        #expect(model.evaluate(utcNow: now, monoMs: mono).freshness == .stale)
    }

    @Test("consecutive failures still go Stale at 3x and Unknown at 10x")
    func failuresUnaffected() {
        let model = QuotaModel()
        let t0 = utc("2026-09-22T08:00:00.000000+00:00")
        let farReset = t0.addingTimeInterval(5 * 3600)
        model.ingest(UsageSnapshot.make(session: 10, sessionResets: raw(farReset)), utcNow: t0, monoMs: 0)
        // Burning: cPolicy = 30 s → stale_at = t0 + 90 s, unknown_at = t0 + 300 s. A transport
        // failure never re-freezes these, exactly as before this change.
        #expect(model.evaluate(utcNow: t0.addingTimeInterval(80), monoMs: 80_000).freshness == .live)

        model.ingestFailure("boom", utcNow: t0.addingTimeInterval(95), monoMs: 95_000)
        #expect(model.evaluate(utcNow: t0.addingTimeInterval(95), monoMs: 95_000).freshness == .stale)

        model.ingestFailure("boom", utcNow: t0.addingTimeInterval(200), monoMs: 200_000)
        model.ingestFailure("boom", utcNow: t0.addingTimeInterval(310), monoMs: 310_000)
        #expect(model.evaluate(utcNow: t0.addingTimeInterval(310), monoMs: 310_000).freshness == .unknown)
    }

    @Test("a response without a valid session reading does not renew the transport deadlines")
    func invalidSessionReadingDoesNotRenew() {
        let model = QuotaModel()
        let resetsAt = utc("2026-09-22T20:00:00.000000+00:00")
        let t0 = resetsAt.addingTimeInterval(-5 * 3600)
        model.ingest(UsageSnapshot.make(session: 30, sessionResets: raw(resetsAt)), utcNow: t0, monoMs: 0)
        #expect(model.evaluate(utcNow: t0, monoMs: 0).freshness == .live)
        // Burning: stale_at = t0 + 90 s, unknown_at = t0 + 300 s.

        // A poll that round-trips successfully — this is ingest, not ingestFailure — but carries
        // an unparseable resets_at: neither a valid open reading nor a validly closed one. It
        // must be a complete no-op for the transport deadlines, exactly like a failure.
        let t1 = t0.addingTimeInterval(60)
        model.ingest(UsageSnapshot.make(session: 30, sessionResets: "not-a-timestamp"),
                     utcNow: t1, monoMs: 60_000)

        // 100 s after t0: past the ORIGINAL stale_at (90 s), proving it was never pushed out to
        // the 150 s a renewal at t1 would have produced.
        #expect(model.evaluate(utcNow: t0.addingTimeInterval(100), monoMs: 100_000).freshness == .stale)
    }

    /// Sleep across a reset: the resumed reply is a deduped copy of the OLD window, whose own
    /// `resets_at` has already passed. The duplicate DOES renew the transport deadlines now — it
    /// is a valid session reading — so transport liveness alone would read Live. But no new data
    /// has arrived in 110 real minutes, so the 20-minute fingerprint rule trips; independently
    /// the resumed instant is also past this window's `resets_at + 60 s`. Either is enough, and
    /// both fire before the transport deadlines ever could. **Stale, not Unknown.**
    @Test("sleeping across a reset reads Stale, not Unknown")
    func sleepAcrossResetIsStale() {
        let model = QuotaModel()
        let resetsAt = utc("2026-09-22T12:00:00.000000+00:00")
        let t0 = resetsAt.addingTimeInterval(-10 * 60)      // 10 min before the reset
        let fingerprint = raw(resetsAt)
        model.ingest(UsageSnapshot.make(session: 60, sessionResets: fingerprint), utcNow: t0, monoMs: 0)
        #expect(model.evaluate(utcNow: t0, monoMs: 0).freshness == .live)

        // The machine sleeps for 110 minutes and the resumed poll returns the same cached reply,
        // still describing the window that has since expired.
        let resumed = t0.addingTimeInterval(110 * 60)
        let resumedMono: Int64 = 110 * 60 * 1000
        model.ingest(UsageSnapshot.make(session: 60, sessionResets: fingerprint),
                     utcNow: resumed, monoMs: resumedMono)

        let view = model.evaluate(utcNow: resumed, monoMs: resumedMono)
        #expect(view.freshness == .stale, "expected Stale, got \(view.freshness)")
        // And the window itself is awaiting its replacement rather than presenting the old one.
        #expect(view.session.state == .measuring)
        #expect(view.session.measuringReason == "Nytt fönster väntas")
    }
}
