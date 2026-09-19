import Foundation

/// The five per-window verdicts (docs/forecast-and-states.md, "States"). Ordered by severity, so
/// the icon's `max(session, weekly)` rule is a plain comparison. `measuring` ranks lowest: it is
/// not a verdict, it is the absence of one.
public enum QuotaState: Int, Comparable, Sendable, CaseIterable {
    case measuring = 0
    case safe = 1
    case tight = 2
    case dryEarly = 3
    case spent = 4

    public static func < (a: QuotaState, b: QuotaState) -> Bool { a.rawValue < b.rawValue }
}

/// The freshness ladder (docs/forecast-and-states.md, "Freshness ladder"). It exists because
/// `get_usage` cannot report its own staleness, so the app must never present a number as current
/// on the strength of a reply alone.
public enum Freshness: Sendable {
    case live
    case stale
    case unknown
}

/// Everything the icon needs to draw one account, and nothing else. Fractions are 0...1.
public struct QuotaIconParams: Sendable, Equatable {
    /// Weekly "all models" utilization — the outer ring.
    public var weeklyFraction: Double
    /// Session utilization — the inner pie. When `state == .spent` this is reinterpreted as the
    /// fraction of the blocking window still to run, so the pie drains toward the reset.
    public var sessionFraction: Double
    /// Weekly projected utilization at reset; the ring wedge continues from `weeklyFraction` to here.
    public var weeklyForecastFraction: Double
    /// Session projected utilization at reset; the pie sector continues from `sessionFraction` to here.
    public var sessionForecastFraction: Double
    /// `max(session, weekly)` of the committed states — what the glyph is coloured by.
    public var state: QuotaState
    public var freshness: Freshness

    public init(
        weeklyFraction: Double = 0,
        sessionFraction: Double = 0,
        weeklyForecastFraction: Double = 0,
        sessionForecastFraction: Double = 0,
        state: QuotaState = .measuring,
        freshness: Freshness = .unknown
    ) {
        self.weeklyFraction = weeklyFraction
        self.sessionFraction = sessionFraction
        self.weeklyForecastFraction = weeklyForecastFraction
        self.sessionForecastFraction = sessionForecastFraction
        self.state = state
        self.freshness = freshness
    }
}
