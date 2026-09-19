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
