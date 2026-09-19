import Foundation
import Testing

@testable import ClaudeQuotaCore

/// Ported from the C# model suite. Where the spec states an exact number, that number is the
/// oracle — not a plausibility range — so the two implementations cannot quietly diverge.
@Suite("Forecast math")
struct ForecastMathTests {

    /// docs/forecast-and-states.md's worked example: P = 56, E = 132, t_reset = 168 (W = 300)
    /// → r ≈ 0.424 %/min, shortfall ≈ 64 min → DryEarly. It must also leave Measuring
    /// immediately, since this is the very first sample and there is nothing to debounce.
    @Test("the spec's worked example is DryEarly immediately")
    func workedExample() throws {
        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        let resetsAt = utc("2026-01-01T05:00:00.000000+00:00")
        let utcNow = resetsAt.addingTimeInterval(-168 * 60)   // t_reset = 168 ⇒ E = 300 − 168 = 132

        tracker.ingest(pct: 56.0, resetsAtRaw: wireFormat(resetsAt), utcNow: utcNow, monoMs: 0)

        let snap = tracker.computeSnapshot(utcNow: utcNow, monoMs: 0, isWeekly: false)
        let forecast = try #require(snap.forecast)

        #expect(abs(forecast.ratePctPerMin - 0.424) < 0.0005, "rate was \(forecast.ratePctPerMin)")
        #expect(abs(forecast.shortfallMinutes - 64.0) < 0.5, "shortfall was \(forecast.shortfallMinutes)")
        #expect(!snap.refused)

        let raw = RawStateClassifier.classify(usedPct: 56.0, forecast: forecast,
                                              windowMinutes: QuotaWindows.sessionMinutes, refused: false)
        #expect(raw == .dryEarly)

        let hold = HysteresisHold(weekly: false)
        #expect(hold.step(raw, monoMs: 0) == .dryEarly, "Measuring → verdict must bypass hysteresis")
    }

    /// The spec again: "At 60 % burning 0.6 %/min with 4 h left, the shortfall is ~173 min, so
    /// that's DryEarly."
    @Test("60 % at 0.6 %/min with four hours left is DryEarly with a ~173 min shortfall")
    func sixtyPercentBurning() {
        let forecast = ForecastMath.evaluate(
            usedPct: 60.0, elapsedMinutes: QuotaWindows.sessionMinutes - 240,
            ratePctPerMin: 0.6, windowMinutes: QuotaWindows.sessionMinutes, minutesToReset: 240)

        #expect(abs(forecast.shortfallMinutes - 173.0) < 0.5, "shortfall was \(forecast.shortfallMinutes)")
        #expect(RawStateClassifier.classify(usedPct: 60.0, forecast: forecast,
                                            windowMinutes: QuotaWindows.sessionMinutes,
                                            refused: false) == .dryEarly)
    }

    @Test("a zero rate makes depletion infinite rather than dividing by zero")
    func zeroRate() {
        let forecast = ForecastMath.evaluate(usedPct: 10, elapsedMinutes: 60, ratePctPerMin: 0,
                                             windowMinutes: 300, minutesToReset: 240)
        #expect(forecast.minutesToDeplete.isInfinite)
        #expect(forecast.shortfallMinutes == 0)
        #expect(forecast.projectedPctAtReset == 10)
    }

    @Test("projected is uncapped, so a renderer can see it exceed 100")
    func projectedIsUncapped() {
        let forecast = ForecastMath.evaluate(usedPct: 90, elapsedMinutes: 60, ratePctPerMin: 1.0,
                                             windowMinutes: 300, minutesToReset: 100)
        #expect(forecast.projectedPctAtReset == 190)
    }
}

@Suite("State table")
struct StateTableTests {

    private func forecast(shortfall: Double, slack: Double, remaining: Double) -> ForecastResult {
        ForecastResult(usedPct: 100 - remaining, elapsedMinutes: 0, ratePctPerMin: 0,
                       remainingPct: remaining, minutesToReset: 0, minutesToDeplete: 0,
                       shortfallMinutes: shortfall, slackMinutes: slack,
                       projectedPctAtReset: 0, pace: 0)
    }

    /// Decision 15: Spent only at P >= 100 — confirmed exhaustion, not "almost". 97–99.9 % still
    /// reaches DryEarly via the remaining <= 3 clause, so nothing in that range goes unflagged.
    @Test("Spent requires confirmed exhaustion, and 99.5 % is DryEarly instead")
    func spentRequiresHundred() {
        let f = forecast(shortfall: 0, slack: 999, remaining: 0.5)
        #expect(RawStateClassifier.classify(usedPct: 100.0, forecast: f, windowMinutes: 300, refused: false) == .spent)
        #expect(RawStateClassifier.classify(usedPct: 99.5, forecast: f, windowMinutes: 300, refused: false) == .dryEarly)
    }

    @Test("Spent outranks the refusal rule")
    func spentOutranksRefusal() {
        let f = forecast(shortfall: 0, slack: 999, remaining: 0)
        #expect(RawStateClassifier.classify(usedPct: 100, forecast: f, windowMinutes: 300, refused: true) == .spent)
    }

    @Test("the session thresholds are 15 / 24 minutes")
    func sessionThresholds() {
        // S_lo = max(0.05·300, 10) = 15; S_hi = max(0.08·300, 15) = 24.
        let justUnder = forecast(shortfall: 14.9, slack: 100, remaining: 50)
        #expect(RawStateClassifier.classify(usedPct: 50, forecast: justUnder, windowMinutes: 300, refused: false) == .tight)
        let atThreshold = forecast(shortfall: 15.0, slack: 100, remaining: 50)
        #expect(RawStateClassifier.classify(usedPct: 50, forecast: atThreshold, windowMinutes: 300, refused: false) == .dryEarly)

        let slackAtHi = forecast(shortfall: 0, slack: 24.0, remaining: 50)
        #expect(RawStateClassifier.classify(usedPct: 50, forecast: slackAtHi, windowMinutes: 300, refused: false) == .tight)
        let slackAbove = forecast(shortfall: 0, slack: 24.1, remaining: 50)
        #expect(RawStateClassifier.classify(usedPct: 50, forecast: slackAbove, windowMinutes: 300, refused: false) == .safe)
    }

    @Test("remaining alone can reach Tight and DryEarly")
    func remainingClauses() {
        let comfortable = forecast(shortfall: 0, slack: 999, remaining: 10)
        #expect(RawStateClassifier.classify(usedPct: 90, forecast: comfortable, windowMinutes: 300, refused: false) == .tight)
        let nearlyGone = forecast(shortfall: 0, slack: 999, remaining: 3)
        #expect(RawStateClassifier.classify(usedPct: 97, forecast: nearlyGone, windowMinutes: 300, refused: false) == .dryEarly)
    }

    /// Decision 2: grace may only ever LOWER severity, and never to Safe — if the forecast says
    /// depletion precedes the reset, the worst grace may do is quiet DryEarly to Tight.
    @Test("grace caps DryEarly at Tight near a reset and never produces Safe")
    func graceNeverProducesSafe() {
        #expect(GraceCap.apply(.dryEarly, minutesToReset: 14) == .tight)
        #expect(GraceCap.apply(.tight, minutesToReset: 14) == .tight)
        #expect(GraceCap.apply(.safe, minutesToReset: 14) == .safe, "a cap must never raise severity")
        #expect(GraceCap.apply(.spent, minutesToReset: 1) == .spent, "Spent is never capped")
        #expect(GraceCap.apply(.dryEarly, minutesToReset: 16) == .dryEarly, "outside the grace window")
    }
}

@Suite("Hysteresis")
struct HysteresisTests {

    @Test("entering and leaving Measuring commits immediately")
    func measuringBypasses() {
        let hold = HysteresisHold(weekly: false)
        #expect(hold.step(.safe, monoMs: 0) == .safe)          // leaving Measuring
        #expect(hold.step(.measuring, monoMs: 1000) == .measuring)  // entering it again
    }

    @Test("Spent commits immediately in either direction")
    func spentBypasses() {
        let hold = HysteresisHold(weekly: false)
        hold.step(.safe, monoMs: 0)
        #expect(hold.step(.spent, monoMs: 1000) == .spent)
        #expect(hold.step(.tight, monoMs: 2000) == .tight, "leaving Spent also commits immediately")
    }

    /// Decision 11: escalation commits to the LOWEST severity still above committed, seen across
    /// the last 2 accepted observations — NOT exact repeated agreement. Two different-but-both-
    /// escalating raw states count together; an exact-match rule could stall on the old state
    /// indefinitely while raw alternated between two higher ones.
    @Test("alternating Tight/DryEarly escalates to the lower of the two")
    func alternatingEscalates() {
        let hold = HysteresisHold(weekly: false)
        hold.step(.safe, monoMs: 0)
        #expect(hold.committed == .safe)

        hold.step(.tight, monoMs: 100_000)
        #expect(hold.committed == .safe, "one observation is not yet evidence")
        hold.step(.dryEarly, monoMs: 200_000)
        #expect(hold.committed == .tight, "commits to the LOWEST of the two escalating states")
    }

    @Test("de-escalation needs three agreeing observations AND ten real minutes")
    func deescalationNeedsEvidenceAndTime() {
        let hold = HysteresisHold(weekly: false)
        hold.step(.dryEarly, monoMs: 0)
        #expect(hold.committed == .dryEarly)

        // Three agreeing observations, but only two minutes apart: not enough time.
        hold.step(.safe, monoMs: 60_000)
        hold.step(.safe, monoMs: 90_000)
        hold.step(.safe, monoMs: 120_000)
        #expect(hold.committed == .dryEarly, "de-escalated without the 10-minute minimum")

        // Past ten minutes from the first of them.
        hold.step(.safe, monoMs: 700_000)
        #expect(hold.committed == .safe)
    }

    @Test("a cooldown limits committed changes to one per 90 s")
    func cooldown() {
        let hold = HysteresisHold(weekly: false)
        hold.step(.safe, monoMs: 0)
        hold.step(.tight, monoMs: 1000)
        hold.step(.tight, monoMs: 2000)
        #expect(hold.committed == .safe, "changed again inside the 90 s cooldown")
        hold.step(.tight, monoMs: 100_000)
        hold.step(.tight, monoMs: 101_000)
        #expect(hold.committed == .tight)
    }

    @Test("the weekly window needs four observations to escalate, not two")
    func weeklyNeedsMoreEvidence() {
        let hold = HysteresisHold(weekly: true)
        hold.step(.safe, monoMs: 0)
        for step in 1...3 { hold.step(.tight, monoMs: Int64(step) * 400_000) }
        #expect(hold.committed == .safe, "escalated on fewer than four observations")
        hold.step(.tight, monoMs: 1_600_000)
        #expect(hold.committed == .tight)
    }

    @Test("commitFromMeasuring only fires while committed is Measuring")
    func commitFromMeasuringIsNarrow() {
        let hold = HysteresisHold(weekly: false)
        #expect(hold.commitFromMeasuring(.safe, monoMs: 0) == .safe)
        // Now that it holds a verdict, it must not be used to bypass hysteresis again.
        #expect(hold.commitFromMeasuring(.dryEarly, monoMs: 1000) == .safe)
    }
}

@Suite("Freshness ladder")
struct FreshnessLadderTests {
    static let now = utc("2026-01-01T12:00:00.000000+00:00")
    static let nowMono: Int64 = 10_000_000

    private func baseline(
        hasEverSucceeded: Bool = true,
        staleAtMono: Int64? = nowMono + 90_000,
        unknownAtMono: Int64? = nowMono + 300_000,
        lastAcceptedMono: Int64? = nowMono,
        sessionResetsAt: Date? = now.addingTimeInterval(2 * 3600),
        weeklyResetsAt: Date? = now.addingTimeInterval(2 * 86_400),
        clockDiscontinuity: Bool = false
    ) -> FreshnessInputs {
        FreshnessInputs(hasEverSucceeded: hasEverSucceeded, staleAtMono: staleAtMono,
                        unknownAtMono: unknownAtMono, lastAcceptedMono: lastAcceptedMono,
                        sessionResetsAt: sessionResetsAt, weeklyResetsAt: weeklyResetsAt,
                        clockDiscontinuity: clockDiscontinuity)
    }

    @Test("Unknown before the first success")
    func unknownBeforeFirstSuccess() {
        let i = baseline(hasEverSucceeded: false, staleAtMono: nil, unknownAtMono: nil, lastAcceptedMono: nil)
        #expect(FreshnessOracle.evaluate(i, utcNow: Self.now, monoMs: Self.nowMono) == .unknown)
    }

    @Test("Live in the baseline case")
    func liveBaseline() {
        #expect(FreshnessOracle.evaluate(baseline(), utcNow: Self.now, monoMs: Self.nowMono) == .live)
    }

    @Test("Stale when the fingerprint has not changed for 21 minutes")
    func staleOnUnchangedFingerprint() {
        let i = baseline(lastAcceptedMono: Self.nowMono - 21 * 60 * 1000)
        #expect(FreshnessOracle.evaluate(i, utcNow: Self.now, monoMs: Self.nowMono) == .stale)
    }

    @Test("Stale once now is past resets_at + 60 s, for either window")
    func staleAfterReset() {
        let session = baseline(sessionResetsAt: Self.now.addingTimeInterval(-61))
        #expect(FreshnessOracle.evaluate(session, utcNow: Self.now, monoMs: Self.nowMono) == .stale)
        let weekly = baseline(weeklyResetsAt: Self.now.addingTimeInterval(-61))
        #expect(FreshnessOracle.evaluate(weekly, utcNow: Self.now, monoMs: Self.nowMono) == .stale)
    }

    @Test("the frozen deadlines drive Stale and then Unknown")
    func frozenDeadlines() {
        let i = baseline()
        #expect(FreshnessOracle.evaluate(i, utcNow: Self.now, monoMs: Self.nowMono + 90_001) == .stale)
        #expect(FreshnessOracle.evaluate(i, utcNow: Self.now, monoMs: Self.nowMono + 300_001) == .unknown)
    }

    /// Decision 6: a clock jump drops to Stale, not Unknown — we DO have real data, just an
    /// untrustworthy clock.
    @Test("a clock discontinuity forces Stale, not Unknown")
    func clockJumpIsStale() {
        let i = baseline(clockDiscontinuity: true)
        #expect(FreshnessOracle.evaluate(i, utcNow: Self.now, monoMs: Self.nowMono) == .stale)
    }
}
