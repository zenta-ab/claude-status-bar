import Foundation

/// Raw state table, grace cap and hysteresis, per docs/forecast-and-states.md,
/// "States (per window)". Ported from `src/Windows/Model/StateMachine.cs`.
///
/// Three independent, directly testable pieces:
///   `RawStateClassifier` — the priority table (Spent > Measuring > DryEarly > Tight > Safe)
///   `GraceCap`           — the near-reset clamp, applied after hysteresis, every tick
///   `HysteresisHold`     — per-window debounce, stepped once per ACCEPTED (novel) observation,
///                          never per poll tick or duplicate
///
/// Leaving Measuring commits immediately, because Measuring is not a verdict and there is
/// nothing to debounce — hysteresis only ever arbitrates between Safe/Tight/DryEarly. Spent,
/// grace and rollover all bypass it.
public enum RawStateClassifier {
    public static func classify(
        usedPct: Double, forecast: ForecastResult, windowMinutes: Double, refused: Bool
    ) -> QuotaState {
        // Decision 15: Spent only at P >= 100 (not 99.5) — confirmed exhaustion, not "almost".
        // 97–99.9 % still reaches DryEarly on its own via the remaining <= 3 clause below.
        if usedPct >= 100.0 { return .spent }
        if refused { return .measuring }

        let sLo = max(0.05 * windowMinutes, 10.0)
        let sHi = max(0.08 * windowMinutes, 15.0)

        if forecast.shortfallMinutes >= sLo || forecast.remainingPct <= 3.0 { return .dryEarly }
        if forecast.shortfallMinutes > 0.0 || forecast.slackMinutes <= sHi || forecast.remainingPct <= 10.0 {
            return .tight
        }
        return .safe
    }
}

public enum GraceCap {
    /// A second live clamp, in the same spirit as the near-reset one below: a committed
    /// **DryEarly whose current forecast shows no shortfall at all** is contradicted by the live
    /// numbers, and presenting it unchanged produces copy that states a falsehood — observed in
    /// the real app as *"⚠ Kvoten tar slut sön kl 00:52"* over *"0 s före reset kl 23:20"*, i.e.
    /// a depletion falling AFTER the reset paired with a shortfall of zero.
    ///
    /// Hysteresis is right to hold the verdict (the pace really was that high, and flapping is
    /// worse), so this does not clear it — it lowers it one step to Tight, which is exactly
    /// "the pace is straining but on current numbers it reaches the reset". Like every cap here
    /// it can only ever lower severity, never raise it, and it never produces Safe: a verdict
    /// that was DryEarly must not become "✓ räcker till reset" on one tick's arithmetic.
    ///
    /// Applies only when there is genuinely no blockage: any shortfall at all, or 3 % or less
    /// remaining, leaves DryEarly exactly as committed.
    public static func applyNoShortfall(_ state: QuotaState, forecast: ForecastResult) -> QuotaState {
        guard state == .dryEarly else { return state }
        guard forecast.shortfallMinutes < 1.0, forecast.remainingPct > 3.0 else { return state }
        return .tight
    }

    /// Decision 2: grace must never turn a predicted cutoff into Safe. The only remaining cap is
    /// `t_reset <= 15 min`, which caps DryEarly at Tight (no red right before a reset that will
    /// clear it anyway); the old `t_reset <= 5 min → Safe` rule is removed entirely. Spent is
    /// never capped, and a cap can only ever lower severity, never raise it.
    public static func apply(_ state: QuotaState, minutesToReset: Double) -> QuotaState {
        if state == .spent { return state }
        if minutesToReset <= 15.0 {
            return QuotaState(rawValue: min(state.rawValue, QuotaState.tight.rawValue)) ?? state
        }
        return state
    }
}

/// Per-window hysteresis. `step` must be called only for ACCEPTED (novel) observations —
/// decision 11: duplicate replies and poll ticks are not evidence, and a failed poll neither
/// counts as evidence nor resets pending evidence (the caller simply never steps for either).
///
/// All persistence — `lastChangeMono`, the cooldown, and the de-escalation minimum-elapsed gate
/// — is **monotonic** milliseconds, not UTC (round-2 decision 2).
///
/// Escalation commits to the LOWEST severity still above `committed`, seen across the last
/// `escalateEvidenceCount` accepted observations — not exact repeated agreement on one target.
/// That is what lets an alternating Tight/DryEarly escalate at all; the old exact-match rule
/// could stall on Safe forever (review finding 11).
///
/// De-escalation needs `deescalateEvidenceCount` consecutive accepted observations agreeing on
/// the same lower state, AND at least `deescalateMinElapsedMs` of real time since the first of
/// them — replacing poll-count-only thresholds, which scaled with polling policy instead of
/// wall-clock confidence.
public final class HysteresisHold {
    private let escalateEvidenceCount: Int
    private let deescalateEvidenceCount: Int
    private let deescalateMinElapsedMs: Int64
    private let cooldownMs: Int64

    private var escalateHistory: [QuotaState] = []
    private var pendingDeescalateState: QuotaState?
    private var pendingDeescalateCount = 0
    private var pendingDeescalateSinceMono: Int64?

    public private(set) var committed: QuotaState = .measuring
    public private(set) var lastChangeMono: Int64?

    public init(weekly: Bool) {
        escalateEvidenceCount = weekly ? 4 : 2
        deescalateEvidenceCount = weekly ? 4 : 3
        deescalateMinElapsedMs = weekly ? 2 * 60 * 60 * 1000 : 10 * 60 * 1000
        cooldownMs = weekly ? 5 * 60 * 1000 : 90 * 1000
    }

    /// Call only for an accepted (novel) observation — see the type doc comment.
    @discardableResult
    public func step(_ raw: QuotaState, monoMs: Int64) -> QuotaState {
        // Spent, Measuring (entering or leaving) and rollover (which forces raw == measuring
        // while its gate holds) all bypass hysteresis: commit immediately.
        if raw == .spent || raw == .measuring || committed == .measuring || committed == .spent {
            commitImmediately(raw, monoMs: monoMs)
            return committed
        }

        if raw == committed {
            resetPendingEvidence()
            return committed
        }

        let cooldownElapsed = lastChangeMono.map { monoMs - $0 >= cooldownMs } ?? true
        let escalating = raw.rawValue > committed.rawValue

        if escalating {
            pendingDeescalateState = nil
            pendingDeescalateCount = 0
            pendingDeescalateSinceMono = nil

            escalateHistory.append(raw)
            if escalateHistory.count > escalateEvidenceCount { escalateHistory.removeFirst() }

            let allAboveCommitted = escalateHistory.count == escalateEvidenceCount
                && escalateHistory.allSatisfy { $0.rawValue > committed.rawValue }
            if allAboveCommitted, cooldownElapsed,
               let lowest = escalateHistory.min(by: { $0.rawValue < $1.rawValue }) {
                commitImmediately(lowest, monoMs: monoMs)
            }
        } else {
            escalateHistory.removeAll()

            if pendingDeescalateState != raw {
                pendingDeescalateState = raw
                pendingDeescalateCount = 1
                pendingDeescalateSinceMono = monoMs
            } else {
                pendingDeescalateCount += 1
            }

            let enoughEvidence = pendingDeescalateCount >= deescalateEvidenceCount
            let enoughTime = pendingDeescalateSinceMono.map { monoMs - $0 >= deescalateMinElapsedMs } ?? false
            if enoughEvidence, enoughTime, cooldownElapsed {
                commitImmediately(raw, monoMs: monoMs)
            }
        }

        return committed
    }

    /// Round-2 decision 4: leaving Measuring on live time/eligibility alone, with no new accepted
    /// sample — the rollover/weekly-24h gates lifting purely with elapsed time, or a
    /// WarmStart-seeded window whose only live poll so far was deduped. Bypasses all evidence,
    /// exactly like `step`'s own "leaving Measuring commits immediately" branch, but is safe to
    /// call from a pure evaluation tick (unlike `step`, which would otherwise treat a
    /// non-Measuring committed's raw as evidence on every 1 s tick, violating decision 11).
    /// A no-op unless `committed` is currently `.measuring`.
    @discardableResult
    public func commitFromMeasuring(_ raw: QuotaState, monoMs: Int64) -> QuotaState {
        guard committed == .measuring else { return committed }
        commitImmediately(raw, monoMs: monoMs)
        return committed
    }

    private func resetPendingEvidence() {
        escalateHistory.removeAll()
        pendingDeescalateState = nil
        pendingDeescalateCount = 0
        pendingDeescalateSinceMono = nil
    }

    private func commitImmediately(_ state: QuotaState, monoMs: Int64) {
        committed = state
        lastChangeMono = monoMs
        resetPendingEvidence()
    }
}
