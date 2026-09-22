import Foundation

/// Composes `WindowTracker` (ingest/estimator), `HysteresisHold` (state machine) and
/// `FreshnessOracle` into the `QuotaModelling` contract. Owned and called on the main thread
/// only. Ported from `src/Windows/Model/QuotaModel.cs`.
///
/// Poll-rate work (raw classification, hysteresis stepping, bookkeeping, freshness-deadline
/// freezing, the clock anchor) happens once per `ingest`/`ingestFailure`. `evaluate` re-reads
/// live-computable numbers (E, t_reset, r, grace, the clock-discontinuity check) against
/// already-committed/frozen state — cheap enough for the 1 s UI tick. Round-2 decision 4 adds
/// exactly one exception to "never re-runs hysteresis on the tick": a window stuck in Measuring
/// may commit its very first verdict on live time/eligibility alone.
public final class QuotaModel: QuotaModelling {
    private static let sessionHalfLifeMinutes = 15.0
    private static let weeklyHalfLifeMinutes = 8.0 * 60.0

    static let burningWindowSeconds: TimeInterval = 10 * 60
    static let idleInterval: TimeInterval = 150
    static let burningInterval: TimeInterval = 30
    static let spentInterval: TimeInterval = 300
    static let awaitingResetInterval: TimeInterval = 30
    static let resetPollGrace: TimeInterval = 3
    static let minPollInterval: TimeInterval = 1
    static let clockJumpThresholdSeconds: TimeInterval = 60
    static let backoffBaseSeconds = 60.0
    static let backoffCapSeconds = 600.0

    private let session = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes,
                                        halfLifeMinutes: sessionHalfLifeMinutes, isWeekly: false)
    private let weekly = WindowTracker(windowMinutes: QuotaWindows.weeklyMinutes,
                                       halfLifeMinutes: weeklyHalfLifeMinutes, isWeekly: true)
    private let sessionHold = HysteresisHold(weekly: false)
    private let weeklyHold = HysteresisHold(weekly: true)

    private var everSucceeded = false
    /// Consumed only by an ACCEPTED (usable) live ingest, not merely a poll that round-tripped.
    private var liveDataIngested = false
    private var lastPollAt: Date?
    private var consecutiveFailures = 0
    private var lastError: String?

    /// Decision 13: was this window present/parseable in the latest successful poll?
    private var sessionValidLastPoll = false
    private var weeklyValidLastPoll = false
    /// Present but closed. Decision 13 forces Measuring and drops global Live when a window is
    /// missing from a successful poll — correct for missing data, wrong for a window that is
    /// reporting itself shut. Tracked separately so the two cases can say different things.
    private var sessionInactiveLastPoll = false
    private var weeklyInactiveLastPoll = false

    /// **Transport liveness** deadlines, frozen in MONOTONIC milliseconds using the POLICY
    /// interval only (no reset cap, no backoff). They answer *"are polls succeeding at roughly
    /// the expected cadence"* — a different question from *"is the data moving"*, which
    /// `lastUsableMono` answers below.
    ///
    /// Renewed by ANY poll returning a valid reading for the SESSION window: novel accepted, a
    /// cached duplicate of an already-open window, or a validly closed one. Never by a failure,
    /// and never by a poll without a valid session reading.
    ///
    /// Before this split (2026-09-22) they re-froze only on a *novel accepted* sample. But
    /// `get_usage` produces a novel sample roughly every 5 min and returns a byte-identical
    /// cached reply to every poll in between, so at the 30 s burning cadence `stale_at` sat at
    /// last-novel + 90 s while novel samples arrived ~300 s apart — the panel read Stale for most
    /// of every refresh cycle while the user was working and nothing was wrong.
    ///
    /// Widening this does not reopen round-2 decision 2's concern: that is about the UTC↔mono
    /// anchor, which is never re-anchored here, and a duplicate cannot fake elapsed monotonic
    /// time.
    private var freshnessStaleAtMono: Int64?
    private var freshnessUnknownAtMono: Int64?

    /// UTC↔monotonic anchor from the last ACCEPTED LIVE sample only — a deduped reply must never
    /// re-anchor, or it silently defeats clock-jump detection on the very next tick.
    private var anchorUtc: Date?
    private var anchorMono: Int64?

    /// **Data age**: the last poll that delivered a currently usable reading — accepted, OR a
    /// window validly reporting itself closed. Drives only the 20-minute fingerprint-stale rule
    /// (`FreshnessInputs.lastAcceptedMono`).
    ///
    /// Deliberately NARROWER than what renews the transport deadlines above: a cached duplicate
    /// of an open window must not count here, or a server that never produces a novel sample
    /// would read as perpetually fresh data instead of eventually going Stale.
    ///
    /// A window with no `resets_at` — an expired 5 h session, or a weekly window just after its
    /// reset with nothing consumed — can never be accepted, because the tracker has no deadline
    /// to track. Tying freshness to acceptance therefore froze its deadlines forever: an account
    /// nobody had used in a while decayed to Unknown and showed "!" in the menu bar while its
    /// polls were succeeding every 150 s and the honest answer was "0 % used, nothing running".
    ///
    /// An inactive window is by definition current — there is no older cached state it could be
    /// a stale copy of — so it refreshes freshness. It does **not** re-anchor the clock guard:
    /// that needs a real timing baseline from a sample the tracker actually took.
    private var lastUsableMono: Int64?
    /// The UTC side of the same thing, for `QuotaView.lastChangedAt`. An account whose windows
    /// are all closed never accepts a sample, so `session.lastAcceptedUtc` stays nil forever and
    /// the panel header would read "Hämtar…" indefinitely on a perfectly healthy account. Every
    /// poll returns the same true answer there — "nothing running" — so the last usable poll IS
    /// the honest age of that data.
    private var lastUsableUtc: Date?

    /// The latest monoMs received via ingest/evaluate (NOT ingestFailure), so `nextPollDelay`,
    /// whose signature carries no monoMs, can still classify "burning" monotonically.
    private var lastKnownMono: Int64?

    public init() {}

    // Test seams.
    var sessionTracker: WindowTracker { session }
    var weeklyTracker: WindowTracker { weekly }
    var sessionHysteresis: HysteresisHold { sessionHold }
    var weeklyHysteresis: HysteresisHold { weeklyHold }

    public func ingest(_ snapshot: UsageSnapshot, utcNow: Date, monoMs: Int64) {
        lastKnownMono = monoMs

        // Round-2 decision 3: session validity requires a valid percentage too, exactly like
        // weekly — an invalid utilization must not retain a stale verdict and Live.
        // "Valid" here means trackable: a percentage AND a deadline to measure it against.
        let sessionValid = QuotaTimeUtil.parseResetsAt(snapshot.sessionResetsAt) != nil
            && snapshot.sessionUtilization.map(QuotaTimeUtil.isValidPct) == true
        let weeklyValid = snapshot.weeklyUtilization.map(QuotaTimeUtil.isValidPct) == true
            && QuotaTimeUtil.parseResetsAt(snapshot.weeklyResetsAt) != nil

        // Decision 13: a missing/invalid window this poll is never substituted with a default —
        // it simply is not fed to the tracker this round, and `evaluate` reports that window
        // Measuring ("data saknas") instead of a stale or blended forecast.
        let sessionAccepted = sessionValid && session.ingest(
            pct: snapshot.sessionUtilization!, resetsAtRaw: snapshot.sessionResetsAt,
            utcNow: utcNow, monoMs: monoMs)
        let weeklyAccepted = weeklyValid && weekly.ingest(
            pct: snapshot.weeklyUtilization!, resetsAtRaw: snapshot.weeklyResetsAt,
            utcNow: utcNow, monoMs: monoMs)

        sessionValidLastPoll = sessionValid
        weeklyValidLastPoll = weeklyValid
        sessionInactiveLastPoll = !sessionValid && !snapshot.sessionIsActive
            && snapshot.sessionUtilization.map(QuotaTimeUtil.isValidPct) == true
        weeklyInactiveLastPoll = !weeklyValid && !snapshot.weeklyIsActive
            && snapshot.weeklyUtilization.map(QuotaTimeUtil.isValidPct) == true

        // Decision 11: hysteresis advances only on accepted (novel) observations.
        if sessionAccepted {
            stepHysteresis(session, sessionHold, utcNow: utcNow, monoMs: monoMs, isWeekly: false)
        }
        if weeklyAccepted {
            stepHysteresis(weekly, weeklyHold, utcNow: utcNow, monoMs: monoMs, isWeekly: true)
        }

        let anyAccepted = sessionAccepted || weeklyAccepted
        if anyAccepted { liveDataIngested = true }

        // "Usable" = something was accepted, OR a window is validly reporting that it is not
        // currently open. Both are current readings; only a cached duplicate is not.
        let usable = anyAccepted
            || (!sessionValid && !snapshot.sessionIsActive
                && snapshot.sessionUtilization.map(QuotaTimeUtil.isValidPct) == true)
            || (!weeklyValid && !snapshot.weeklyIsActive
                && snapshot.weeklyUtilization.map(QuotaTimeUtil.isValidPct) == true)

        everSucceeded = true
        lastPollAt = utcNow
        consecutiveFailures = 0
        lastError = nil

        // Round-2 decision 2: re-anchor the CLOCK GUARD only on a genuinely novel accepted
        // observation — a deduped reply must never renew it.
        if anyAccepted {
            anchorUtc = utcNow
            anchorMono = monoMs
        }

        // DATA AGE — the narrow half. Only a genuinely new reading counts: a novel accepted
        // sample, or a window validly reporting itself closed.
        if usable {
            lastUsableMono = monoMs
            lastUsableUtc = utcNow
        }

        // TRANSPORT LIVENESS — the wide half. Any poll carrying a valid session reading proves
        // the pipeline is working, whether or not the reading is new. See the deadlines' own doc
        // comment for the false-Stale symptom this split fixed.
        let sessionCurrentThisPoll = sessionValid || sessionInactiveLastPoll
        if sessionCurrentThisPoll {
            let cPolicy = policyInterval(utcNow: utcNow)
            freshnessStaleAtMono = monoMs + Int64(cPolicy * 1000 * 3)
            freshnessUnknownAtMono = monoMs + Int64(cPolicy * 1000 * 10)
        }
    }

    public func ingestFailure(_ error: String, utcNow: Date, monoMs: Int64) {
        lastPollAt = utcNow
        lastError = error
        consecutiveFailures += 1
        // A failure must NOT gate WarmStart (only an accepted ingest does), must never move the
        // frozen freshness deadlines or the anchor, and neither counts as hysteresis evidence
        // nor resets pending evidence — all of which follow from simply not touching them here.
        // lastKnownMono is deliberately NOT updated: see its doc comment.
    }

    /// One-time warm start from the window-shape CSV (docs/forecast-and-states.md, "Ingest"
    /// rule 6). Call exactly once, with the FIRST successful snapshot, **before** calling
    /// `ingest` with that same snapshot — replay must reconstruct history older than the live
    /// sample, and `WindowTracker`'s `dt <= 0` guard would otherwise drop every replayed row as
    /// "older than the last accepted sample".
    ///
    /// A no-op once a live, usable sample has been accepted (round-2 decision 6/14: an initial
    /// transport failure, or an initial response that failed every window's validity check, does
    /// not consume this).
    public func warmStart(_ snapshot: UsageSnapshot, utcNow: Date, rows: [CsvReplay.RawRow]) {
        if liveDataIngested { return }
        if rows.isEmpty { return }

        replayWindow(session, kind: .session, resetsAtRaw: snapshot.sessionResetsAt, rows: rows, utcNow: utcNow)
        replayWindow(weekly, kind: .weekly, resetsAtRaw: snapshot.weeklyResetsAt, rows: rows, utcNow: utcNow)
    }

    private func replayWindow(_ tracker: WindowTracker, kind: WindowKind, resetsAtRaw: String?,
                              rows: [CsvReplay.RawRow], utcNow: Date) {
        guard let resetsAt = QuotaTimeUtil.parseResetsAt(resetsAtRaw) else { return }
        guard QuotaTimeUtil.isPlausibleResetsAt(resetsAt, now: utcNow) else { return }

        // Round-2 decision 6: pass the full-precision resets_at, not a truncated key —
        // membership matches with the same ±120 s jitter tolerance a live poll uses.
        let matched = CsvReplay.filterForWindow(rows, kind: kind, windowKey: resetsAt,
                                                windowMinutes: tracker.windowMinutes, now: utcNow)
        for row in matched {
            tracker.ingest(pct: row.pct, resetsAtRaw: row.resetsAtRaw,
                           utcNow: row.utcIso, monoMs: row.monoMs, isReplay: true)
        }
    }

    private func stepHysteresis(_ tracker: WindowTracker, _ hold: HysteresisHold,
                                utcNow: Date, monoMs: Int64, isWeekly: Bool) {
        let snap = tracker.computeSnapshot(utcNow: utcNow, monoMs: monoMs, isWeekly: isWeekly)
        let raw: QuotaState = snap.forecast.map {
            RawStateClassifier.classify(usedPct: $0.usedPct, forecast: $0,
                                        windowMinutes: tracker.windowMinutes, refused: snap.refused)
        } ?? .measuring
        hold.step(raw, monoMs: monoMs)
    }

    public func evaluate(utcNow: Date, monoMs: Int64) -> QuotaView {
        lastKnownMono = monoMs

        let pollInterval = computePollInterval(utcNow: utcNow)
        let clockJump = detectClockDiscontinuity(utcNow: utcNow, monoMs: monoMs)

        let sessionSnap = session.computeSnapshot(utcNow: utcNow, monoMs: monoMs, isWeekly: false)
        let weeklySnap = weekly.computeSnapshot(utcNow: utcNow, monoMs: monoMs, isWeekly: true)

        // Round-2 decision 4: a window stuck in Measuring commits its first verdict the instant
        // it is no longer refused, even with no new accepted sample. Skipped during a clock jump
        // (the snapshot's own E/t_reset are themselves suspect) and while this window's latest
        // poll was invalid.
        if !clockJump, sessionValidLastPoll {
            maybeCommitFromMeasuring(sessionHold, sessionSnap, QuotaWindows.sessionMinutes, monoMs)
        }
        if !clockJump, weeklyValidLastPoll {
            maybeCommitFromMeasuring(weeklyHold, weeklySnap, QuotaWindows.weeklyMinutes, monoMs)
        }

        let sessionFinal = clockJump ? .measuring : finalState(sessionHold, sessionSnap, sessionValidLastPoll)
        let weeklyFinal = clockJump ? .measuring : finalState(weeklyHold, weeklySnap, weeklyValidLastPoll)

        let sessionForcedReason: MeasuringReasonCode? = clockJump ? .clockJump
            : (sessionValidLastPoll ? nil : (sessionInactiveLastPoll ? .windowInactive : .dataMissing))
        let weeklyForcedReason: MeasuringReasonCode? = clockJump ? .clockJump
            : (weeklyValidLastPoll ? nil : (weeklyInactiveLastPoll ? .windowInactive : .dataMissing))

        let sessionView = buildView(.session, QuotaWindows.sessionMinutes, sessionSnap,
                                    sessionFinal, utcNow, sessionForcedReason)
        let weeklyView = buildView(.weekly, QuotaWindows.weeklyMinutes, weeklySnap,
                                   weeklyFinal, utcNow, weeklyForcedReason)

        let severity = QuotaState(rawValue: max(sessionFinal.rawValue, weeklyFinal.rawValue)) ?? .measuring

        var blockedUntil: Date?
        if sessionFinal == .spent, let resets = sessionSnap.resetsAt { blockedUntil = resets }
        if weeklyFinal == .spent, let resets = weeklySnap.resetsAt {
            blockedUntil = (blockedUntil == nil || resets > blockedUntil!) ? resets : blockedUntil
        }

        let inputs = FreshnessInputs(
            hasEverSucceeded: everSucceeded, staleAtMono: freshnessStaleAtMono,
            unknownAtMono: freshnessUnknownAtMono, lastAcceptedMono: lastUsableMono,
            sessionResetsAt: sessionSnap.resetsAt, weeklyResetsAt: weeklySnap.resetsAt,
            clockDiscontinuity: clockJump)
        var freshness = FreshnessOracle.evaluate(inputs, utcNow: utcNow, monoMs: monoMs)

        // Decision 13: global Live requires a usable session window. A window reporting itself
        // CLOSED counts — it is an answer, not an absence — otherwise an account nobody has used
        // for a while reads as stale forever while its polls succeed.
        if freshness == .live, !sessionValidLastPoll, !sessionInactiveLastPoll { freshness = .stale }

        return QuotaView(
            session: sessionView, weekly: weeklyView, freshness: freshness,
            lastChangedAt: session.lastAcceptedUtc ?? lastUsableUtc, lastPollAt: lastPollAt,
            pollInterval: pollInterval, error: lastError,
            iconSeverity: severity, blockedUntil: blockedUntil)
    }

    /// A no-op unless the hold is currently Measuring AND the snapshot is no longer refused.
    private func maybeCommitFromMeasuring(_ hold: HysteresisHold, _ snap: WindowSnapshot,
                                          _ windowMinutes: Double, _ monoMs: Int64) {
        guard hold.committed == .measuring else { return }
        guard !snap.refused, let forecast = snap.forecast else { return }
        let raw = RawStateClassifier.classify(usedPct: forecast.usedPct, forecast: forecast,
                                              windowMinutes: windowMinutes, refused: false)
        hold.commitFromMeasuring(raw, monoMs: monoMs)
    }

    /// The committed verdict with grace applied live, except for two conditions that override it
    /// straight to Measuring without going through hysteresis at all — both are live, time-based
    /// facts rather than sample-based verdicts, so (like Measuring itself) there is nothing to
    /// debounce: decision 13's per-poll validity, and "awaiting reset".
    private func finalState(_ hold: HysteresisHold, _ snap: WindowSnapshot, _ validLastPoll: Bool) -> QuotaState {
        if !validLastPoll { return .measuring }
        if snap.refused, snap.reasonCode == .awaitingReset { return .measuring }
        guard let forecast = snap.forecast else { return hold.committed }
        // Both live clamps, in order: a DryEarly with no actual shortfall presents as Tight, and
        // the near-reset grace applies on top. Neither ever raises severity.
        let withoutFalseShortfall = GraceCap.applyNoShortfall(hold.committed, forecast: forecast)
        return GraceCap.apply(withoutFalseShortfall, minutesToReset: forecast.minutesToReset)
    }

    private func buildView(_ kind: WindowKind, _ windowMinutes: Double, _ snap: WindowSnapshot,
                           _ finalState: QuotaState, _ utcNow: Date,
                           _ forcedReason: MeasuringReasonCode?) -> WindowView {
        let measuring = finalState == .measuring
        let reasonCode = forcedReason ?? snap.reasonCode

        guard let forecast = snap.forecast else {
            let base = WindowView.empty(kind, windowMinutes)
            return WindowView(kind: base.kind, windowMinutes: base.windowMinutes, usedPct: nil,
                              resetsAt: snap.resetsAt, state: finalState, ratePctPerMin: nil,
                              paceMultiple: nil, projectedPctAtReset: nil, depletesAt: nil,
                              shortfallMinutes: 0,
                              measuringReason: measuring ? Self.reasonText(reasonCode) : nil)
        }

        // Decision 12: ALL forecast display fields are nil whenever the final state is Measuring,
        // regardless of whether the underlying forecast carries numbers — a window can be
        // Measuring for a reason unrelated to the estimator (rollover gate, awaiting reset, data
        // missing, clock jump) while still holding a stale rate. % and the countdown still show.
        // Round-2 decision 11 ("partial"): shortfall stays non-optional for contract stability —
        // the zero here is a fixed placeholder, meaningless while State is Measuring.
        if measuring {
            return WindowView(kind: kind, windowMinutes: windowMinutes, usedPct: forecast.usedPct,
                              resetsAt: snap.resetsAt, state: finalState, ratePctPerMin: nil,
                              paceMultiple: nil, projectedPctAtReset: nil, depletesAt: nil,
                              shortfallMinutes: 0, measuringReason: Self.reasonText(reasonCode))
        }

        let depletesAt = QuotaTimeUtil.safeAddMinutes(utcNow, forecast.minutesToDeplete)
        return WindowView(kind: kind, windowMinutes: windowMinutes, usedPct: forecast.usedPct,
                          resetsAt: snap.resetsAt, state: finalState,
                          ratePctPerMin: forecast.ratePctPerMin, paceMultiple: forecast.pace,
                          projectedPctAtReset: forecast.projectedPctAtReset, depletesAt: depletesAt,
                          shortfallMinutes: forecast.shortfallMinutes, measuringReason: nil)
    }

    static func reasonText(_ reason: MeasuringReasonCode) -> String {
        switch reason {
        case .rollover: return "Nytt fönster, mäter takt…"
        case .tooEarly: return "För tidigt att mäta takt…"
        case .tooEarlyInWeek: return "För tidigt i veckan — väntar på ett helt dygn"
        case .tooLittleUsage: return "För lite förbrukning ännu…"
        case .noData: return "Hämtar…"
        case .awaitingReset: return "Nytt fönster väntas"
        case .dataMissing: return "Data saknas"
        case .windowInactive: return "Inget förbrukat ännu"
        case .clockJump: return "Klockan ändrades, mäter om…"
        case .none: return "Mäter takt…"
        }
    }

    /// Decision 6: is `utcNow` consistent with elapsed monotonic time since the last accepted
    /// live sample?
    private func detectClockDiscontinuity(utcNow: Date, monoMs: Int64) -> Bool {
        guard let anchorUtc, let anchorMono else { return false }
        let expected = anchorUtc.addingTimeInterval(Double(monoMs - anchorMono) / 1000.0)
        return abs(utcNow.timeIntervalSince(expected)) > Self.clockJumpThresholdSeconds
    }

    public func nextPollDelay(utcNow: Date) -> TimeInterval { computePollInterval(utcNow: utcNow) }

    private func computePollInterval(utcNow: Date) -> TimeInterval {
        let base = consecutiveFailures > 0 ? backoffInterval() : policyInterval(utcNow: utcNow)
        return capToSoonestReset(base, utcNow: utcNow)
    }

    private func backoffInterval() -> TimeInterval {
        min(Self.backoffBaseSeconds * pow(2, Double(consecutiveFailures - 1)), Self.backoffCapSeconds)
    }

    /// The base poll-rate policy: awaiting-reset / Spent / burning / idle, WITHOUT the
    /// soonest-reset cap and WITHOUT failure backoff. Used both for the non-failure base interval
    /// and to freeze freshness deadlines at each accepted poll.
    ///
    /// Round-2 decision 8: awaiting-reset is checked BEFORE Spent. An expired Spent window must
    /// recover at the fast 30 s cadence, not stay pinned to the slow 300 s interval for a window
    /// that is not actually current any more.
    private func policyInterval(utcNow: Date) -> TimeInterval {
        if isAwaitingReset(utcNow: utcNow) { return Self.awaitingResetInterval }
        if sessionHold.committed == .spent || weeklyHold.committed == .spent { return Self.spentInterval }
        return isBurning() ? Self.burningInterval : Self.idleInterval
    }

    private func isAwaitingReset(utcNow: Date) -> Bool {
        if let resets = session.resetsAt,
           let graced = QuotaTimeUtil.safeAdd(resets, Self.resetPollGrace), utcNow >= graced { return true }
        if let resets = weekly.resetsAt,
           let graced = QuotaTimeUtil.safeAdd(resets, Self.resetPollGrace), utcNow >= graced { return true }
        return false
    }

    /// Round-2 decision 2: monotonic, using the latest monoMs received via ingest/evaluate —
    /// never a UTC duration against `lastRiseAt`.
    private func isBurning() -> Bool {
        guard let mono = lastKnownMono else { return false }
        let windowMs = Int64(Self.burningWindowSeconds * 1000)
        if let rise = session.lastRiseMono, mono - rise <= windowMs { return true }
        if let rise = weekly.lastRiseMono, mono - rise <= windowMs { return true }
        return false
    }

    /// Decision 4: only a deadline still AHEAD of us can cap the interval, and only down to
    /// reset + 3 s — once that instant has passed it never caps anything again (that is what
    /// awaiting-reset takes over for). This is what turns the old "1 s forever after a reset"
    /// hot loop into a single poll landing exactly at reset + 3 s, then a steady 30 s cadence.
    private func capToSoonestReset(_ interval: TimeInterval, utcNow: Date) -> TimeInterval {
        var soonest: Date?
        for resets in [session.resetsAt, weekly.resetsAt].compactMap({ $0 }) {
            guard let graced = QuotaTimeUtil.safeAdd(resets, Self.resetPollGrace), graced > utcNow else { continue }
            if soonest == nil || graced < soonest! { soonest = graced }
        }
        guard let soonest else { return interval }

        let untilCap = soonest.timeIntervalSince(utcNow)   // always > 0 by the guard above
        let effective = min(untilCap, interval)
        return max(effective, Self.minPollInterval)
    }
}
