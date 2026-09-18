namespace ClaudeStatusBar.Model;

/// <summary>
/// The pure forecast algebra from docs/forecast-and-states.md, "Forecast". Takes
/// the envelope percentage, elapsed minutes, point rate and time-to-reset as
/// plain numbers and returns everything derived from them -- no window state,
/// no ingest, so it is trivial to hit exact worked-example numbers in tests.
/// </summary>
public readonly record struct ForecastResult(
    double UsedPct,
    double ElapsedMinutes,
    double RatePctPerMin,
    double RemainingPct,
    double MinutesToReset,
    double MinutesToDeplete,
    double ShortfallMinutes,
    double SlackMinutes,
    double ProjectedPctAtReset,
    double Pace);

public static class ForecastMath
{
    /// <summary>Below this, a rate is treated as zero (t_dep -> infinity), not divided by.</summary>
    const double RateEpsilon = 1e-9;

    public static ForecastResult Evaluate(double usedPct, double elapsedMinutes, double ratePctPerMin, double windowMinutes, double minutesToReset)
    {
        double rem = 100.0 - usedPct;
        double tDep = ratePctPerMin > RateEpsilon ? rem / ratePctPerMin : double.PositiveInfinity;
        double shortfall = Math.Max(0.0, minutesToReset - tDep);
        double slack = tDep - minutesToReset;
        double projected = usedPct + ratePctPerMin * minutesToReset; // uncapped, per contract
        double pace = windowMinutes > 0 ? ratePctPerMin / (100.0 / windowMinutes) : 0.0;

        return new ForecastResult(
            UsedPct: usedPct,
            ElapsedMinutes: elapsedMinutes,
            RatePctPerMin: ratePctPerMin,
            RemainingPct: rem,
            MinutesToReset: minutesToReset,
            MinutesToDeplete: tDep,
            ShortfallMinutes: shortfall,
            SlackMinutes: slack,
            ProjectedPctAtReset: projected,
            Pace: pace);
    }
}
