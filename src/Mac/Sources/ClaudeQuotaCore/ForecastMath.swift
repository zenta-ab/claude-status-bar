import Foundation

/// The pure forecast algebra from docs/forecast-and-states.md, "Forecast". Takes the envelope
/// percentage, elapsed minutes, point rate and time-to-reset as plain numbers and returns
/// everything derived from them — no window state, no ingest, so the worked examples in the spec
/// are exactly reproducible in tests.
public struct ForecastResult: Sendable, Equatable {
    public let usedPct: Double
    public let elapsedMinutes: Double
    public let ratePctPerMin: Double
    public let remainingPct: Double
    public let minutesToReset: Double
    public let minutesToDeplete: Double
    public let shortfallMinutes: Double
    public let slackMinutes: Double
    public let projectedPctAtReset: Double
    public let pace: Double
}

public enum ForecastMath {
    /// Below this, a rate is treated as zero (t_dep → ∞), not divided by.
    static let rateEpsilon = 1e-9

    public static func evaluate(
        usedPct: Double, elapsedMinutes: Double, ratePctPerMin: Double,
        windowMinutes: Double, minutesToReset: Double
    ) -> ForecastResult {
        let remaining = 100.0 - usedPct
        let timeToDeplete = ratePctPerMin > rateEpsilon ? remaining / ratePctPerMin : Double.infinity
        let shortfall = max(0.0, minutesToReset - timeToDeplete)
        let slack = timeToDeplete - minutesToReset
        let projected = usedPct + ratePctPerMin * minutesToReset   // uncapped, per contract
        let pace = windowMinutes > 0 ? ratePctPerMin / (100.0 / windowMinutes) : 0.0

        return ForecastResult(
            usedPct: usedPct,
            elapsedMinutes: elapsedMinutes,
            ratePctPerMin: ratePctPerMin,
            remainingPct: remaining,
            minutesToReset: minutesToReset,
            minutesToDeplete: timeToDeplete,
            shortfallMinutes: shortfall,
            slackMinutes: slack,
            projectedPctAtReset: projected,
            pace: pace)
    }
}
