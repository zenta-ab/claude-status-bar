using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class WindowTrackerTests
{
    static readonly DateTimeOffset ResetT = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);

    /// <summary>Distinct fingerprint strings that all parse to (almost exactly) the same instant,
    /// mirroring the wire format's 6-digit-fraction jitter between polls without moving resets_at.</summary>
    static string Fingerprint(DateTimeOffset instant, int jitterIndex) =>
        instant.AddTicks(jitterIndex * 13).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void ConstantRate_IsEstimatedExactly_UnderIrregularPollSpacing()
    {
        // r0 = 0.5 %/min, session window. Points placed exactly on P = r0 * E from
        // the first (seeded) sample onward: dt of 30s, 7min, 90s, 12min between polls.
        const double r0 = 0.5;
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);

        double[] cumulativeMinutes = { 0.0, 0.5, 7.5, 9.0, 21.0 }; // seed, then +30s, +7min, +90s, +12min
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280); // E0 = 300 - 280 = 20

        for (int k = 0; k < cumulativeMinutes.Length; k++)
        {
            DateTimeOffset utcNow = utcNow0.AddMinutes(cumulativeMinutes[k]);
            double pct = 10.0 + r0 * cumulativeMinutes[k]; // P0=10 at E0=20 -> r_wtd0 = 0.5 = r0
            long monoMs = (long)(cumulativeMinutes[k] * 60_000);
            Assert.True(tracker.Ingest(pct, Fingerprint(ResetT, k), utcNow, monoMs));
        }

        DateTimeOffset finalUtc = utcNow0.AddMinutes(cumulativeMinutes[^1]);
        long finalMono = (long)(cumulativeMinutes[^1] * 60_000);
        WindowSnapshot snap = tracker.ComputeSnapshot(finalUtc, finalMono, isWeekly: false);

        Assert.True(snap.Forecast.HasValue);
        Assert.Equal(r0, snap.Forecast!.Value.RatePctPerMin, precision: 6);
    }

    /// <summary>
    /// Review finding: "the constant-rate test examines max(EWMA,WTD), where WTD is
    /// constructed to equal the answer. It does not independently verify the EWMA
    /// behavior." This test reads Sp/St directly, bypassing ComputeSnapshot's blend
    /// entirely, and deliberately seeds a WTD that DISAGREES with the recent rate (an
    /// inflated first jump), so the assertion can only pass if the EWMA accumulator
    /// itself converges correctly, not because the two estimators happen to agree.
    /// </summary>
    [Fact]
    public void Ewma_ConvergesToRecentRate_IndependentlyOfWindowToDatePace()
    {
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280); // E0 = 20

        Assert.True(tracker.Ingest(40.0, Fingerprint(ResetT, 0), utcNow0, 0)); // seed: WTD = 40/20 = 2.0 %/min

        const double recentRate = 0.1; // %/min -- far below the seeded WTD
        DateTimeOffset t = utcNow0;
        double p = 40.0;
        for (int k = 1; k <= 20; k++) // 20*10min = 200min ~= 9.2 half-lives (tau ~21.64min): fully converged
        {
            t = t.AddMinutes(10);
            p += recentRate * 10.0;
            Assert.True(tracker.Ingest(p, Fingerprint(ResetT, k), t, (long)(t - utcNow0).TotalMilliseconds));
        }

        double rEwma = tracker.Sp / tracker.St;
        Assert.Equal(recentRate, rEwma, precision: 3);

        // Confirm this genuinely differs from what WTD alone reports at this instant --
        // otherwise the test would not distinguish the two estimators at all.
        double e = QuotaWindows.SessionMinutes - (ResetT - t).TotalMinutes;
        double rWtd = tracker.EnvelopeP / e;
        Assert.True(rWtd - recentRate > 0.15, $"WTD ({rWtd:F3}) should still be dragged up by the initial jump, unlike the converged EWMA ({rEwma:F4})");
    }

    [Fact]
    public void RepeatedFingerprint_IsNotIngested_EstimateUnchanged()
    {
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280);
        string fp = Fingerprint(ResetT, 0);

        Assert.True(tracker.Ingest(20.0, fp, utcNow0, 0));
        double spAfterFirst = tracker.Sp;
        double stAfterFirst = tracker.St;
        int countAfterFirst = tracker.SampleCount;
        double envelopeAfterFirst = tracker.EnvelopeP;

        // Same fingerprint again, later in time and with a different (even higher) pct:
        // must be skipped entirely -- stale cache / replica revert of an already-seen snapshot.
        Assert.False(tracker.Ingest(99.0, fp, utcNow0.AddMinutes(5), 300_000));

        Assert.Equal(spAfterFirst, tracker.Sp);
        Assert.Equal(stAfterFirst, tracker.St);
        Assert.Equal(countAfterFirst, tracker.SampleCount);
        Assert.Equal(envelopeAfterFirst, tracker.EnvelopeP);
    }

    /// <summary>
    /// Review finding: dedup is symmetric and unconditional on the fingerprint's identity,
    /// regardless of whether the repeated payload is lower OR higher than the current
    /// envelope -- not a coincidence of "lower values happen to be safe to ignore". A
    /// same-fingerprint echo carrying a wildly higher value (99, vs envelope 15) must be
    /// just as inert as one carrying a lower value: both are the same already-seen snapshot,
    /// not new evidence in either direction.
    /// </summary>
    [Fact]
    public void ReplicaRegression_EnvelopeHolds_NoRollover_EvenWithPreviouslySeenFingerprint()
    {
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280);
        string fpA = Fingerprint(ResetT, 0);
        string fpB = Fingerprint(ResetT, 1);

        Assert.True(tracker.Ingest(15.0, fpA, utcNow0, 0));                       // envelope -> 15
        Assert.True(tracker.Ingest(14.0, fpB, utcNow0.AddMinutes(1), 60_000));     // new fingerprint, lower pct: replica skew

        Assert.Equal(15.0, tracker.EnvelopeP);
        DateTimeOffset? keyAfterB = tracker.WindowKey;

        // fpA reappears (cached/reverted replica) with yet another lower value -- deduped, no effect.
        Assert.False(tracker.Ingest(13.0, fpA, utcNow0.AddMinutes(2), 120_000));

        Assert.Equal(15.0, tracker.EnvelopeP);
        Assert.Equal(keyAfterB, tracker.WindowKey); // no rollover: same window key throughout
        Assert.Null(tracker.RolloverAt);
        Assert.Equal(2, tracker.SampleCount); // fpA's second occurrence was deduped, not counted

        // fpA reappears yet again, this time with a payload FAR ABOVE the envelope: still
        // deduped, symmetric with the lower-value case above -- the fingerprint's identity
        // is what is being deduped, not "is this value plausible".
        Assert.False(tracker.Ingest(99.0, fpA, utcNow0.AddMinutes(3), 180_000));
        Assert.Equal(15.0, tracker.EnvelopeP);
        Assert.Equal(2, tracker.SampleCount);
    }

    [Fact]
    public void Weekly_IsMeasuring_WhenElapsedIsBelowTwentyFourHours()
    {
        // Real situation as of 2026-09-11: the weekly window reset this morning, so
        // E is far below the weekly 24h (1440 min) minimum.
        var weekly = new WindowTracker(QuotaWindows.WeeklyMinutes, halfLifeMinutes: 8 * 60, isWeekly: true);
        DateTimeOffset resetsAt = new(2026, 9, 18, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset utcNow = resetsAt.AddMinutes(-(QuotaWindows.WeeklyMinutes - 200)); // E = 200 < 1440

        Assert.True(weekly.Ingest(10.0, resetsAt.ToString("O"), utcNow, 0));

        WindowSnapshot snap = weekly.ComputeSnapshot(utcNow, 0, isWeekly: true);
        Assert.True(snap.Refused);
        Assert.Equal(MeasuringReasonCode.TooEarlyInWeek, snap.ReasonCode);
        Assert.True(snap.Forecast.HasValue);

        QuotaState raw = RawStateClassifier.Classify(10.0, snap.Forecast!.Value, QuotaWindows.WeeklyMinutes, snap.Refused);
        Assert.Equal(QuotaState.Measuring, raw);
    }

    /// <summary>
    /// The real false alarm: weekly window reset at 07:00, 11% used by ~12:40 (E ~= 342 min --
    /// just past the OLD W/30 = 336 min threshold). The weekly window went straight to
    /// DryEarly, extrapolating a Friday-morning-only working pace through the coming nights
    /// and weekend. The weekly window now needs a full day/night cycle (E &gt;= 1440 min, not
    /// W/30) before a seed -- and therefore a verdict -- is trusted. Session is unaffected.
    /// </summary>
    [Fact]
    public void Weekly_FalseAlarmOnDayOne_StaysMeasuring_UntilAFullDayNightCycleHasElapsed()
    {
        DateTimeOffset resetStart = new(2026, 9, 11, 7, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = resetStart.AddDays(7);

        var weekly = new WindowTracker(QuotaWindows.WeeklyMinutes, halfLifeMinutes: 8 * 60, isWeekly: true);
        DateTimeOffset stillEarly = resetStart.AddMinutes(342); // E = 342: past the old 336min threshold, short of 1440
        Assert.True(weekly.Ingest(11.0, resetsAt.ToString("O"), stillEarly, 0));

        WindowSnapshot snapEarly = weekly.ComputeSnapshot(stillEarly, 0, isWeekly: true);
        Assert.True(snapEarly.Refused);
        Assert.Equal(MeasuringReasonCode.TooEarlyInWeek, snapEarly.ReasonCode);

        QuotaState rawEarly = RawStateClassifier.Classify(11.0, snapEarly.Forecast!.Value, QuotaWindows.WeeklyMinutes, snapEarly.Refused);
        Assert.Equal(QuotaState.Measuring, rawEarly);

        // Past a full day/night cycle, with a proportionate P (same implied pace, scaled to
        // the larger E): a verdict is now allowed.
        var weeklyAfterADay = new WindowTracker(QuotaWindows.WeeklyMinutes, halfLifeMinutes: 8 * 60, isWeekly: true);
        DateTimeOffset pastADay = resetStart.AddMinutes(1441); // E = 1441
        double proportionateP = 11.0 * (1441.0 / 342.0);
        Assert.True(weeklyAfterADay.Ingest(proportionateP, resetsAt.ToString("O"), pastADay, 0));

        WindowSnapshot snapAllowed = weeklyAfterADay.ComputeSnapshot(pastADay, 0, isWeekly: true);
        Assert.False(snapAllowed.Refused);
        Assert.NotEqual(MeasuringReasonCode.TooEarlyInWeek, snapAllowed.ReasonCode);
    }

    // ---- decision 10: the WTD floor decays with confirmed idle time ----

    [Fact]
    public void SessionFloor_DecaysExponentially_WithConfirmedIdleTime()
    {
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280); // E0 = 20

        Assert.True(tracker.Ingest(20.0, Fingerprint(ResetT, 0), utcNow0, 0));                       // seed: r_wtd = 20/20 = 1.0
        DateTimeOffset lastRise = utcNow0.AddMinutes(1);
        Assert.True(tracker.Ingest(25.0, Fingerprint(ResetT, 1), lastRise, 60_000));                  // burst: dP=5 over 1 min, LastRiseAt = lastRise

        // 40 minutes idle: same pct (a fresh poll, unchanged usage), fingerprint still moves --
        // "confirmed by novel fingerprints without a rise", per decision 10.
        DateTimeOffset idleUtc = utcNow0.AddMinutes(41);
        Assert.True(tracker.Ingest(25.0, Fingerprint(ResetT, 2), idleUtc, 41 * 60_000));

        WindowSnapshot snap = tracker.ComputeSnapshot(idleUtc, 41 * 60_000, isWeekly: false);
        Assert.True(snap.Forecast.HasValue);
        ForecastResult f = snap.Forecast!.Value;

        double rWtdNow = tracker.EnvelopeP / f.ElapsedMinutes; // 25 / 61
        double idleMinutes = (idleUtc - lastRise).TotalMinutes; // 40
        double expectedFloor = rWtdNow * Math.Exp(-idleMinutes / 30.0); // decision 10's formula

        double rEwmaNow = tracker.Sp / tracker.St;
        Assert.True(rEwmaNow < expectedFloor, "the decayed EWMA should still be below the (already-decayed) floor at 40 min idle");
        Assert.True(expectedFloor < rWtdNow, "the floor must have decayed below the raw window-to-date pace");
        Assert.Equal(expectedFloor, f.RatePctPerMin, precision: 9); // r == the decayed floor, not the raw WTD pace and not the (lower) EWMA
    }

    [Fact]
    public void SessionFloor_NoLongerForcesDryEarly_AfterLongConfirmedIdle()
    {
        // Review finding 10, exact numbers: 60% used, then confirmed zero consumption
        // (fresh fingerprints, unchanged pct) for 110 minutes. The OLD hard floor
        // (r >= WTD unconditionally) kept this DryEarly with ~16.7 min of "shortfall" that
        // was pure historical average, not a live prediction. The decayed floor must have
        // relaxed enough by then that the raw state is no longer DryEarly.
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset seedAt = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = seedAt.AddMinutes(300);

        Assert.True(tracker.Ingest(60.0, resetsAt.ToString("O"), seedAt, 0));

        DateTimeOffset t = seedAt;
        long tMono = 0;
        for (int k = 1; k <= 22; k++) // every 5 min for 110 min, unchanged pct, novel fingerprint each time
        {
            t = t.AddMinutes(5);
            tMono = (long)(t - seedAt).TotalMilliseconds;
            Assert.True(tracker.Ingest(60.0, Fingerprint(resetsAt, k), t, tMono));
        }

        WindowSnapshot snap = tracker.ComputeSnapshot(t, tMono, isWeekly: false);
        Assert.True(snap.Forecast.HasValue);
        QuotaState raw = RawStateClassifier.Classify(60.0, snap.Forecast!.Value, QuotaWindows.SessionMinutes, snap.Refused);
        Assert.NotEqual(QuotaState.DryEarly, raw);
    }

    /// <summary>
    /// Review finding 7's exact trace: a novel, unchanged-pct poll at +40min confirms 40
    /// minutes of idle. A LATER evaluation with NO further poll in between (the defining
    /// negative case the old test suite never exercised -- it always supplied a confirming
    /// sample right at the evaluation instant) must not silently decay the floor any further,
    /// since no new evidence arrived. Idle is frozen at the last accepted observation, not
    /// re-derived against the live evaluation clock.
    /// </summary>
    [Fact]
    public void ConfirmedIdle_DoesNotDecayFurther_OnAnEvaluationWithNoNewPollInBetween()
    {
        // Reuses SessionFloor_DecaysExponentially's proven setup, which establishes the floor
        // as the BINDING term (rEwma decayed below it via a real EWMA decay across several
        // accepted samples, not merely a frozen seed value equal to WTD) -- otherwise `r =
        // max(rEwma, floor)` can silently pick rEwma regardless of whether the floor's idle
        // computation is correct, and this test would not actually distinguish old from new
        // behavior.
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset utcNow0 = ResetT.AddMinutes(-280); // E0 = 20: an immediate valid seed

        Assert.True(tracker.Ingest(20.0, Fingerprint(ResetT, 0), utcNow0, 0));           // seed: r_wtd = 20/20 = 1.0
        DateTimeOffset lastRise = utcNow0.AddMinutes(1);
        Assert.True(tracker.Ingest(25.0, Fingerprint(ResetT, 1), lastRise, 60_000));     // burst: dP=5 over 1 min, LastRiseAt = lastRise

        DateTimeOffset confirmAt = utcNow0.AddMinutes(41); // 40 min idle since the rise, one confirming unchanged sample
        long confirmMono = 41 * 60_000;
        Assert.True(tracker.Ingest(25.0, Fingerprint(ResetT, 2), confirmAt, confirmMono));

        WindowSnapshot atConfirm = tracker.ComputeSnapshot(confirmAt, confirmMono, isWeekly: false);
        double rateAtConfirm = atConfirm.Forecast!.Value.RatePctPerMin;
        double rEwmaAtConfirm = tracker.Sp / tracker.St;
        Assert.True(rEwmaAtConfirm < rateAtConfirm, "test setup sanity: the floor must be the binding term, not rEwma");

        // No further Ingest -- just a later evaluation, exactly like the always-running 1s UI
        // tick between polls. rWtd itself legitimately keeps moving with elapsed time (that is
        // correct and unrelated to this bug), so the raw rate cannot be compared bit-for-bit
        // against rateAtConfirm -- instead, recompute what the rate SHOULD be if idle stayed
        // frozen at 40 min (correct) versus what it would be if idle kept growing to 50 min
        // (the old bug), and assert against the frozen expectation.
        DateTimeOffset laterAt = utcNow0.AddMinutes(51);
        long laterMono = 51 * 60_000;
        WindowSnapshot atLater = tracker.ComputeSnapshot(laterAt, laterMono, isWeekly: false);
        double rateAtLater = atLater.Forecast!.Value.RatePctPerMin;

        double rWtdAtLater = tracker.EnvelopeP / atLater.Forecast!.Value.ElapsedMinutes;
        double expectedIfFrozenAt40MinIdle = rWtdAtLater * Math.Exp(-40.0 / 30.0);
        double whatTheOldBugWouldHaveProduced = rWtdAtLater * Math.Exp(-50.0 / 30.0); // idle kept growing with utcNow

        Assert.Equal(expectedIfFrozenAt40MinIdle, rateAtLater, precision: 6);
        Assert.NotEqual(whatTheOldBugWouldHaveProduced, rateAtLater);
    }

    /// <summary>
    /// Companion negative cases (test critique): a duplicate poll (rejected, not accepted) and
    /// a poll tick with literally no Ingest call at all must both leave idle exactly as frozen
    /// at the last ACCEPTED sample -- neither a rejected duplicate nor mere ticking must move it.
    /// </summary>
    [Fact]
    public void ConfirmedIdle_UnaffectedByARejectedDuplicatePoll_BetweenEvaluations()
    {
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset seedAt = new(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = seedAt.AddMinutes(300);
        string fingerprint = resetsAt.ToString("O");

        Assert.True(tracker.Ingest(60.0, fingerprint, seedAt, 0));

        DateTimeOffset dupAt = seedAt.AddMinutes(20);
        Assert.False(tracker.Ingest(60.0, fingerprint, dupAt, 20 * 60_000)); // same fingerprint: rejected, not accepted

        WindowSnapshot snap = tracker.ComputeSnapshot(dupAt, 20 * 60_000, isWeekly: false);
        Assert.Equal(0.0, tracker.ConfirmedIdleMinutes); // no live accepted rise-then-confirm sequence has happened yet -- stays at its safe default, unmoved by the duplicate
        Assert.True(snap.Forecast.HasValue);
    }
}
