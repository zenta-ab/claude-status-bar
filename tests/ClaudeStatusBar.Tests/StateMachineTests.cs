using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class StateMachineTests
{
    // ---- Spent threshold (decision 15) ----

    [Fact]
    public void Spent_OnlyAt100Percent_NotAt99Point7()
    {
        // Decision 15: Spent only at P >= 100 (not the old 99.5). 97-99.9% still reaches
        // DryEarly on its own via the existing RemainingPct <= 3 clause.
        const double windowMinutes = QuotaWindows.SessionMinutes;
        ForecastResult f = ForecastMath.Evaluate(usedPct: 99.7, elapsedMinutes: 200, ratePctPerMin: 0.1, windowMinutes, minutesToReset: 100);

        QuotaState raw = RawStateClassifier.Classify(99.7, f, windowMinutes, refused: false);
        Assert.Equal(QuotaState.DryEarly, raw); // rem = 0.3 <= 3

        QuotaState atThreshold = RawStateClassifier.Classify(100.0, f, windowMinutes, refused: false);
        Assert.Equal(QuotaState.Spent, atThreshold);
    }

    // ---- Grace (decision 2: never produces Safe; only caps DryEarly at Tight within 15 min) ----

    [Fact]
    public void Grace_NeverProducesSafe_WhenPredictedDepletionPrecedesReset()
    {
        // Review finding 2's exact sequence: (04:50:00Z, 80, A) then (04:55:00Z, 99, B).
        // r ~= 0.968 %/min, remaining ~= 1%, t_dep ~= 1.03 min, t_reset = 5 min -- the raw
        // table is DryEarly (rem <= 3), and grace must NOT turn that into Safe just because
        // the reset is close: the user is still predicted to run dry ~4 min before it.
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        // Reconstruct the review's literal timestamps: window resets at 05:00:00Z.
        DateTimeOffset windowReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 4, 50, 0, TimeSpan.Zero);
        DateTimeOffset t1 = new(2026, 1, 1, 4, 55, 0, TimeSpan.Zero);

        Assert.True(tracker.Ingest(80.0, windowReset.ToString("O"), t0, 0));
        Assert.True(tracker.Ingest(99.0, windowReset.AddTicks(10_000).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture), t1, 5 * 60_000));

        WindowSnapshot snap = tracker.ComputeSnapshot(t1, 5 * 60_000, isWeekly: false);
        Assert.True(snap.Forecast.HasValue);
        ForecastResult f = snap.Forecast!.Value;
        Assert.True(f.ShortfallMinutes > 0, "the model's own prediction says depletion precedes reset");

        QuotaState raw = RawStateClassifier.Classify(f.UsedPct, f, QuotaWindows.SessionMinutes, snap.Refused);
        Assert.Equal(QuotaState.DryEarly, raw);

        QuotaState final = GraceCap.Apply(raw, minutesToReset: f.MinutesToReset);
        Assert.NotEqual(QuotaState.Safe, final);
        Assert.Equal(QuotaState.Tight, final); // capped, but never below Tight
    }

    [Fact]
    public void Grace_ResetInTwelveMinutes_WithShortfall_IsCappedAtTight()
    {
        const double windowMinutes = QuotaWindows.SessionMinutes;
        // P=98 (rem<=3) forces raw DryEarly on its own; a real shortfall is present too.
        ForecastResult f = ForecastMath.Evaluate(usedPct: 98.0, elapsedMinutes: windowMinutes - 12, ratePctPerMin: 0.5, windowMinutes, minutesToReset: 12.0);
        Assert.True(f.ShortfallMinutes > 0);

        QuotaState raw = RawStateClassifier.Classify(98.0, f, windowMinutes, refused: false);
        Assert.Equal(QuotaState.DryEarly, raw);

        QuotaState final = GraceCap.Apply(raw, minutesToReset: 12.0);
        Assert.Equal(QuotaState.Tight, final);
    }

    [Fact]
    public void Grace_NeverCapsSpent()
    {
        Assert.Equal(QuotaState.Spent, GraceCap.Apply(QuotaState.Spent, minutesToReset: 1.0));
    }

    [Fact]
    public void Grace_NeverRaisesSeverity_SafeStaysSafeNearReset()
    {
        // A cap can only ever LOWER severity, never raise it -- a genuinely Safe verdict
        // close to reset must not be bumped up to Tight just because grace is "active".
        Assert.Equal(QuotaState.Safe, GraceCap.Apply(QuotaState.Safe, minutesToReset: 2.0));
    }

    [Fact]
    public void Grace_DoesNothing_WhenResetIsMoreThanFifteenMinutesAway()
    {
        Assert.Equal(QuotaState.DryEarly, GraceCap.Apply(QuotaState.DryEarly, minutesToReset: 15.1));
    }

    // ---- Hysteresis (decision 11: accepted-observation evidence, not poll counts) ----
    // Round 2 decision 2: HysteresisHold.Step takes monotonic MILLISECONDS (long), not
    // DateTimeOffset -- every poll response it sees is always live (QuotaModel never steps
    // hysteresis from WarmStart's replay path), so a plain incrementing long stands in for
    // Environment.TickCount64 here.

    [Fact]
    public void Hysteresis_AlternatingRaw_DoesNotFlapTheCommittedState()
    {
        var hold = new HysteresisHold(weekly: false);
        long t = 0;

        QuotaState first = hold.Step(QuotaState.Tight, t); // leaving Measuring: immediate
        Assert.Equal(QuotaState.Tight, first);

        for (int i = 0; i < 20; i++)
        {
            t += 100_000;
            QuotaState raw = i % 2 == 0 ? QuotaState.Safe : QuotaState.Tight;
            hold.Step(raw, t);
        }

        Assert.Equal(QuotaState.Tight, hold.Committed); // every Safe observation was immediately cancelled by the next Tight==Committed one
    }

    /// <summary>
    /// Review finding 11's central complaint: under the OLD exact-match escalation rule,
    /// Tight and DryEarly are different pending states, so alternating between them never
    /// accumulated 2 consecutive agreements and Safe stayed committed indefinitely. The new
    /// rule looks at the last N accepted observations and commits to the LOWEST one that is
    /// still above Committed -- two different-but-both-escalating raw states now count
    /// together as evidence.
    /// </summary>
    [Fact]
    public void Hysteresis_Escalation_CommitsToLowestSeverityAboveCommitted_EvenWhenRawAlternates()
    {
        var hold = new HysteresisHold(weekly: false);
        long t = 0;
        hold.Step(QuotaState.Safe, t); // Committed = Safe (leaving Measuring, bypass)

        t += 100_000;
        QuotaState afterFirst = hold.Step(QuotaState.Tight, t);
        Assert.Equal(QuotaState.Safe, afterFirst); // only 1 of 2 required observations so far

        t += 100_000;
        QuotaState afterSecond = hold.Step(QuotaState.DryEarly, t);
        Assert.Equal(QuotaState.Tight, afterSecond); // commits to min(Tight, DryEarly), not DryEarly and not stuck on Safe
    }

    [Fact]
    public void Hysteresis_Escalation_InterruptedByAnAgreeingObservation_ResetsEvidence()
    {
        var hold = new HysteresisHold(weekly: false);
        long t = 0;
        hold.Step(QuotaState.Safe, t);

        t += 100_000;
        hold.Step(QuotaState.Tight, t); // 1 of 2

        t += 100_000;
        hold.Step(QuotaState.Safe, t); // back to Committed: resets escalation evidence

        t += 100_000;
        QuotaState after = hold.Step(QuotaState.Tight, t); // only 1 of 2 again
        Assert.Equal(QuotaState.Safe, after);
    }

    [Fact]
    public void Hysteresis_Deescalation_Session_RequiresThreeAcceptedObservations_AndTenMinutesElapsed()
    {
        var hold = new HysteresisHold(weekly: false);
        const long t0 = 0;
        hold.Step(QuotaState.Tight, t0); // Committed = Tight, LastChangeMono = t0

        const long t1 = 200_000;
        hold.Step(QuotaState.Safe, t1); // evidence 1/3
        const long t2 = 400_000;
        hold.Step(QuotaState.Safe, t2); // evidence 2/3
        const long t3 = 600_000;
        QuotaState afterThird = hold.Step(QuotaState.Safe, t3); // evidence 3/3, but only 400s since t1 (< 10 min)
        Assert.Equal(QuotaState.Tight, afterThird);

        const long t4 = 800_000; // 600s (== 10 min) since t1: both gates now satisfied
        QuotaState afterFourth = hold.Step(QuotaState.Safe, t4);
        Assert.Equal(QuotaState.Safe, afterFourth);
    }

    [Fact]
    public void Hysteresis_Deescalation_Weekly_RequiresFourAcceptedObservations_AndTwoHoursElapsed()
    {
        var hold = new HysteresisHold(weekly: true);
        const long t0 = 0;
        hold.Step(QuotaState.Tight, t0);

        long t1 = (long)TimeSpan.FromMinutes(25).TotalMilliseconds;
        hold.Step(QuotaState.Safe, t1); // 1/4
        long t2 = (long)TimeSpan.FromMinutes(50).TotalMilliseconds;
        hold.Step(QuotaState.Safe, t2); // 2/4
        long t3 = (long)TimeSpan.FromMinutes(75).TotalMilliseconds;
        hold.Step(QuotaState.Safe, t3); // 3/4
        long t4 = (long)TimeSpan.FromMinutes(100).TotalMilliseconds;
        QuotaState afterFour = hold.Step(QuotaState.Safe, t4); // 4/4, but only 75 min since t1 (< 2h)
        Assert.Equal(QuotaState.Tight, afterFour);

        long t5 = (long)TimeSpan.FromMinutes(125).TotalMilliseconds;
        QuotaState afterFive = hold.Step(QuotaState.Safe, t5); // 100 min since t1, still < 2h
        Assert.Equal(QuotaState.Tight, afterFive);

        long t6 = (long)TimeSpan.FromMinutes(150).TotalMilliseconds; // 125 min since t1: >= 2h now, evidence long since satisfied
        QuotaState afterSix = hold.Step(QuotaState.Safe, t6);
        Assert.Equal(QuotaState.Safe, afterSix);
    }

    /// <summary>
    /// The de-escalation minimum-elapsed-time requirement (10 min / 2h) already exceeds the
    /// cooldown (90s / 5min) in every realistic case, so the cooldown's remaining
    /// observable purpose is blocking a RAPID ESCALATION even once its (much smaller)
    /// evidence-count threshold is met.
    /// </summary>
    [Fact]
    public void Hysteresis_CooldownBlocksRapidEscalation_EvenWhenEvidenceThresholdIsMet()
    {
        var hold = new HysteresisHold(weekly: false);
        const long t0 = 0;
        hold.Step(QuotaState.Safe, t0); // Committed = Safe, LastChangeMono = t0 (bypass, leaving Measuring)

        const long t1 = 10_000;
        hold.Step(QuotaState.Tight, t1); // 1/2

        const long t2 = 20_000; // evidence now 2/2, but only 20s since t0 (< 90s cooldown)
        QuotaState afterCooldownBlock = hold.Step(QuotaState.Tight, t2);
        Assert.Equal(QuotaState.Safe, afterCooldownBlock);

        const long t3 = 100_000; // past the 90s cooldown, still agreeing
        QuotaState afterCooldownElapsed = hold.Step(QuotaState.Tight, t3);
        Assert.Equal(QuotaState.Tight, afterCooldownElapsed);
    }

    // ---- decision 4 (round 2): leaving Measuring on live time/eligibility alone ----

    [Fact]
    public void CommitFromMeasuring_NoOp_WhenAlreadyCommittedToAVerdict()
    {
        var hold = new HysteresisHold(weekly: false);
        hold.Step(QuotaState.Safe, 0); // leaves Measuring immediately

        QuotaState result = hold.CommitFromMeasuring(QuotaState.DryEarly, 1000);
        Assert.Equal(QuotaState.Safe, result); // must not silently override an already-committed verdict
    }

    [Fact]
    public void CommitFromMeasuring_CommitsImmediately_WhenStillMeasuring()
    {
        var hold = new HysteresisHold(weekly: false);
        Assert.Equal(QuotaState.Measuring, hold.Committed);

        QuotaState result = hold.CommitFromMeasuring(QuotaState.Tight, 5000);
        Assert.Equal(QuotaState.Tight, result);
        Assert.Equal(QuotaState.Tight, hold.Committed);
    }
}
