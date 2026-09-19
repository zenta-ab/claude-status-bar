import Foundation

// Contract between the quota model (ingest, estimator, state machine, freshness) and everything
// that renders it (menu-bar icon, panel). Ported from src/Windows/Model/QuotaContracts.cs.
// Spec: docs/forecast-and-states.md.

public enum WindowKind: Sendable { case session, weekly }

public enum QuotaWindows {
    public static let sessionMinutes: Double = 300
    public static let weeklyMinutes: Double = 10_080
}

/// Why a window is refused to Measuring, for the panel's forecast line.
public enum MeasuringReasonCode: Sendable {
    case none
    case noData          // no sample ingested yet this window
    case rollover        // less than 5 min since the window key last advanced
    case tooEarly        // E < W/30 (session)
    case tooEarlyInWeek  // E < 24 h (weekly only — a partial day/night cycle skews the pace)
    case tooLittleUsage  // P < 3
    case awaitingReset   // resets_at has passed but no new window has been observed yet
    case dataMissing     // absent/invalid in the latest successful poll (decision 13)
    case clockJump       // utcNow is inconsistent with the monotonic anchor (decision 6)
}

/// One quota window as the UI should show it.
///
/// The optional forecast fields are nil while the window is Measuring. `projectedPctAtReset` is
/// uncapped (renderers clamp to 100). `shortfall` is the time you would be blocked before the
/// reset — **except** while `state` is `.measuring`: round-2 decision 11 kept it non-optional for
/// contract stability, so the zero there is a fixed placeholder, *not* "no blockage". Every
/// renderer gates display on `state`; `shortfall` must never be read without checking it first.
public struct WindowView: Sendable, Equatable {
    public let kind: WindowKind
    public let windowMinutes: Double
    public let usedPct: Double?
    public let resetsAt: Date?
    public let state: QuotaState
    public let ratePctPerMin: Double?
    public let paceMultiple: Double?
    public let projectedPctAtReset: Double?
    public let depletesAt: Date?
    public let shortfallMinutes: Double
    public let measuringReason: String?

    public init(kind: WindowKind, windowMinutes: Double, usedPct: Double?, resetsAt: Date?,
                state: QuotaState, ratePctPerMin: Double?, paceMultiple: Double?,
                projectedPctAtReset: Double?, depletesAt: Date?, shortfallMinutes: Double,
                measuringReason: String?) {
        self.kind = kind
        self.windowMinutes = windowMinutes
        self.usedPct = usedPct
        self.resetsAt = resetsAt
        self.state = state
        self.ratePctPerMin = ratePctPerMin
        self.paceMultiple = paceMultiple
        self.projectedPctAtReset = projectedPctAtReset
        self.depletesAt = depletesAt
        self.shortfallMinutes = shortfallMinutes
        self.measuringReason = measuringReason
    }

    public static func empty(_ kind: WindowKind, _ windowMinutes: Double) -> WindowView {
        WindowView(kind: kind, windowMinutes: windowMinutes, usedPct: nil, resetsAt: nil,
                   state: .measuring, ratePctPerMin: nil, paceMultiple: nil,
                   projectedPctAtReset: nil, depletesAt: nil, shortfallMinutes: 0,
                   measuringReason: "Hämtar…")
    }
}

/// Everything the icon and panel render. `lastChangedAt` is when a novel fingerprint last
/// arrived (the honest data age), not when the last poll round-tripped. `blockedUntil` is set
/// only while some window is Spent.
public struct QuotaView: Sendable, Equatable {
    public let session: WindowView
    public let weekly: WindowView
    public let freshness: Freshness
    public let lastChangedAt: Date?
    public let lastPollAt: Date?
    public let pollInterval: TimeInterval
    public let error: String?
    public let iconSeverity: QuotaState
    public let blockedUntil: Date?

    public init(session: WindowView, weekly: WindowView, freshness: Freshness,
                lastChangedAt: Date?, lastPollAt: Date?, pollInterval: TimeInterval,
                error: String?, iconSeverity: QuotaState, blockedUntil: Date?) {
        self.session = session
        self.weekly = weekly
        self.freshness = freshness
        self.lastChangedAt = lastChangedAt
        self.lastPollAt = lastPollAt
        self.pollInterval = pollInterval
        self.error = error
        self.iconSeverity = iconSeverity
        self.blockedUntil = blockedUntil
    }

    public static let initial = QuotaView(
        session: .empty(.session, QuotaWindows.sessionMinutes),
        weekly: .empty(.weekly, QuotaWindows.weeklyMinutes),
        freshness: .unknown, lastChangedAt: nil, lastPollAt: nil,
        pollInterval: 30, error: nil, iconSeverity: .measuring, blockedUntil: nil)
}

/// Owned and called on the main thread only. `ingest`/`ingestFailure` run once per poll result;
/// `evaluate` runs on the 1 s UI timer and must be cheap.
public protocol QuotaModelling: AnyObject {
    func ingest(_ snapshot: UsageSnapshot, utcNow: Date, monoMs: Int64)
    func ingestFailure(_ error: String, utcNow: Date, monoMs: Int64)
    func evaluate(utcNow: Date, monoMs: Int64) -> QuotaView
    func nextPollDelay(utcNow: Date) -> TimeInterval
}
