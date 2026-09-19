import Foundation

/// Everything `WindowTracker` knows, evaluated fresh against `utcNow`. Never mutates state.
public struct WindowSnapshot: Sendable {
    public let resetsAt: Date?
    public let forecast: ForecastResult?
    public let refused: Bool
    public let reasonCode: MeasuringReasonCode
    public let sampleCount: Int
}

/// Per-window ingest: window-key rollover, fingerprint dedup, the monotone envelope, and the
/// ratio-EWMA rate accumulators. One instance per window; see docs/forecast-and-states.md
/// "Ingest" and "Estimator". Ported from `src/Windows/Model/WindowTracker.cs`.
///
/// `ingest` is the only mutator, and accepts or rejects a sample **atomically**: every
/// timing/validity check runs before any field is touched (review decision 7). It returns true
/// iff the sample was accepted; `QuotaModel` uses that to drive hysteresis only from genuinely
/// novel observations (decision 11), never from duplicates or rejected samples.
///
/// `computeSnapshot` is a pure read: E, t_reset, r and everything derived are recomputed on
/// every call, never accumulated ("recomputed on every use").
public final class WindowTracker {

    /// Decision 1: a deadline within this tolerance of the current one is the same window
    /// (jitter), not a rollover candidate. `CsvReplay.filterForWindow` uses the same tolerance
    /// for replay-row membership, so a row a live poll would have accepted as "the same window"
    /// is never excluded from warm start on a sub-second technicality.
    public static let jitterToleranceSeconds: Double = 120

    /// Decision 1's safety valve: an implausible later deadline seen this many *consecutive*
    /// times is accepted as a rollover anyway.
    private static let implausibleRolloverSafetyValve = 3

    /// Round-2 finding 7: the window-to-date floor decays with confirmed idle time.
    private static let floorDecayMinutes = 30.0

    /// The weekly window's own minimum elapsed time before a seed/verdict is trusted: a full
    /// day/night cycle (24 h), not W/30 (336 min). A Friday-morning-only pace read at ~5.6 h in
    /// extrapolates straight through the coming nights and weekend, and was producing false
    /// DryEarly alarms on day one of the week. Session keeps W/30.
    private static let weeklyMinElapsedMinutes = 1440.0

    public let windowMinutes: Double
    public let tau: Double
    private let isWeeklyWindow: Bool

    private var seenFingerprints: Set<String> = []

    public private(set) var windowKey: Date?          // the tracked deadline, jitter-tolerant
    public private(set) var resetsAt: Date?           // latest accepted full-precision resets_at
    public private(set) var envelopeP: Double = 0
    public private(set) var sampleCount = 0
    public private(set) var seeded = false
    public private(set) var sp: Double = 0
    public private(set) var st: Double = 0
    public private(set) var lastAcceptedUtc: Date?
    private var lastAcceptedMono: Int64?
    public private(set) var rolloverAt: Date?

    /// Monotonic anchor for the rollover gate, so it survives a UTC clock jump. Only set for a
    /// LIVE rollover — a replay-driven one has no comparable monotonic epoch.
    public private(set) var rolloverMono: Int64?

    public private(set) var lastRiseAt: Date?

    /// Monotonic time of the last accepted LIVE rise (dP > 0). Never set by replay.
    public private(set) var lastRiseMono: Int64?

    /// Round-2 decision 7: idle minutes as of the LAST ACCEPTED LIVE observation, frozen at that
    /// accept — never recomputed against the live evaluation clock. "No decay without evidence":
    /// a window with no live poll in a while must not look more relaxed just because time passed
    /// with nobody asking. Stays 0 (maximally protective) until a live sample confirms it.
    public private(set) var confirmedIdleMinutes: Double = 0

    private var pendingRolloverKey: Date?
    private var pendingRolloverCount = 0

    public init(windowMinutes: Double, halfLifeMinutes: Double, isWeekly: Bool = false) {
        self.windowMinutes = windowMinutes
        self.tau = halfLifeMinutes / log(2.0)   // half-life → tau: exp(-t/tau) = 0.5 at t = halfLife
        self.isWeeklyWindow = isWeekly
    }

    /// W/30 for session, a full 24 h for weekly.
    private var minElapsedForSeedMinutes: Double {
        isWeeklyWindow ? Self.weeklyMinElapsedMinutes : windowMinutes / 30.0
    }

    /// One poll's (or one replayed CSV row's) sample for this window.
    ///
    /// A no-op when `resetsAtRaw` is nil/unparseable, when `resets_at` is implausibly far from
    /// `utcNow` (decision 10), or when the percentage is not finite/in [0, 100] (decision 9) —
    /// ingest must never throw. `isReplay` must be true for CSV warm-start rows (decision 7):
    /// replay uses UTC-only deltas and never persists a monotonic baseline, so the live epoch
    /// after replay is always established fresh, never mixed with a previous boot's monoMs.
    ///
    /// Returns true iff the sample was accepted (mutated tracker state).
    @discardableResult
    public func ingest(pct: Double, resetsAtRaw: String?, utcNow: Date, monoMs: Int64,
                       isReplay: Bool = false) -> Bool {
        guard let parsedResetsAt = QuotaTimeUtil.parseResetsAt(resetsAtRaw) else {
            resetPendingRollover(); return false
        }
        guard QuotaTimeUtil.isPlausibleResetsAt(parsedResetsAt, now: utcNow) else {
            resetPendingRollover(); return false
        }
        guard QuotaTimeUtil.isValidPct(pct) else { resetPendingRollover(); return false }
        let fingerprint = resetsAtRaw!

        // Round-2 finding 1: classifyWindowKey itself resets pending safety-valve evidence on a
        // sameWindow or regressedIgnore outcome — an intervening non-matching reply (even one
        // that ends up fingerprint-deduped below) must break continuity, or non-consecutive
        // candidate replies can accumulate across replica-alternating polls into a destructive
        // rollover.
        let outcome = classifyWindowKey(parsedResetsAt, utcNow: utcNow)
        if outcome == .regressedIgnore || outcome == .implausibleIgnored { return false }

        let isRollover = (outcome == .rollover || outcome == .safetyValveRollover)

        // Fingerprint dedup only makes sense against the CURRENT window's set, which a rollover
        // is about to clear — so a rollover's own fingerprint can never collide.
        if !isRollover, seenFingerprints.contains(fingerprint) { return false }

        // Ordering check BEFORE any mutation (atomic accept, decision 7), and — decision 1
        // (round 2) — applied to EVERY accepted sample, rollover included: read against
        // lastAcceptedUtc/lastAcceptedMono here, BEFORE clearForRollover wipes them.
        var dtMinutes: Double?
        if let lastUtc = lastAcceptedUtc {
            let dt = isReplay
                ? utcNow.timeIntervalSince(lastUtc) / 60.0
                : computeDtMinutes(utcNow: utcNow, monoMs: monoMs)
            if dt <= 0 { return false }   // out of order / clock reorder: reject entirely
            dtMinutes = dt
        }

        // — accepted: now mutate. Clear first (it wipes the fingerprint set), THEN record this
        // sample's fingerprint — otherwise a rollover's own fingerprint would be recorded and
        // immediately erased by the clear, leaving it un-deduped afterwards.
        if isRollover {
            clearForRollover()
            rolloverAt = utcNow
            rolloverMono = isReplay ? nil : monoMs
        }
        seenFingerprints.insert(fingerprint)
        windowKey = parsedResetsAt   // freshest known deadline (decision 1: "keep the latest raw string")
        resetPendingRollover()

        resetsAt = parsedResetsAt

        let previousP = envelopeP
        envelopeP = max(envelopeP, pct)   // monotone envelope: drops are replica skew
        let dP = envelopeP - previousP
        if dP > 0 {
            lastRiseAt = utcNow
            if !isReplay { lastRiseMono = monoMs }
        }
        if !isReplay {
            confirmedIdleMinutes = lastRiseMono.map { max(0.0, Double(monoMs - $0) / 60_000.0) } ?? 0.0
        }

        sampleCount += 1

        if !seeded {
            let elapsed = windowMinutes - parsedResetsAt.timeIntervalSince(utcNow) / 60.0
            if elapsed >= minElapsedForSeedMinutes && envelopeP >= 3.0 {
                let m0 = min(elapsed, tau)
                let rWtd = elapsed > 0 ? envelopeP / elapsed : 0.0
                sp = rWtd * m0
                st = m0
                seeded = true
            }
        } else if let dt = dtMinutes {
            let decay = exp(-dt / tau)
            sp = sp * decay + dP
            st = st * decay + dt
        }

        lastAcceptedUtc = utcNow
        lastAcceptedMono = isReplay ? nil : monoMs   // decision 7: never persist a replay-era mono value
        return true
    }

    private func resetPendingRollover() {
        pendingRolloverKey = nil
        pendingRolloverCount = 0
    }

    private enum WindowKeyOutcome {
        case cold, sameWindow, rollover, implausibleIgnored, safetyValveRollover, regressedIgnore
    }

    /// Decision 1. A deadline within ±120 s of the tracked one is the same window (jitter). A
    /// genuinely later deadline (> 120 s ahead) is a rollover only if it is also *plausible* —
    /// `now` is at least within 120 s of the OLD deadline, i.e. the window was actually due to
    /// end. An implausible later deadline is ignored, unless it has recurred 3 consecutive times
    /// (the safety valve). A deadline that regressed by more than 120 s is replica noise on the
    /// key itself, never a rollover.
    ///
    /// Round-2 finding 1: `sameWindow` and `regressedIgnore` both reset the pending
    /// safety-valve counter — either one is proof the window has NOT ended.
    private func classifyWindowKey(_ candidate: Date, utcNow: Date) -> WindowKeyOutcome {
        guard let currentKey = windowKey else { return .cold }

        let diffSeconds = candidate.timeIntervalSince(currentKey)
        if abs(diffSeconds) <= Self.jitterToleranceSeconds {
            resetPendingRollover()
            return .sameWindow
        }
        if diffSeconds < 0 {
            resetPendingRollover()
            return .regressedIgnore
        }

        // A later deadline. Plausible only once we are within 120 s of the OLD deadline.
        let plausible = utcNow >= currentKey.addingTimeInterval(-Self.jitterToleranceSeconds)
        if plausible { return .rollover }

        if let pending = pendingRolloverKey,
           abs(candidate.timeIntervalSince(pending)) <= Self.jitterToleranceSeconds {
            pendingRolloverCount += 1
        } else {
            pendingRolloverKey = candidate
            pendingRolloverCount = 1
        }

        return pendingRolloverCount >= Self.implausibleRolloverSafetyValve
            ? .safetyValveRollover
            : .implausibleIgnored
    }

    private func computeDtMinutes(utcNow: Date, monoMs: Int64) -> Double {
        guard let lastUtc = lastAcceptedUtc else { return 0 }
        let utcDtSeconds = utcNow.timeIntervalSince(lastUtc)
        guard let lastMono = lastAcceptedMono else { return utcDtSeconds / 60.0 }

        let monoDtSeconds = Double(monoMs - lastMono) / 1000.0

        // A negative monotonic delta cannot happen within one continuous live process — a
        // monotonic clock only resets across an actual reboot, which in practice also means a
        // fresh, cold tracker. When it happens anyway (replaying two days of raw log data
        // spanning a genuine reboot through one tracker), mono is simply not comparable across
        // that gap: fall back to the UTC delta rather than letting a nonsensical value
        // masquerade as "definitely out of order".
        if monoDtSeconds < 0 { return utcDtSeconds / 60.0 }

        let dtSeconds = abs(utcDtSeconds - monoDtSeconds) > 15.0 ? monoDtSeconds : utcDtSeconds
        return dtSeconds / 60.0
    }

    private func clearForRollover() {
        seenFingerprints.removeAll()
        resetsAt = nil
        envelopeP = 0
        sampleCount = 0
        seeded = false
        sp = 0
        st = 0
        lastAcceptedUtc = nil
        lastAcceptedMono = nil
        lastRiseAt = nil
        lastRiseMono = nil
        confirmedIdleMinutes = 0
        // rolloverAt/rolloverMono/windowKey are set by the caller right after this.
    }

    /// Pure read: forecast + raw-classification inputs as of `utcNow`. Never mutates state.
    public func computeSnapshot(utcNow: Date, monoMs: Int64, isWeekly: Bool) -> WindowSnapshot {
        guard let currentResetsAt = resetsAt else {
            return WindowSnapshot(resetsAt: nil, forecast: nil, refused: true,
                                  reasonCode: .noData, sampleCount: sampleCount)
        }

        let tReset = currentResetsAt.timeIntervalSince(utcNow) / 60.0
        let elapsed = windowMinutes - tReset

        let rWtd = elapsed > 0 ? envelopeP / elapsed : 0.0
        let rEwma = st > 0 ? sp / st : 0.0
        let rate = isWeekly
            ? weeklyRate(rWtd: rWtd, rEwma: rEwma)
            : max(rEwma, decayedSessionFloor(rWtd))

        let forecast = ForecastMath.evaluate(
            usedPct: envelopeP, elapsedMinutes: elapsed, ratePctPerMin: rate,
            windowMinutes: windowMinutes, minutesToReset: tReset)

        // Round-2 decision 2: since-rollover uses a monotonic anchor when one exists (a live
        // rollover); a replay-driven rollover has no comparable monotonic epoch and falls back
        // to UTC.
        let sinceRolloverMinutes: Double? = rolloverMono.map { Double(monoMs - $0) / 60_000.0 }
            ?? rolloverAt.map { utcNow.timeIntervalSince($0) / 60.0 }

        var reason = MeasuringReasonCode.none
        var refused = false

        // Decision 12: seed eligibility is re-checked live, so time alone (not just a fresh
        // ingest) can satisfy the refusal rule's "no valid seed" clause. The weekly window uses
        // a 24 h minimum instead of W/30.
        let tooEarlyReason: MeasuringReasonCode = isWeeklyWindow ? .tooEarlyInWeek : .tooEarly
        let seedValidNow = elapsed >= minElapsedForSeedMinutes && envelopeP >= 3.0
        if !seedValidNow && sampleCount < 2 { refused = true; reason = tooEarlyReason }
        if elapsed < minElapsedForSeedMinutes { refused = true; reason = tooEarlyReason }
        if envelopeP < 3.0 { refused = true; reason = .tooLittleUsage }
        if let since = sinceRolloverMinutes, since < 5.0 { refused = true; reason = .rollover }

        // "Sleep across a reset": the deadline has passed but no later window has been observed
        // yet — the cached data still describes the old window. Takes priority when true.
        if currentResetsAt <= utcNow { refused = true; reason = .awaitingReset }

        return WindowSnapshot(resetsAt: currentResetsAt, forecast: forecast, refused: refused,
                              reasonCode: refused ? reason : .none, sampleCount: sampleCount)
    }

    private func weeklyRate(rWtd: Double, rEwma: Double) -> Double {
        let w = min(0.5, st / tau)
        return (1 - w) * rWtd + w * rEwma
    }

    /// Round-2 decision 7: `floor = r_wtd · exp(−idle / 30 min)`, where idle is
    /// `confirmedIdleMinutes` — frozen at the last accepted LIVE observation, never recomputed
    /// against the live evaluation clock. There is no decay without evidence: silence alone must
    /// not relax the floor further than it already was at the last confirming poll.
    private func decayedSessionFloor(_ rWtd: Double) -> Double {
        rWtd * exp(-confirmedIdleMinutes / Self.floorDecayMinutes)
    }
}
