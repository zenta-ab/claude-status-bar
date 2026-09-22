import Foundation

/// Inputs to the freshness ladder, evaluated fresh on every call — this runs on the
/// always-running 1 s UI timer (docs/forecast-and-states.md, "Freshness ladder"), never
/// accumulated, never cached. Ported from `src/Windows/Model/FreshnessOracle.cs`.
///
/// Freshness is two questions, and the 2026-09-22 split keeps them apart:
///
/// `staleAtMono`/`unknownAtMono` are **transport liveness** — *"are polls succeeding at roughly
/// the expected cadence"*. `QuotaModel` freezes them at each poll returning a valid reading for
/// the SESSION window — novel accepted, a cached duplicate of an already-open window, or a
/// validly closed one — as `t + 3·c_policy` and `t + 10·c_policy`, where `c_policy` is the
/// burning/idle/spent base interval **without** the soonest-reset cap and **without** failure
/// backoff. Neither a failure, a poll lacking a valid session reading, nor the mere passage of
/// time can move them later.
///
/// `lastAcceptedMono` is **data age** — *"is the data moving"*. It tracks the last **usable**
/// reading (accepted, or a window validly reporting itself closed) and is deliberately narrower:
/// a cached duplicate renews transport liveness (the poll round-tripped) but not this, so a
/// server that only ever replays the same snapshot still ages past the 20-minute threshold and
/// goes Stale while transport stays Live.
///
/// Using monotonic time for both the deadlines and the comparison also closes review finding 2's
/// last example: a UTC rollback too small to trip the 60 s clock-discontinuity check could
/// otherwise walk the clock backward across a UTC-denominated deadline and "improve" freshness
/// with no new data at all. Monotonic time cannot move backward that way.
public struct FreshnessInputs: Sendable {
    public let hasEverSucceeded: Bool
    public let staleAtMono: Int64?
    public let unknownAtMono: Int64?
    /// The monotonic instant of the last ACCEPTED live observation (`QuotaModel`'s own UTC↔mono
    /// anchor). Since re-anchoring only happens on an accepted sample, it already carries the
    /// "did real new data arrive" signal, without a second UTC-based duration to convert.
    public let lastAcceptedMono: Int64?
    public let sessionResetsAt: Date?
    public let weeklyResetsAt: Date?
    public let clockDiscontinuity: Bool

    public init(hasEverSucceeded: Bool, staleAtMono: Int64?, unknownAtMono: Int64?,
                lastAcceptedMono: Int64?, sessionResetsAt: Date?, weeklyResetsAt: Date?,
                clockDiscontinuity: Bool) {
        self.hasEverSucceeded = hasEverSucceeded
        self.staleAtMono = staleAtMono
        self.unknownAtMono = unknownAtMono
        self.lastAcceptedMono = lastAcceptedMono
        self.sessionResetsAt = sessionResetsAt
        self.weeklyResetsAt = weeklyResetsAt
        self.clockDiscontinuity = clockDiscontinuity
    }
}

public enum FreshnessOracle {
    static let fingerprintStaleAfterMs: Int64 = 20 * 60 * 1000
    static let resetGraceSeconds: TimeInterval = 60

    public static func evaluate(_ i: FreshnessInputs, utcNow: Date, monoMs: Int64) -> Freshness {
        if !i.hasEverSucceeded { return .unknown }

        // Decision 6: a detected clock jump makes every live-computed age suspect. Freshness
        // drops to Stale — not Unknown; we DO have real data, just an untrustworthy clock —
        // until the next accepted sample re-anchors.
        if i.clockDiscontinuity { return .stale }

        if let unknownAt = i.unknownAtMono, monoMs > unknownAt { return .unknown }
        if let staleAt = i.staleAtMono, monoMs > staleAt { return .stale }

        if let changed = i.lastAcceptedMono, monoMs - changed > fingerprintStaleAfterMs { return .stale }

        // resets_at deadlines stay UTC: an absolute wall-clock instant compared to the current
        // wall clock, not a duration since a past event, so it needs no monotonic conversion.
        // Guarded (decision 10) so the comparison itself cannot overflow.
        if let sessionResets = i.sessionResetsAt,
           let graced = QuotaTimeUtil.safeAdd(sessionResets, resetGraceSeconds), utcNow > graced {
            return .stale
        }
        if let weeklyResets = i.weeklyResetsAt,
           let graced = QuotaTimeUtil.safeAdd(weeklyResets, resetGraceSeconds), utcNow > graced {
            return .stale
        }

        return .live
    }
}
