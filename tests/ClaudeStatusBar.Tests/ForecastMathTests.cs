using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class ForecastMathTests
{
    [Fact]
    public void WorkedExample_SessionFirstSample_IsDryEarlyImmediately()
    {
        // docs/forecast-and-states.md worked example: P=56, E=132, t_reset=168 (W=300)
        // -> r ~= 0.424 %/min, shortfall ~= 64 min -> DryEarly, and it must leave
        // Measuring immediately since this is the very first sample (nothing to debounce).
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset utcNow = resetsAt.AddMinutes(-168); // t_reset = 168 => E = 300-168 = 132

        tracker.Ingest(56.0, resetsAt.ToString("O"), utcNow, 0);

        WindowSnapshot snap = tracker.ComputeSnapshot(utcNow, 0, isWeekly: false);
        Assert.True(snap.Forecast.HasValue);
        ForecastResult f = snap.Forecast!.Value;

        Assert.Equal(0.424, f.RatePctPerMin, precision: 3);
        Assert.Equal(64.0, f.ShortfallMinutes, precision: 0);
        Assert.False(snap.Refused);

        QuotaState raw = RawStateClassifier.Classify(56.0, f, QuotaWindows.SessionMinutes, snap.Refused);
        Assert.Equal(QuotaState.DryEarly, raw);

        var hold = new HysteresisHold(weekly: false);
        QuotaState committed = hold.Step(raw, monoMs: 0);
        Assert.Equal(QuotaState.DryEarly, committed); // immediate: Measuring -> verdict bypasses hysteresis
    }

    [Fact]
    public void SixtyPercentAt_PointSixPerMin_WithFourHoursLeft_IsDryEarlyWithShortfall173()
    {
        // docs/forecast-and-states.md: "At 60% burning 0.6%/min with 4h left, the
        // shortfall is ~173 min, so that's DryEarly."
        const double windowMinutes = QuotaWindows.SessionMinutes;
        const double usedPct = 60.0;
        const double rate = 0.6;
        const double minutesToReset = 240.0;

        ForecastResult f = ForecastMath.Evaluate(usedPct, elapsedMinutes: windowMinutes - minutesToReset, rate, windowMinutes, minutesToReset);

        Assert.Equal(173.0, f.ShortfallMinutes, precision: 0);

        QuotaState raw = RawStateClassifier.Classify(usedPct, f, windowMinutes, refused: false);
        Assert.Equal(QuotaState.DryEarly, raw);
    }
}
