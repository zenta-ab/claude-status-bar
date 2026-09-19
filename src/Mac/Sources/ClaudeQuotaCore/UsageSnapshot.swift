import Foundation

/// One `get_usage` reply, reduced to what the model needs. Mirrors
/// `src/Windows/Data/UsageSnapshot.cs`.
///
/// `sessionResetsAt` and `weeklyResetsAt` are the **raw strings** from the wire, not parsed
/// dates, because the exact byte sequence is the snapshot fingerprint the model dedups on
/// (docs/forecast-and-states.md, "Ingest" rule 2). Parsing and re-formatting them would destroy
/// the microsecond digits that carry the signal.
public struct UsageSnapshot: Equatable, Sendable {
    public let sessionUtilization: Double?
    public let sessionResetsAt: String?
    public let weeklyUtilization: Double?
    public let weeklyResetsAt: String?
    public let subscriptionType: String?
    /// Whether the server says this window is currently open.
    ///
    /// A 5 h session that has simply expired comes back as
    /// `{"utilization": 0, "resets_at": null, "is_active": false}`. That is a real, meaningful
    /// state — "nothing running, nothing consumed" — **not** a broken response. An earlier cut of
    /// the parser required `resets_at` and therefore rejected the whole reading, which put the
    /// menu-bar icon into "cannot read the quota" for five hours on a perfectly healthy account.
    public let sessionIsActive: Bool
    public let weeklyIsActive: Bool
    public let observedAt: Date
    /// Which reading strategy produced this snapshot -- recorded so a regression that silently
    /// falls back to the fragile path is visible rather than invisible. See `UsageParser`.
    public let source: Source

    public enum Source: String, Sendable {
        case limitsArray = "limits[]"
        case exactKeys = "exact-keys"
        case substringSearch = "substring-search"
    }

    public init(
        sessionUtilization: Double?,
        sessionResetsAt: String?,
        weeklyUtilization: Double?,
        weeklyResetsAt: String?,
        subscriptionType: String?,
        sessionIsActive: Bool = true,
        weeklyIsActive: Bool = true,
        observedAt: Date,
        source: Source
    ) {
        self.sessionUtilization = sessionUtilization
        self.sessionResetsAt = sessionResetsAt
        self.weeklyUtilization = weeklyUtilization
        self.weeklyResetsAt = weeklyResetsAt
        self.subscriptionType = subscriptionType
        self.sessionIsActive = sessionIsActive
        self.weeklyIsActive = weeklyIsActive
        self.observedAt = observedAt
        self.source = source
    }

    /// docs/forecast-and-states.md, round-1 decision 9: utilization must be finite and in
    /// [0, 100] at the boundary, or the window is treated as missing rather than as 0 %.
    public static func isValidPercentage(_ value: Double?) -> Bool {
        guard let value else { return false }
        return value.isFinite && value >= 0 && value <= 100
    }

    /// A usable percentage. A *reset* is not required: an inactive window legitimately has none.
    public var hasValidSession: Bool { Self.isValidPercentage(sessionUtilization) }
    public var hasValidWeekly: Bool { Self.isValidPercentage(weeklyUtilization) }

    /// What the forecast model needs: a percentage AND a deadline to measure against. Without a
    /// deadline the window can only be reported, never forecast — which is what
    /// docs/forecast-and-states.md's refusal rules already say.
    public var sessionIsForecastable: Bool { hasValidSession && sessionResetsAt != nil }
    public var weeklyIsForecastable: Bool { hasValidWeekly && weeklyResetsAt != nil }
}
