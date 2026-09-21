using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/statistics.md: the cycle archive (cycles.csv/hourly-*.csv), the live QuotaModel hooks
/// that produce them, the backfill that derives them from window-shape-*.csv history, and the
/// pure StatisticsEngine that reads them back. Rewritten 2026-09-21 against
/// docs/reviews/2026-09-21-codex-statistics.md's 32 findings and their decisions table -- every
/// section below is annotated with the finding(s) it proves fixed.
/// </summary>
public class StatisticsTests
{
    static string RolloverFixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "rollover-2026-09-11.csv");
    static readonly TimeSpan Zero = TimeSpan.Zero;

    static string Raw(DateTimeOffset instant, int jitter = 0) =>
        instant.AddTicks(jitter * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", CultureInfo.InvariantCulture);

    static CsvReplay.RawRow SessionRow(DateTimeOffset utc, DateTimeOffset resetsAt, double pct) =>
        new(utc, 0, pct, resetsAt.ToString("O", CultureInfo.InvariantCulture), null, null);

    // ==================== Live cycle detection (WindowTracker/QuotaModel) ====================

    [Fact]
    public void ClosedCycle_EveryColumn_MatchesKnownAnswers_FromASyntheticSequence()
    {
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);

        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddMinutes(5);
        DateTimeOffset t2 = t0.AddMinutes(10); // envelope reaches 100 here, under continuous observation (gap from t1 is 5min, within the allowance) -- an EXACT crossing
        DateTimeOffset t3 = t0.AddMinutes(15); // dips to 95 (replica skew) -- the LAST accepted sample before rollover

        model.Ingest(new UsageSnapshot(10.0, Raw(resetA, 0), null, null, t0, SubscriptionType: "max"), t0, 0);
        Assert.Empty(model.TakeClosedCycles());
        model.Ingest(new UsageSnapshot(20.0, Raw(resetA, 1), null, null, t1, SubscriptionType: "max"), t1, 5 * 60_000);
        Assert.Empty(model.TakeClosedCycles());
        model.Ingest(new UsageSnapshot(100.0, Raw(resetA, 2), null, null, t2, SubscriptionType: "max"), t2, 10 * 60_000);
        Assert.Empty(model.TakeClosedCycles());
        model.Ingest(new UsageSnapshot(95.0, Raw(resetA, 3), null, null, t3, SubscriptionType: "max"), t3, 15 * 60_000);
        Assert.Empty(model.TakeClosedCycles());

        // The last accepted (jittered) fingerprint's own instant -- reparsed from the exact same
        // 6-fractional-digit wire string WindowKey is built from, so a sub-microsecond rounding
        // difference between AddTicks and the string round-trip can never cause a false mismatch.
        DateTimeOffset expectedResetUtc = DateTimeOffset.Parse(
            Raw(resetA, 3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
        DateTimeOffset resetB = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset t4 = resetA.AddSeconds(10); // a genuine rollover: past the old deadline, well within jitter of "due"
        model.Ingest(new UsageSnapshot(5.0, resetB.ToString("O"), null, null, t4, SubscriptionType: "max"), t4, (long)(t4 - t0).TotalMilliseconds);

        IReadOnlyList<CycleClosed> closed = model.TakeClosedCycles();
        CycleClosed cycle = Assert.Single(closed);

        Assert.Equal(WindowKind.Session, cycle.Kind);
        Assert.Equal(expectedResetUtc, cycle.ResetUtc);
        Assert.Equal(expectedResetUtc.AddMinutes(-QuotaWindows.SessionMinutes), cycle.StartedUtc);
        Assert.Equal(100.0, cycle.PeakPct); // the monotone envelope's max
        // Codex review #15: FinalPct is the FINAL ENVELOPE value (100), never the raw last
        // reading (95) -- the whole premise of the monotone envelope is that usage never truly
        // decreases, so "final" must go through it exactly like "peak" does.
        Assert.Equal(100.0, cycle.FinalPct);
        Assert.True(cycle.HitCeiling);
        Assert.Equal(290.0, cycle.BlockedMinutes, 3); // resetA(05:00) - ceiling-reached(t2=00:10) = 4h50m
        Assert.Equal(15.0, cycle.CoveredMinutes, 3); // 5+5+5 between t0..t3, none of the gaps beyond the allowance
        Assert.Equal("max", cycle.PlanTier);
        Assert.False(cycle.WarnedDryEarly); // nothing ever drove hysteresis to DryEarly in this sequence
        Assert.Null(cycle.PredictedPeakPct); // no Evaluate() ran at/after 50% elapsed before the close
        Assert.Null(cycle.PredictedAtUtc);

        // Codex review #14: reached under CONTINUOUS observation (the t1->t2 gap was 5min, well
        // inside the continuity allowance) -- an EXACT crossing, never censored.
        Assert.Equal(t2, cycle.CeilingReachedAtUtc);
        Assert.False(cycle.CeilingReachedCensored);

        // The still-open new window (resetB) must not itself have produced a spurious cycle.
        Assert.Empty(model.TakeClosedCycles());
    }

    [Fact]
    public void FinalPct_ReplicaRegressionBeforeRollover_NeverRecordsTheRegressedValue()
    {
        // Codex review #15's exact input: 20, 100, then replica-skewed 95 before rollover.
        // Result before the fix: peak_pct=100, final_pct=95. The feature's whole premise is that
        // usage never truly decreases -- final_pct must go through the same monotone envelope.
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddMinutes(5);
        DateTimeOffset t2 = t0.AddMinutes(10);

        model.Ingest(new UsageSnapshot(20.0, Raw(resetA, 0), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(100.0, Raw(resetA, 1), null, null, t1), t1, 5 * 60_000);
        model.Ingest(new UsageSnapshot(95.0, Raw(resetA, 2), null, null, t2), t2, 10 * 60_000);

        DateTimeOffset resetB = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset rolloverAt = resetA.AddSeconds(10);
        model.Ingest(new UsageSnapshot(5.0, resetB.ToString("O"), null, null, rolloverAt), rolloverAt, (long)(rolloverAt - t0).TotalMilliseconds);

        CycleClosed cycle = Assert.Single(model.TakeClosedCycles());
        Assert.Equal(100.0, cycle.PeakPct);
        Assert.Equal(100.0, cycle.FinalPct); // never 95
    }

    [Fact]
    public void CoveredMinutes_AGapBeyondTheContinuityAllowance_ContributesNothing_NeverCapped()
    {
        // Codex review #17: a gap larger than the continuity allowance must contribute ZERO
        // covered minutes, never be capped down to the allowance (which used to fabricate up to
        // 10 minutes of "coverage" for an outage of any length).
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);

        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddMinutes(5);
        DateTimeOffset t2 = t1.AddMinutes(5);
        DateTimeOffset t3 = t2.AddMinutes(45); // the deliberate gap: the app was "off" for 45 real minutes
        DateTimeOffset t4 = t3.AddMinutes(5);

        long Mono(DateTimeOffset t) => (long)(t - t0).TotalMilliseconds;
        model.Ingest(new UsageSnapshot(10.0, Raw(resetA, 0), null, null, t0), t0, Mono(t0));
        model.Ingest(new UsageSnapshot(20.0, Raw(resetA, 1), null, null, t1), t1, Mono(t1));
        model.Ingest(new UsageSnapshot(30.0, Raw(resetA, 2), null, null, t2), t2, Mono(t2));
        model.Ingest(new UsageSnapshot(40.0, Raw(resetA, 3), null, null, t3), t3, Mono(t3));
        model.Ingest(new UsageSnapshot(50.0, Raw(resetA, 4), null, null, t4), t4, Mono(t4));

        DateTimeOffset resetB = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset rolloverAt = resetA.AddSeconds(10);
        model.Ingest(new UsageSnapshot(5.0, resetB.ToString("O"), null, null, rolloverAt), rolloverAt, Mono(rolloverAt));

        CycleClosed cycle = Assert.Single(model.TakeClosedCycles());
        // 5 + 5 + 0 (the 45-minute gap, beyond the allowance) + 5 = 15, never the old capped-to-10 total of 25.
        Assert.Equal(15.0, cycle.CoveredMinutes, 3);
        Assert.Equal(50.0, cycle.PeakPct);
    }

    [Fact]
    public void CoveredMinutes_ASixtyMinuteOutage_ContributesZero()
    {
        // Finding #17's own more adversarial example: "a 60-minute outage contributes ten
        // covered minutes" under the old cap. Must now contribute exactly zero.
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddMinutes(60);

        model.Ingest(new UsageSnapshot(10.0, Raw(resetA, 0), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(20.0, Raw(resetA, 1), null, null, t1), t1, (long)(t1 - t0).TotalMilliseconds);

        DateTimeOffset resetB = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset rolloverAt = resetA.AddSeconds(10);
        model.Ingest(new UsageSnapshot(5.0, resetB.ToString("O"), null, null, rolloverAt), rolloverAt, (long)(rolloverAt - t0).TotalMilliseconds);

        CycleClosed cycle = Assert.Single(model.TakeClosedCycles());
        Assert.Equal(0.0, cycle.CoveredMinutes, 3);
    }

    [Fact]
    public void HourlyRollup_ConsumedPctIsSumOfPositiveDeltas_ExcludingTheFirstObservationOfTheHour()
    {
        // Codex review #3/#27: the FIRST observation of an hour bucket re-baselines and
        // contributes zero -- the old test (finding #27) expected the initial 10% to be counted,
        // which is exactly the bug: that 10% may have accumulated before logging began.
        var model = new QuotaModel();
        DateTimeOffset farReset = new(2026, 1, 8, 0, 0, 0, TimeSpan.Zero); // far away -- no rollover in this test

        DateTimeOffset t0 = new(2026, 1, 1, 0, 50, 0, TimeSpan.Zero); // hour bucket 00:00-01:00
        DateTimeOffset t1 = t0.AddMinutes(5);                          // still hour 00
        DateTimeOffset t2 = t0.AddMinutes(15);                         // 01:05 -- crosses into hour 01, closing hour 00's bucket

        long Mono(DateTimeOffset t) => (long)(t - t0).TotalMilliseconds;
        model.Ingest(new UsageSnapshot(10.0, Raw(farReset, 0), null, null, t0), t0, Mono(t0));
        Assert.Empty(model.TakeClosedHours());
        model.Ingest(new UsageSnapshot(25.0, Raw(farReset, 1), null, null, t1), t1, Mono(t1)); // dP=15, within the continuity allowance
        Assert.Empty(model.TakeClosedHours());
        model.Ingest(new UsageSnapshot(30.0, Raw(farReset, 2), null, null, t2), t2, Mono(t2)); // dP=5, but belongs to the NEW hour

        HourClosed hour = Assert.Single(model.TakeClosedHours());
        Assert.Equal(WindowKind.Session, hour.Kind);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), hour.HourStartUtc);
        // Never 25 (10+15): the FIRST observation (envelope 0->10 at t0) is a re-baseline, never
        // credited -- only t1's genuine +15 rise, observed under continuity, counts.
        Assert.Equal(15.0, hour.ConsumedPct, 3);
        Assert.Equal(2, hour.Samples);
        Assert.Equal(5.0, hour.CoveredMinutes, 3); // only the t0->t1 gap; the boundary-crossing gap (t1->t2) is discarded
    }

    [Fact]
    public void HourlyRollup_ColdStart_AppAlreadyAt60Percent_DoesNotCreditTheJumpToTheHour()
    {
        // Codex review #3's first concrete input: "the window is already at 60% when the app
        // starts at 14:00, followed by polls through 14:55 at 60%. The 14:00 hour receives
        // consumed_pct=60" despite no evidence the consumption occurred then. A genuine
        // WITHIN-continuity rise (14:00->14:05) must still be credited normally.
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 19, 0, 0, TimeSpan.Zero); // far enough that there is no rollover
        DateTimeOffset t0 = new(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddMinutes(5);
        DateTimeOffset t2 = new(2026, 1, 1, 15, 5, 0, TimeSpan.Zero); // crosses into hour 15, closing hour 14

        model.Ingest(new UsageSnapshot(60.0, Raw(resetA, 0), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(65.0, Raw(resetA, 1), null, null, t1), t1, (long)(t1 - t0).TotalMilliseconds);
        model.Ingest(new UsageSnapshot(65.0, Raw(resetA, 2), null, null, t2), t2, (long)(t2 - t0).TotalMilliseconds);

        HourClosed hour14 = model.TakeClosedHours().Single(h => h.HourStartUtc == new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero));
        Assert.Equal(5.0, hour14.ConsumedPct, 3); // never 65 -- the cold-start jump to 60% is never credited, only the genuine +5 rise
    }

    [Fact]
    public void HourlyRollup_GapVariant_TheUnobservedJumpAcrossAGapIsNeverCreditedToTheResumingHour()
    {
        // Codex review #3's gap variant: "last poll 10% at 09:55, app absent until 14:00 when
        // usage is 80%, then runs all hour. The 70-point unknown-gap increase is credited to
        // 14:00" -- must now be zero-credited; a genuine SMALL rise once observation resumes
        // (14:00->14:05, within the allowance) is still counted normally.
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 19, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 9, 55, 0, TimeSpan.Zero);
        DateTimeOffset t1 = new(2026, 1, 1, 14, 0, 0, TimeSpan.Zero); // resumes after a ~4h05m gap, jumps to 80%
        DateTimeOffset t2 = t1.AddMinutes(5);                          // genuine small rise to 82%
        DateTimeOffset t3 = new(2026, 1, 1, 15, 5, 0, TimeSpan.Zero);  // crosses into hour 15

        model.Ingest(new UsageSnapshot(10.0, Raw(resetA, 0), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(80.0, Raw(resetA, 1), null, null, t1), t1, (long)(t1 - t0).TotalMilliseconds);
        model.Ingest(new UsageSnapshot(82.0, Raw(resetA, 2), null, null, t2), t2, (long)(t2 - t0).TotalMilliseconds);
        model.Ingest(new UsageSnapshot(82.0, Raw(resetA, 3), null, null, t3), t3, (long)(t3 - t0).TotalMilliseconds);

        HourClosed hour14 = model.TakeClosedHours().Single(h => h.HourStartUtc == new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero));
        Assert.Equal(2.0, hour14.ConsumedPct, 3); // never 72 (70 unobserved + 2 genuine) -- the gap's jump is never attributed to any hour
        Assert.Equal(2, hour14.Samples);
        Assert.Equal(5.0, hour14.CoveredMinutes, 3);
    }

    [Fact]
    public void CeilingCrossing_ReachedAfterAGapBeyondTheAllowance_IsIntervalCensored()
    {
        // Codex review #14's exact input: 90% at 01:00, no polls for 60 minutes, 100% at 02:00,
        // reset 05:00. The ceiling could have been reached anywhere inside that 60-minute gap --
        // the crossing is a LOWER BOUND (blocked_minutes) and must be marked censored, excluding
        // it from any hour-of-hit pattern.
        var model = new QuotaModel();
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 55, 0, TimeSpan.Zero); // continuity anchor just before 01:00
        DateTimeOffset tA = t0.AddMinutes(5);                          // 01:00, 90%
        DateTimeOffset tB = tA.AddMinutes(60);                         // 02:00, 100% -- reached after a 60-minute gap

        model.Ingest(new UsageSnapshot(85.0, Raw(resetA, 0), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(90.0, Raw(resetA, 1), null, null, tA), tA, (long)(tA - t0).TotalMilliseconds);
        model.Ingest(new UsageSnapshot(100.0, Raw(resetA, 2), null, null, tB), tB, (long)(tB - t0).TotalMilliseconds);

        DateTimeOffset resetB = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset rolloverAt = resetA.AddSeconds(10);
        model.Ingest(new UsageSnapshot(5.0, resetB.ToString("O"), null, null, rolloverAt), rolloverAt, (long)(rolloverAt - t0).TotalMilliseconds);

        CycleClosed cycle = Assert.Single(model.TakeClosedCycles());
        Assert.True(cycle.HitCeiling);
        Assert.Equal(tB, cycle.CeilingReachedAtUtc);
        Assert.True(cycle.CeilingReachedCensored);
        Assert.Equal(180.0, cycle.BlockedMinutes, 3); // resetA(05:00) - ceilingAt(02:00) -- a LOWER bound while censored
    }

    // ==================== Backfill grouping (pure, from window-shape rows) ====================

    [Fact]
    public void Backfill_RealFixture_RolloverProducesExactlyOneClosedSessionCycle_WithTheRightPeak()
    {
        IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(RolloverFixturePath);
        Assert.True(rows.Count > 30, $"expected the fixture's full slice, got {rows.Count}");

        StatisticsBackfill.WindowGroupingResult grouped = StatisticsBackfill.GroupWindow(rows, WindowKind.Session, QuotaWindows.SessionMinutes);

        StatisticsBackfill.ClosedCycleCandidate cycle = Assert.Single(grouped.Cycles);
        Assert.Equal(60.0, cycle.PeakPct); // the highest five_hour_pct observed before the 11:20:33 rollover
        Assert.False(cycle.HitCeiling);
        Assert.Equal(0.0, cycle.BlockedMinutes);
        // The final resets_at the OLD window settled on before closing.
        Assert.Equal(DateTimeOffset.Parse("2026-09-11T11:20:00.712790+00:00", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal), cycle.ResetUtc);
    }

    [Fact]
    public void Backfill_GroupWindow_RegressedReplicaRowsNeverJoinAnyGroup_ProvenAcrossEveryOutput()
    {
        // Codex review #28: the old test asserted only "one cycle exists". This constructs a
        // fully known, synthetic sequence and checks EVERY output a regressed row could have
        // corrupted: peak/final, coverage, and the derived hourly consumption -- a regressed key
        // (>120s earlier than the canonical group key) is replica noise on the key itself and
        // must never be counted anywhere.
        DateTimeOffset canonical = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var rows = new List<CsvReplay.RawRow>
        {
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), canonical, 20.0),
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), canonical, 95.0),
            // Regressed replica: resets_at 180s EARLIER than the canonical key, with a deliberately
            // extreme pct (999 -- invalid, but this proves the row is REJECTED before even the
            // percentage validity check could save it, since IsValidPct would also reject 999;
            // use a valid-but-extreme 99.9 instead so a bug that let the row through would still
            // show up in PeakPct without relying on validity rejection to hide it).
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 6, 0, TimeSpan.Zero), canonical.AddSeconds(-180), 99.9),
            // Rollover: closes the canonical group.
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), 5.0),
        };

        StatisticsBackfill.WindowGroupingResult grouped = StatisticsBackfill.GroupWindow(rows, WindowKind.Session, QuotaWindows.SessionMinutes);

        StatisticsBackfill.ClosedCycleCandidate cycle = Assert.Single(grouped.Cycles); // never a spurious second cycle either
        Assert.Equal(95.0, cycle.PeakPct); // never 99.9
        Assert.Equal(95.0, cycle.FinalPct);
        Assert.False(cycle.HitCeiling);
        Assert.Equal(5.0, cycle.CoveredMinutes, 3); // only the 0:00->0:05 gap; the regressed row contributes no coverage at all

        // The regressed row's timestamp (00:06) must not appear in any closed hour bucket's
        // consumption either -- the hour-00 bucket never closes in this data (the rollover row
        // at 00:15 is still inside hour 00), so it must be entirely absent, not merely empty.
        Assert.DoesNotContain(grouped.Hours, h => h.HourStartUtc == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void GroupWindow_ColdStartAndGapBeyondAllowance_NeverCreditConsumptionToAnyHour()
    {
        // Backfill's own version of #3/#17 -- GroupWindow deliberately never routes through
        // WindowTracker, so it needs its own proof.
        DateTimeOffset resetA = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        var rows = new List<CsvReplay.RawRow>
        {
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), resetA, 60.0),  // cold start, already at 60%
            SessionRow(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), resetA, 65.0),  // +5, within the allowance -> credited
            SessionRow(new DateTimeOffset(2026, 1, 1, 1, 10, 0, TimeSpan.Zero), resetA, 90.0), // 65-minute gap -> re-baseline, the 25pt jump NOT credited
            SessionRow(new DateTimeOffset(2026, 1, 1, 1, 15, 0, TimeSpan.Zero), resetA, 92.0), // +2, within the allowance -> credited
            SessionRow(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero).AddHours(5), 5.0), // rollover
        };

        StatisticsBackfill.WindowGroupingResult grouped = StatisticsBackfill.GroupWindow(rows, WindowKind.Session, QuotaWindows.SessionMinutes);
        StatisticsBackfill.ClosedCycleCandidate cycle = Assert.Single(grouped.Cycles);
        Assert.Equal(92.0, cycle.PeakPct);
        Assert.Equal(92.0, cycle.FinalPct); // #15: final = the envelope's own final value
        Assert.Equal(10.0, cycle.CoveredMinutes, 3); // 5 (0:00->0:05) + 0 (65-min gap) + 5 (1:10->1:15)

        StatisticsBackfill.ClosedHourCandidate hour00 = grouped.Hours.Single(h => h.HourStartUtc == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(5.0, hour00.ConsumedPct, 3); // never 65 -- the cold-start jump is never credited
        StatisticsBackfill.ClosedHourCandidate hour01 = grouped.Hours.Single(h => h.HourStartUtc == new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(2.0, hour01.ConsumedPct, 3); // never 27 -- the 25-point jump across the 65-minute gap is never credited
    }

    [Fact]
    public void GroupWindow_JitteredResetKeys_NeverDriftPastToleranceViaARatchetedComparison()
    {
        // Codex review #19's exact input: successive reset keys 05:00:00, 05:01:40, 05:03:20,
        // 05:05:00. Each step is only 100s from the PREVIOUS key, but the whole span from first
        // to last is 300s -- comparing against a ratcheted "latest key" merges all four into one
        // group even though the true span exceeds the +-120s tolerance. Comparing against the
        // group's STABLE canonical (first) key correctly splits it into two groups.
        DateTimeOffset baseUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<CsvReplay.RawRow>
        {
            SessionRow(baseUtc, new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero), 10.0),
            SessionRow(baseUtc.AddMinutes(1), new DateTimeOffset(2026, 1, 1, 5, 1, 40, TimeSpan.Zero), 20.0),
            SessionRow(baseUtc.AddMinutes(2), new DateTimeOffset(2026, 1, 1, 5, 3, 20, TimeSpan.Zero), 30.0),
            SessionRow(baseUtc.AddMinutes(3), new DateTimeOffset(2026, 1, 1, 5, 5, 0, TimeSpan.Zero), 40.0),
            SessionRow(baseUtc.AddMinutes(10), new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), 5.0), // far-future rollover, closes the second group
        };

        StatisticsBackfill.WindowGroupingResult grouped = StatisticsBackfill.GroupWindow(rows, WindowKind.Session, QuotaWindows.SessionMinutes);

        Assert.Equal(2, grouped.Cycles.Count); // never one -- the old ratcheted comparison would have merged all four rows
        Assert.Equal(20.0, grouped.Cycles[0].PeakPct);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 5, 1, 40, TimeSpan.Zero), grouped.Cycles[0].ResetUtc);
        Assert.Equal(40.0, grouped.Cycles[1].PeakPct);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 5, 5, 0, TimeSpan.Zero), grouped.Cycles[1].ResetUtc);
    }

    // ==================== Backfill idempotency, dedup, concurrency and directory scanning ====================

    [Fact]
    public void Backfill_RunningTwice_ProducesNoDuplicateRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"backfill-idem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(RolloverFixturePath, Path.Combine(dir, "window-shape-2026-09.csv"));
            const string accountKey = "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222";

            StatisticsBackfill.AccountResult first = StatisticsBackfill.RunForAccount(dir, accountKey);
            Assert.True(first.CyclesNewlyWritten >= 1, "expected the first run to write at least the one closed session cycle");

            IReadOnlyList<CycleArchiveCsv.Row> afterFirst = CycleArchiveCsv.ReadAll(Path.Combine(dir, CycleArchiveCsv.FileName));

            StatisticsBackfill.AccountResult second = StatisticsBackfill.RunForAccount(dir, accountKey);
            Assert.Equal(0, second.CyclesNewlyWritten);
            Assert.Equal(0, second.HourlyRowsNewlyWritten);
            Assert.Equal(first.CyclesDerived, second.CyclesDerived); // the same data on disk derives the same candidates every time

            IReadOnlyList<CycleArchiveCsv.Row> afterSecond = CycleArchiveCsv.ReadAll(Path.Combine(dir, CycleArchiveCsv.FileName));
            Assert.Equal(afterFirst.Count, afterSecond.Count); // no duplicate rows appended
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Backfill_JitteredCandidate_DedupesAgainstAnExistingLiveRow_PreferringIt()
    {
        // Codex review #20's exact scenario: a live archive uses reset 11:20:00.712790Z; the
        // same window's backfill candidate (derived from the SAME fixture rows,
        // Backfill_RealFixture_..._WithTheRightPeak) settles on that instant too, but the live
        // writer's own copy is jittered one second later -- both must be recognized as the SAME
        // logical window (+-120s), and the live row (with calibration data backfill can never
        // supply) must survive untouched.
        string dir = Path.Combine(Path.GetTempPath(), $"backfill-dedup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(RolloverFixturePath, Path.Combine(dir, "window-shape-2026-09.csv"));
            const string accountKey = "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222";

            DateTimeOffset liveResetUtc = DateTimeOffset.Parse("2026-09-11T11:20:01.712790+00:00",
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
            var liveRow = new CycleArchiveCsv.Row(accountKey, WindowKind.Session,
                liveResetUtc.AddMinutes(-QuotaWindows.SessionMinutes), liveResetUtc,
                PeakPct: 60.0, FinalPct: 60.0, HitCeiling: false, BlockedMinutes: 0, CoveredMinutes: 250,
                PlanTier: "max", WarnedDryEarly: true, PredictedPeakPct: 65.0, PredictedAtUtc: liveResetUtc.AddHours(-2));
            CycleArchiveCsv.AppendRow(Path.Combine(dir, CycleArchiveCsv.FileName), liveRow);

            StatisticsBackfill.AccountResult result = StatisticsBackfill.RunForAccount(dir, accountKey);

            Assert.Equal(0, result.CyclesNewlyWritten); // never duplicated -- the jittered candidate matches the live row within +-120s
            IReadOnlyList<CycleArchiveCsv.Row> rows = CycleArchiveCsv.ReadAll(Path.Combine(dir, CycleArchiveCsv.FileName));
            CycleArchiveCsv.Row only = Assert.Single(rows);
            Assert.Equal("max", only.PlanTier); // the live row's calibration data survives untouched
            Assert.True(only.WarnedDryEarly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CsvFileLock_ConcurrentLiveAppendAndBackfillStyleRewrite_NeverLosesARow()
    {
        // Codex review #4's exact race: a backfill rewrite (read-modify-write via
        // WriteAllAtomically) running concurrently with live appends must never lose one. This
        // exercises the shared per-account CsvFileLock directly: 40 live appends race 40
        // backfill-shaped "read everything, write it straight back" passes.
        string dir = Path.Combine(Path.GetTempPath(), $"csvlock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, CycleArchiveCsv.FileName);
            const int appendCount = 40;

            var appendTask = Task.Run(() =>
            {
                for (int i = 0; i < appendCount; i++)
                {
                    CycleArchiveCsv.AppendRow(path, new CycleArchiveCsv.Row(
                        "acct", WindowKind.Session,
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(6 * i),
                        new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero).AddHours(6 * i),
                        40.0, 40.0, false, 0, 295, "max", false, null, null));
                }
            });

            // Mirrors StatisticsBackfill's OWN merge shape exactly (MergeCycles): the read AND
            // the write share one lock acquisition, so a live append can never land in the gap
            // between them. Locking only the write (as an earlier draft of this test did) still
            // loses rows -- that gap is exactly Codex review #4's race.
            var rewriteTask = Task.Run(() =>
            {
                for (int i = 0; i < appendCount; i++)
                {
                    lock (CsvFileLock.For(dir))
                    {
                        IReadOnlyList<CycleArchiveCsv.Row> existing = CycleArchiveCsv.ReadAll(path);
                        CycleArchiveCsv.WriteAllAtomically(path, existing);
                    }
                }
            });

            await Task.WhenAll(appendTask, rewriteTask);

            IReadOnlyList<CycleArchiveCsv.Row> final = CycleArchiveCsv.ReadAll(path);
            Assert.Equal(appendCount, final.Count); // every append survived -- none dropped by a racing rewrite
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Rebuild_DiscardsStaleBackfillRows_PreservesLiveRows_RegeneratesUnderCurrentRules()
    {
        // The task's real-data migration: existing archives were produced under the OLD, flawed
        // rules. Rebuild must discard stale backfill-derived rows entirely (never just merge
        // around them, unlike Run/RunForAccount), while a live-written row (carrying calibration
        // data backfill can never reconstruct) survives untouched.
        string baseDir = Path.Combine(Path.GetTempPath(), $"rebuild-{Guid.NewGuid():N}");
        const string accountKey = "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222";
        string accountDir = Path.Combine(baseDir, accountKey);
        Directory.CreateDirectory(accountDir);
        try
        {
            File.Copy(RolloverFixturePath, Path.Combine(accountDir, "window-shape-2026-09.csv"));

            // A stale backfill-derived row (no live-only fields set -- the exact backfill
            // signature) for a window no fresh candidate will ever match.
            var staleRow = new CycleArchiveCsv.Row(accountKey, WindowKind.Session,
                new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2020, 1, 1, 5, 0, 0, TimeSpan.Zero),
                PeakPct: 42.0, FinalPct: 42.0, HitCeiling: false, BlockedMinutes: 0, CoveredMinutes: 100,
                PlanTier: null, WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);
            CycleArchiveCsv.WriteAllAtomically(Path.Combine(accountDir, CycleArchiveCsv.FileName), new[] { staleRow });

            // A live-written row (carries plan_tier) for yet another window.
            var liveRow = new CycleArchiveCsv.Row(accountKey, WindowKind.Weekly,
                new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2021, 1, 8, 0, 0, 0, TimeSpan.Zero),
                PeakPct: 55.0, FinalPct: 55.0, HitCeiling: false, BlockedMinutes: 0, CoveredMinutes: 9000,
                PlanTier: "max", WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);
            CycleArchiveCsv.AppendRow(Path.Combine(accountDir, CycleArchiveCsv.FileName), liveRow);

            IReadOnlyList<StatisticsBackfill.AccountResult> results = StatisticsBackfill.Rebuild(baseDir);
            Assert.Single(results);

            IReadOnlyList<CycleArchiveCsv.Row> final = CycleArchiveCsv.ReadAll(Path.Combine(accountDir, CycleArchiveCsv.FileName));
            Assert.DoesNotContain(final, r => r.StartedUtc.Year == 2020); // the stale backfill row is gone
            Assert.Contains(final, r => r.PlanTier == "max" && r.Window == WindowKind.Weekly); // the live row survived untouched
            Assert.Contains(final, r => r.Window == WindowKind.Session && r.PeakPct == 60.0); // freshly re-derived from the fixture under the current rules
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222", true)] // accountUuid_organizationUuid: current scheme
    [InlineData("11111111-1111-4111-8111-111111111111", false)] // bare accountUuid: legacy pre-split, mixes two accounts -- skip
    [InlineData("_pending", false)] // identity not yet known -- not a stable key
    [InlineData("cache_old", false)] // Codex review #26: neither half is a UUID
    [InlineData("foo_bar", false)]
    [InlineData("uuid_not-a-uuid", false)]
    [InlineData("11111111-1111-4111-8111-111111111111_not-a-uuid", false)] // first half real, second half not
    public void IsBackfillableAccountDirectory_RequiresExactlyTwoUuids(string directoryName, bool expected)
    {
        Assert.Equal(expected, StatisticsBackfill.IsBackfillableAccountDirectory(directoryName));
    }

    [Fact]
    public void Backfill_Run_SkipsLegacyDirectory_NeverCountsIt()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), $"backfill-legacy-{Guid.NewGuid():N}");
        string legacyDir = Path.Combine(baseDir, "11111111-1111-4111-8111-111111111111"); // bare accountUuid
        string realDir = Path.Combine(baseDir, "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222");
        Directory.CreateDirectory(legacyDir);
        Directory.CreateDirectory(realDir);
        try
        {
            File.Copy(RolloverFixturePath, Path.Combine(legacyDir, "window-shape-2026-09.csv"));
            File.Copy(RolloverFixturePath, Path.Combine(realDir, "window-shape-2026-09.csv"));

            IReadOnlyList<StatisticsBackfill.AccountResult> results = StatisticsBackfill.Run(baseDir);

            Assert.Single(results); // only the real (org-suffixed) directory was processed
            Assert.DoesNotContain(results, r => r.AccountKey == "11111111-1111-4111-8111-111111111111");
            Assert.False(File.Exists(Path.Combine(legacyDir, CycleArchiveCsv.FileName)), "the legacy directory must never even get a cycles.csv");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ==================== StatisticsEngine: N-gating and the coverage filter ====================

    static CycleArchiveCsv.Row SessionCycle(int index, bool hitCeiling, double coveredFraction = 1.0) => new(
        AccountKey: "acct", Window: WindowKind.Session,
        StartedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(6 * index),
        ResetUtc: new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero).AddHours(6 * index),
        PeakPct: hitCeiling ? 100.0 : 40.0, FinalPct: hitCeiling ? 100.0 : 40.0, HitCeiling: hitCeiling,
        BlockedMinutes: hitCeiling ? 30.0 : 0.0, CoveredMinutes: QuotaWindows.SessionMinutes * coveredFraction,
        PlanTier: "max", WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null,
        CeilingReachedAtUtc: hitCeiling ? new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero).AddHours(6 * index).AddMinutes(-30) : null,
        CeilingReachedCensored: false);

    static CycleArchiveCsv.Row WeeklyCycle(int index, double peakPct, string? tier = "max", bool hitCeiling = false) => new(
        AccountKey: "acct", Window: WindowKind.Weekly,
        StartedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(7 * index),
        ResetUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(7 * (index + 1)),
        PeakPct: peakPct, FinalPct: peakPct, HitCeiling: hitCeiling, BlockedMinutes: 0,
        CoveredMinutes: QuotaWindows.WeeklyMinutes, PlanTier: tier, WarnedDryEarly: false,
        PredictedPeakPct: null, PredictedAtUtc: null);

    static CycleArchiveCsv.Row EligibleCycle(int index, bool hitCeiling, bool warned) => new(
        AccountKey: "acct", Window: WindowKind.Session,
        StartedUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(6 * index),
        ResetUtc: new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero).AddHours(6 * index),
        PeakPct: hitCeiling ? 100.0 : 40.0, FinalPct: hitCeiling ? 100.0 : 40.0, HitCeiling: hitCeiling,
        BlockedMinutes: hitCeiling ? 30.0 : 0.0, CoveredMinutes: QuotaWindows.SessionMinutes,
        PlanTier: "max", WarnedDryEarly: warned,
        PredictedPeakPct: 95.0, // captured regardless of whether it became a warning (docs/statistics.md)
        PredictedAtUtc: new DateTimeOffset(2026, 1, 1, 2, 30, 0, TimeSpan.Zero).AddHours(6 * index),
        CeilingReachedAtUtc: hitCeiling ? new DateTimeOffset(2026, 1, 1, 4, 30, 0, TimeSpan.Zero).AddHours(6 * index) : null,
        CeilingReachedCensored: false);

    [Fact]
    public void StatisticsEngine_TimesAtCeiling_BelowThreshold_ReportsNotEnoughData()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations - 1)
            .Select(i => SessionCycle(i, hitCeiling: i == 0)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.False(result.TimesAtCeiling.Ready);
        Assert.Equal(StatisticsEngine.MinObservations - 1, result.TimesAtCeiling.N);
        Assert.Equal(StatisticsEngine.MinObservations, result.TimesAtCeiling.Threshold);
    }

    [Fact]
    public void StatisticsEngine_TimesAtCeiling_AtThreshold_ReportsAClaimWithN()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: i < 3)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.TimesAtCeiling.Ready);
        Assert.Equal(StatisticsEngine.MinObservations, result.TimesAtCeiling.N);
        Assert.Equal(3, result.TimesAtCeiling.Count);
    }

    [Fact]
    public void StatisticsEngine_CoverageFilter_ExcludesCyclesBelowTheThreshold()
    {
        var covered = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: false, coveredFraction: 1.0)).ToList();
        var underCovered = Enumerable.Range(100, 5)
            .Select(i => SessionCycle(i, hitCeiling: true, coveredFraction: 0.5))
            .ToList();

        StatisticsEngine.Result withOnlyCovered = StatisticsEngine.Compute(covered, Array.Empty<HourlyRollupCsv.Row>(), "acct");
        StatisticsEngine.Result withBothMixed = StatisticsEngine.Compute(covered.Concat(underCovered).ToList(), Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.Equal(withOnlyCovered.TimesAtCeiling.N, withBothMixed.TimesAtCeiling.N);
        Assert.Equal(0, withBothMixed.TimesAtCeiling.Count);
    }

    [Fact]
    public void StatisticsEngine_WeeklyBudgetRecommendation_CountsWeeksAtOrBelowTheFixedThreshold()
    {
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(50.0, result.WeeklyBudgetRecommendation.ThresholdPct);
        Assert.Equal(7, result.WeeklyBudgetRecommendation.WeeksAtOrBelow);
        Assert.Equal(StatisticsEngine.MinObservations, result.WeeklyBudgetRecommendation.WeeksConsidered);
    }

    // ==================== Padding helpers: the 12-of-24 hour / 5-of-7 weekday qualifying-bucket rule ====================
    // docs/statistics.md, 2026-09-21 follow-up to Codex review #11: a peak-hour/peak-weekday claim
    // now requires PeakHourMinQualifyingBuckets (12) of the 24 local hours, or
    // PeakWeekdayMinQualifyingBuckets (5) of the 7 local weekdays, to EACH individually clear
    // MinObservations -- not merely "any 2 buckets both qualify". These two helpers pad a test's
    // own fixture up to that quorum with extra, low-usage buckets, so a test can isolate what it
    // actually wants to prove (a specific winner, a specific below-threshold count) without
    // hand-placing a dozen buckets of filler every time.
    //
    // Every padding row lands on a dedicated, DST-safe (Dec-Feb, never near a Central-European
    // transition) date range, in its own calendar year keyed off the bucket's own index -- so it
    // can never collide with, or get summed into, any date a test builds for itself. PadHourBuckets
    // additionally anchors every row to SATURDAY (never any other weekday) and PadWeekdayBuckets to
    // local noon (never any other hour), so the two paddings can never contaminate the OTHER axis's
    // exact day-count/average either -- no test in this file makes an exact assertion about
    // Saturday or about hour 12.

    /// <summary>Pads up to StatisticsEngine.PeakHourMinQualifyingBuckets distinct qualifying HOUR buckets, given `alreadyQualifying` real buckets the caller already built and a set of UTC hours those buckets could shift into (skip so padding can never land on the same LOCAL hour under whichever TimeZoneInfo the test converts with).</summary>
    static List<HourlyRollupCsv.Row> PadHourBuckets(int alreadyQualifying, IReadOnlyCollection<int> excludeHours, double pct = 1.0)
    {
        var rows = new List<HourlyRollupCsv.Row>();
        int needed = StatisticsEngine.PeakHourMinQualifyingBuckets - alreadyQualifying;
        int added = 0;
        for (int hour = 0; hour < 24 && added < needed; hour++)
        {
            if (excludeHours.Contains(hour)) continue;
            DateTimeOffset firstSaturday = FirstDateOfWeekday(2050 + hour, 12, DayOfWeek.Saturday).AddHours(hour);
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                rows.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session, firstSaturday.AddDays(7 * w), pct, Samples: 1, CoveredMinutes: 55.0));
            added++;
        }
        return rows;
    }

    /// <summary>Same padding idea as PadHourBuckets, for the 7 local weekdays.</summary>
    static List<HourlyRollupCsv.Row> PadWeekdayBuckets(int alreadyQualifying, IReadOnlyCollection<DayOfWeek> excludeWeekdays, double pct = 1.0)
    {
        var rows = new List<HourlyRollupCsv.Row>();
        int needed = StatisticsEngine.PeakWeekdayMinQualifyingBuckets - alreadyQualifying;
        int added = 0;
        for (int dow = 0; dow < 7 && added < needed; dow++)
        {
            var weekday = (DayOfWeek)dow;
            if (excludeWeekdays.Contains(weekday)) continue;
            DateTimeOffset firstOccurrence = FirstDateOfWeekday(2100 + dow, 12, weekday).AddHours(12);
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                rows.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session, firstOccurrence.AddDays(7 * w), pct, Samples: 1, CoveredMinutes: 55.0));
            added++;
        }
        return rows;
    }

    /// <summary>The first `weekday` on or after the 1st of `month`/`year` -- Dec/Jan/Feb only, by construction of every caller above, so the 10-week span this feeds never crosses a DST boundary.</summary>
    static DateTimeOffset FirstDateOfWeekday(int year, int month, DayOfWeek weekday)
    {
        var first = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        int diff = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(diff);
    }

    [Fact]
    public void StatisticsEngine_PeakHourAndWeekday_BelowThreshold_NotEnoughData_AtThreshold_ReportsAWinner()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        for (int i = 0; i < StatisticsEngine.MinObservations - 1; i++)
        {
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 1 + i, 3, 0, 0, TimeSpan.Zero), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0));
        }
        StatisticsEngine.Result belowThreshold = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");
        Assert.False(belowThreshold.PeakHour.Ready);
        Assert.False(belowThreshold.PeakWeekday.Ready);

        hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
            new DateTimeOffset(2026, 1, 1 + (StatisticsEngine.MinObservations - 1), 3, 0, 0, TimeSpan.Zero), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0));
        for (int i = 0; i < StatisticsEngine.MinObservations; i++)
        {
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 3, 1 + i, 14, 0, 0, TimeSpan.Zero), ConsumedPct: 1.0, Samples: 1, CoveredMinutes: 55.0));
        }
        // The new 12-of-24 rule (docs/statistics.md, 2026-09-21): hour 3 and hour 14 alone are
        // only two qualifying buckets -- pad up to the required 12 with low-usage filler hours so
        // hour 3's real win is being tested, not merely whether 2 buckets happen to both qualify.
        hours.AddRange(PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 3, 14 }));

        StatisticsEngine.Result atThreshold = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");
        Assert.True(atThreshold.PeakHour.Ready);
        Assert.Equal(StatisticsEngine.MinObservations, atThreshold.PeakHour.N);
        Assert.Equal(3, atThreshold.PeakHour.Hour);
    }

    // ==================== N is counted in the right unit (task item 1) ====================

    [Fact]
    public void StatisticsEngine_PeakWeekdayAndHour_TwoWeeksOfDenseHourlyRows_StayInLearningState_WithNInDistinctDays()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        for (int day = 0; day < 9; day++)
            for (int hour = 9; hour < 15; hour++)
                hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                    new DateTimeOffset(2026, 1, 5 + day, hour, 0, 0, TimeSpan.Zero), ConsumedPct: 20.0, Samples: 2, CoveredMinutes: 55.0));
        Assert.Equal(54, hours.Count); // dense

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");

        // No individual weekday/hour bucket reaches even MinObservations (9 consecutive days give
        // at most 9 days of any one hour, and at most 2 days of any one weekday) -- let alone the
        // new 5-of-7/12-of-24 qualifying-bucket quorum -- so no candidate is named at all.
        Assert.False(result.PeakWeekday.Ready);
        Assert.Equal(0, result.PeakWeekday.QualifyingBuckets);
        Assert.Null(result.PeakWeekday.Weekday);
        Assert.Equal(0, result.PeakWeekday.N);

        Assert.False(result.PeakHour.Ready);
        Assert.Equal(0, result.PeakHour.QualifyingBuckets);
        Assert.Null(result.PeakHour.Hour);
        Assert.Equal(0, result.PeakHour.N);
    }

    [Fact]
    public void StatisticsEngine_PeakWeekdayAndHour_TwelveWeeksOfAWeeklyPattern_UnlocksWithNInDistinctDays()
    {
        DateTimeOffset firstMonday = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero); // Monday
        var hours = Enumerable.Range(0, 12)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                firstMonday.AddDays(7 * i), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0))
            .ToList();
        // The new 5-of-7-weekdays / 12-of-24-hours rule (docs/statistics.md, 2026-09-21 follow-up
        // to Codex review #11): pad up to both quorums with low-usage filler buckets so Monday/14's
        // win is a real comparison against a real quorum, not just one competing bucket.
        hours.AddRange(PadWeekdayBuckets(alreadyQualifying: 1, excludeWeekdays: new[] { DayOfWeek.Monday }, pct: 5.0));
        hours.AddRange(PadHourBuckets(alreadyQualifying: 1, excludeHours: new[] { 14 }, pct: 5.0));

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");

        Assert.True(result.PeakWeekday.Ready);
        Assert.Equal(12, result.PeakWeekday.N); // N in DISTINCT DAYS (12 Mondays), never the raw hourly-row count
        Assert.Equal(DayOfWeek.Monday, result.PeakWeekday.Weekday);

        Assert.True(result.PeakHour.Ready);
        Assert.Equal(12, result.PeakHour.N);
        Assert.Equal(14, result.PeakHour.Hour);
    }

    // ==================== Codex review #11: a single observed bucket is not a comparison ====================

    [Fact]
    public void StatisticsEngine_PeakHour_OnlyOneHourEverObserved_StaysInLearningState()
    {
        // Finding #11's exact input: the app runs only 14:00-15:00 on ten days and is closed the
        // rest of the time -- there is only ever ONE hour bucket in the data at all, nowhere near
        // the new 12-of-24 quorum. Nothing supports comparing it against the other 23 hours.
        var hours = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 1 + i, 14, 0, 0, TimeSpan.Zero), ConsumedPct: 40.0, Samples: 4, CoveredMinutes: 55.0))
            .ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");

        Assert.False(result.PeakHour.Ready); // never "mest aktiva timmen kl 14-15" from a single observed hour
        Assert.Equal(1, result.PeakHour.QualifyingBuckets); // hour 14 itself DOES clear MinObservations -- it's just the ONLY one
        Assert.Null(result.PeakHour.Hour); // no candidate is named below the qualifying-bucket quorum
        Assert.Equal(0, result.PeakHour.N);
    }

    [Fact]
    public void StatisticsEngine_PeakWeekday_OnlyOneWeekdayEverObserved_StaysInLearningState()
    {
        var hours = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero).AddDays(7 * i), ConsumedPct: 40.0, Samples: 4, CoveredMinutes: 55.0))
            .ToList(); // always Monday

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");

        Assert.False(result.PeakWeekday.Ready);
        Assert.Equal(1, result.PeakWeekday.QualifyingBuckets); // Monday alone qualifies -- five are required
        Assert.Null(result.PeakWeekday.Weekday);
        Assert.Equal(0, result.PeakWeekday.N);
    }

    // ==================== Codex review #10/#16: local-day totals, never a raw row mean ====================

    [Fact]
    public void StatisticsEngine_PeakWeekday_ComparesLocalDayTotals_NotARowMean()
    {
        // Finding #10's exact input: ten Mondays, each ONE covered hour consuming 50%; ten
        // Tuesdays, each EIGHT covered hours consuming 10% each (80% total that day). A raw row
        // mean makes Monday win (50 vs 10); comparing DAILY TOTALS correctly makes Tuesday win.
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset firstMonday = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        for (int w = 0; w < 10; w++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session, firstMonday.AddDays(7 * w), ConsumedPct: 50.0, Samples: 2, CoveredMinutes: 55.0));

        DateTimeOffset firstTuesday = new(2026, 1, 6, 9, 0, 0, TimeSpan.Zero);
        for (int w = 0; w < 10; w++)
            for (int h = 0; h < 8; h++)
                hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session, firstTuesday.AddDays(7 * w).AddHours(h), ConsumedPct: 10.0, Samples: 1, CoveredMinutes: 55.0));
        // The new 5-of-7-weekdays rule: pad 3 more low-usage qualifying weekdays (Monday and
        // Tuesday already qualify) so Tuesday's win over Monday is a real comparison against a
        // real quorum, not just two competing buckets.
        hours.AddRange(PadWeekdayBuckets(alreadyQualifying: 2, excludeWeekdays: new[] { DayOfWeek.Monday, DayOfWeek.Tuesday }));

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct");

        Assert.True(result.PeakWeekday.Ready);
        Assert.Equal(DayOfWeek.Tuesday, result.PeakWeekday.Weekday); // never Monday
        Assert.Equal(80.0, result.PeakWeekday.AverageConsumedPct!.Value, 3); // 8*10 per Tuesday, averaged across 10 Tuesdays
    }

    // ==================== Local-time bucketing (docs/statistics.md, "fix first") ====================

    static TimeZoneInfo Stockholm()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }

    [Fact]
    public void StatisticsEngine_PeakHourAndWeekday_ShiftBetweenUtcAndStockholm()
    {
        DateTimeOffset firstMonday = new(2026, 1, 5, 23, 0, 0, TimeSpan.Zero);
        var hours = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                firstMonday.AddDays(7 * i), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0))
            .ToList();
        // A second, fully-qualified, lower-usage competing bucket (Codex review #11) -- Thursday
        // 10:00 UTC, which shifts to a DIFFERENT (weekday, hour) in Stockholm too, so it never
        // collides with the winner in either timezone.
        DateTimeOffset firstThursday = new(2026, 1, 8, 10, 0, 0, TimeSpan.Zero);
        hours.AddRange(Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                firstThursday.AddDays(7 * i), ConsumedPct: 5.0, Samples: 1, CoveredMinutes: 55.0)));
        // The new 12-of-24 / 5-of-7 rule: pad up to both quorums with low-usage filler buckets --
        // Saturday/noon anchored (see PadHourBuckets/PadWeekdayBuckets), so they never collide
        // with -- or shift into -- the Monday/Thursday buckets under test in either timezone.
        hours.AddRange(PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 23, 10 }));
        hours.AddRange(PadWeekdayBuckets(alreadyQualifying: 2, excludeWeekdays: new[] { DayOfWeek.Monday, DayOfWeek.Thursday }));

        StatisticsEngine.Result utcResult = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", TimeZoneInfo.Utc);
        Assert.True(utcResult.PeakHour.Ready);
        Assert.Equal(23, utcResult.PeakHour.Hour);
        Assert.Equal(DayOfWeek.Monday, utcResult.PeakWeekday.Weekday);

        StatisticsEngine.Result localResult = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", Stockholm());
        Assert.True(localResult.PeakHour.Ready);
        Assert.Equal(0, localResult.PeakHour.Hour);
        Assert.Equal(DayOfWeek.Tuesday, localResult.PeakWeekday.Weekday);
    }

    [Fact]
    public void StatisticsEngine_LocalTimeBucketing_OctoberDstFallBack_BothRealHoursCount_NeitherDroppedNorMisrouted()
    {
        TimeZoneInfo tz = Stockholm();
        DateTimeOffset beforeChange = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero); // -> local 02:00 CEST
        DateTimeOffset afterChange = new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero);  // -> local 02:00 CET
        Assert.Equal(2, TimeZoneInfo.ConvertTime(beforeChange, tz).Hour);
        Assert.Equal(2, TimeZoneInfo.ConvertTime(afterChange, tz).Hour);
        Assert.Equal(DayOfWeek.Sunday, TimeZoneInfo.ConvertTime(beforeChange, tz).DayOfWeek);

        var hours = new List<HourlyRollupCsv.Row>
        {
            new("acct", WindowKind.Session, beforeChange, ConsumedPct: 80.0, Samples: 2, CoveredMinutes: 55.0),
            new("acct", WindowKind.Session, afterChange, ConsumedPct: 100.0, Samples: 2, CoveredMinutes: 55.0),
        };
        for (int i = 0; i < 9; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 5 + i, 1, 0, 0, TimeSpan.Zero), ConsumedPct: 90.0, Samples: 2, CoveredMinutes: 55.0)); // 01:00 UTC -> local 02:00 CET in January
        for (int i = 0; i < 10; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 2, 1 + i, 12, 0, 0, TimeSpan.Zero), ConsumedPct: 1.0, Samples: 1, CoveredMinutes: 55.0)); // 12:00 UTC -> local 13:00 CET in February
        // The new 12-of-24 rule: pad up to the required quorum, excluding the UTC hours (0, 1, 12)
        // that feed the two buckets under test -- 0 and 1 BOTH land on local hour 2 (the DST
        // fall-back merge this test exists to prove), 12 lands on local hour 13.
        hours.AddRange(PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 0, 1, 12 }));

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", tz);

        Assert.True(result.PeakHour.Ready);
        Assert.Equal(StatisticsEngine.MinObservations, result.PeakHour.N); // 9 January days + 1 October day -- the DST day counts ONCE, not twice
        Assert.Equal(2, result.PeakHour.Hour);
        // Codex review #16: the two DST rows on the SAME local day are SUMMED into one daily
        // observation (80+100=180) before averaging -- never each averaged in as a separate row
        // (which used to divide by 11 while displaying N=10, finding #32's mismatched weighting).
        // (9*90 + 180) / 10 = 99.
        Assert.Equal(99.0, result.PeakHour.AverageConsumedPct!.Value, 3);
    }

    [Fact]
    public void StatisticsEngine_LocalTimeBucketing_MarchSpringForward_EngineNeverProducesTheSkippedLocalHour()
    {
        // Codex review #29: the old test only checked TimeZoneInfo.ConvertTime directly, never
        // running a single row through the engine -- it would have passed even if engine
        // bucketing were removed entirely. This asserts actual engine buckets and N.
        TimeZoneInfo tz = Stockholm();
        DateTimeOffset beforeChange = new(2026, 3, 29, 0, 0, 0, TimeSpan.Zero); // -> local hour 1 (CET)
        DateTimeOffset afterChange = new(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);  // -> local hour 3 (CEST) -- local hour 2 never existed that day

        // Two fully-qualified competing buckets (Codex review #11): local hour 1 (9 January days
        // + the pre-change day = 10) and local hour 3 (the post-change day + 9 April days = 10),
        // both clearing MinObservations, so "hour 1 wins" is a real comparison, not a winner
        // against an under-sampled leftover bucket.
        var hours = new List<HourlyRollupCsv.Row>
        {
            new("acct", WindowKind.Session, beforeChange, ConsumedPct: 50.0, Samples: 1, CoveredMinutes: 55.0),
            new("acct", WindowKind.Session, afterChange, ConsumedPct: 50.0, Samples: 1, CoveredMinutes: 55.0),
        };
        for (int i = 0; i < 9; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 5 + i, 0, 0, 0, TimeSpan.Zero), ConsumedPct: 90.0, Samples: 1, CoveredMinutes: 55.0)); // -> local hour 1 in January
        for (int i = 0; i < 9; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 4, 1 + i, 1, 0, 0, TimeSpan.Zero), ConsumedPct: 1.0, Samples: 1, CoveredMinutes: 55.0)); // 01:00 UTC -> local hour 3 (CEST, +2h) in April
        // The new 12-of-24 rule: pad up to the required quorum. Padding always converts at the
        // plain CET (+1) offset (Dec-Feb, see PadHourBuckets), so excluding UTC 0 (-> local 1,
        // the winner) and UTC 2 (-> local 3, the second bucket) is enough -- padding never
        // experiences the CEST (+2) offset the real April rows do.
        hours.AddRange(PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 0, 2 }));

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", tz);

        Assert.True(result.PeakHour.Ready);
        Assert.Equal(1, result.PeakHour.Hour); // 9 January rows + the pre-change row = 10 distinct days at local hour 1
        Assert.Equal(StatisticsEngine.MinObservations, result.PeakHour.N);
        // The engine must never have produced a bucket for the skipped local hour 2 at all --
        // not a dropped observation, since it never existed.
        Assert.NotEqual(2, result.PeakHour.Hour);
    }

    [Fact]
    public void StatisticsEngine_Compute_DefaultsToUtc_WhenNoTimezoneIsGiven()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        for (int i = 0; i < StatisticsEngine.MinObservations; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 1 + i, 3, 0, 0, TimeSpan.Zero), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0));
        for (int i = 0; i < StatisticsEngine.MinObservations; i++)
            hours.Add(new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 3, 1 + i, 15, 0, 0, TimeSpan.Zero), ConsumedPct: 5.0, Samples: 1, CoveredMinutes: 55.0));

        StatisticsEngine.Result withNullTz = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", tz: null);
        StatisticsEngine.Result withExplicitUtc = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours, "acct", TimeZoneInfo.Utc);
        Assert.Equal(withExplicitUtc.PeakHour, withNullTz.PeakHour);
    }

    [Fact]
    public void StatisticsEngine_HourlyCoverageFilter_ExcludesHoursBelowTheThreshold()
    {
        var hours = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 1 + i, 3, 0, 0, TimeSpan.Zero), ConsumedPct: 20.0, Samples: 3, CoveredMinutes: 55.0))
            .ToList();
        // A second, properly-covered, lower-usage bucket so there IS a real comparison (Codex review #11).
        hours.AddRange(Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 2, 1 + i, 8, 0, 0, TimeSpan.Zero), ConsumedPct: 2.0, Samples: 1, CoveredMinutes: 55.0)));
        // Under-covered rows at a THIRD hour, with an extreme value that would win if counted.
        var underCovered = Enumerable.Range(0, 5)
            .Select(i => new HourlyRollupCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 3, 1 + i, 20, 0, 0, TimeSpan.Zero), ConsumedPct: 99.0, Samples: 1, CoveredMinutes: 10.0)) // 10/60 < 80%
            .ToList();
        // The new 12-of-24 rule: pad up to the required quorum (the under-covered hour-20 rows
        // never qualify at all, so they don't count against alreadyQualifying).
        var padding = PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 3, 8 });

        StatisticsEngine.Result result = StatisticsEngine.Compute(Array.Empty<CycleArchiveCsv.Row>(), hours.Concat(underCovered).Concat(padding).ToList(), "acct");

        Assert.True(result.PeakHour.Ready);
        Assert.Equal(3, result.PeakHour.Hour); // hour 20's under-covered rows never entered the computation
        Assert.Equal(StatisticsEngine.MinObservations, result.PeakHour.N);
    }

    // ==================== Codex review #7/#8: forecast precision and recall, eligibility ====================

    [Fact]
    public void ForecastPrecisionAndRecall_ReportTwoSeparateNumbers_OverEligibleCyclesOnly()
    {
        // Finding #7's exact input: ten WARNED cycles that all hit the ceiling, plus ninety
        // UNWARNED cycles that also hit -- precision alone ("100% rätt, baserat på 10
        // varningar") hid that the warning caught only 10% of all ceiling events.
        var cycles = new List<CycleArchiveCsv.Row>();
        for (int i = 0; i < 10; i++) cycles.Add(EligibleCycle(i, hitCeiling: true, warned: true));
        for (int i = 10; i < 100; i++) cycles.Add(EligibleCycle(i, hitCeiling: true, warned: false));

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.ForecastPrecision.Ready);
        Assert.Equal(1.0, result.ForecastPrecision.Precision!.Value, 3);
        Assert.Equal(10, result.ForecastPrecision.N);

        Assert.True(result.ForecastRecall.Ready);
        Assert.Equal(0.10, result.ForecastRecall.Recall!.Value, 3); // only 10 of the 100 hits were warned in advance
        Assert.Equal(100, result.ForecastRecall.N);
    }

    [Fact]
    public void ForecastEligibility_RequiresBothPredictionFieldsInsideTheCycleAndBeforeTheOutcome()
    {
        // Finding #8's exact input: rows with WarnedDryEarly=true but empty predicted fields
        // must never count as eligible for either precision or recall.
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: true) with { WarnedDryEarly = true, PredictedPeakPct = null, PredictedAtUtc = null })
            .ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.False(result.ForecastPrecision.Ready);
        Assert.Equal(0, result.ForecastPrecision.N);
        Assert.False(result.ForecastRecall.Ready);
        Assert.Equal(0, result.ForecastRecall.N);
    }

    [Fact]
    public void ForecastEligibility_APredictionTimestampedAfterTheCeilingWasAlreadyReached_IsNotEligible()
    {
        CycleArchiveCsv.Row baseCycle = SessionCycle(0, hitCeiling: true);
        CycleArchiveCsv.Row late = baseCycle with
        {
            WarnedDryEarly = true,
            PredictedPeakPct = 99.0,
            CeilingReachedAtUtc = baseCycle.ResetUtc.AddMinutes(-20),
            PredictedAtUtc = baseCycle.ResetUtc.AddMinutes(-5), // AFTER the ceiling was already reached
        };
        var cycles = Enumerable.Range(1, StatisticsEngine.MinObservations - 1)
            .Select(i => SessionCycle(i, hitCeiling: false)).Append(late).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.Equal(0, result.ForecastRecall.N); // the only hit cycle's "prediction" doesn't count -- it came after the outcome
    }

    // ==================== Codex review #6: the joint weekday x hour "most often" clause is gone ====================

    [Fact]
    public void CeilingRecommendation_NeverExposesAWeekdayOrHourClause()
    {
        // Finding #6's exact input: ten qualifying session cycles over three days, only ONE hit
        // -- the old 168-cell argmax let a single observation "win" outright.
        var cycles = new List<CycleArchiveCsv.Row>
        {
            SessionCycle(0, hitCeiling: true) with { ResetUtc = new DateTimeOffset(2026, 1, 5, 3, 30, 0, TimeSpan.Zero), BlockedMinutes = 30.0 },
        };
        for (int i = 1; i < StatisticsEngine.MinObservations; i++)
            cycles.Add(SessionCycle(i, hitCeiling: false));

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.CeilingRecommendation.Ready);
        Assert.Equal(1, result.CeilingRecommendation.TimesHit);
        // Reflection-confirmed: CeilingRecommendation carries no weekday/hour field at all.
        string[] propertyNames = typeof(StatisticsEngine.CeilingRecommendation).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("MostOftenWeekday", propertyNames);
        Assert.DoesNotContain("MostOftenHour", propertyNames);
    }

    // ==================== Codex review #1/#2/#5: plan advice considers both windows, plan tier, and time ====================

    [Fact]
    public void WeeklyBudgetRecommendation_Downsize_ForbiddenWhenAnyQualifyingSessionCycleHitTheCeiling()
    {
        // Finding #1's exact input: ten qualifying weekly cycles, seven at 30%, three at 80%
        // (downsize-shaped, no WEEKLY ceiling hits), but the session ceiling is hit daily
        // throughout the same period.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: true)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly.Concat(session).ToList(), Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.True(result.WeeklyBudgetRecommendation.SessionEvidenceAvailable);
        Assert.True(result.WeeklyBudgetRecommendation.AnySessionCeilingHitInPeriod);
        Assert.Equal(StatisticsEngine.WeeklyBudgetDirection.None, result.WeeklyBudgetRecommendation.Direction); // never Downsize
    }

    [Fact]
    public void WeeklyBudgetRecommendation_Downsize_ForbiddenWhenSessionEvidenceIsMissingEntirely()
    {
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly, Array.Empty<HourlyRollupCsv.Row>(), "acct"); // no session cycles at all

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.False(result.WeeklyBudgetRecommendation.SessionEvidenceAvailable);
        Assert.Equal(StatisticsEngine.WeeklyBudgetDirection.None, result.WeeklyBudgetRecommendation.Direction); // never Downsize with no session evidence at all
    }

    [Fact]
    public void WeeklyBudgetRecommendation_Downsize_AllowedWhenSessionEvidenceIsCleanAndAvailable()
    {
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: false)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly.Concat(session).ToList(), Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.Equal(StatisticsEngine.WeeklyBudgetDirection.Downsize, result.WeeklyBudgetRecommendation.Direction);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_UnknownCurrentTier_NoAdviceAtAll()
    {
        // Finding #2: backfilled rows (plan_tier always empty) never feed plan advice, and an
        // account with no live-tracked cycle anywhere has no known "current tier".
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, 30.0, tier: null)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.False(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(0, result.WeeklyBudgetRecommendation.N);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_PlanChange_RestartsN_FewCyclesOnTheNewPlanMeansNoRecommendationYet()
    {
        // Finding #2's exact input: ten backfilled weekly rows (PlanTier=null), then the user
        // upgrades and has fewer than MinObservations completed cycles on the new plan.
        var cycles = new List<CycleArchiveCsv.Row>();
        for (int i = 0; i < 10; i++) cycles.Add(WeeklyCycle(i, 30.0, tier: null));
        for (int i = 10; i < 13; i++) cycles.Add(WeeklyCycle(i, 30.0, tier: "max"));

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.False(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(3, result.WeeklyBudgetRecommendation.N); // never 13 -- the 10 backfilled/unknown-tier weeks never count
    }

    [Fact]
    public void WeeklyBudgetRecommendation_PlanChange_OldPlanCyclesNeverCountTowardTheNewPlansN()
    {
        var cycles = new List<CycleArchiveCsv.Row>();
        for (int i = 0; i < 20; i++) cycles.Add(WeeklyCycle(i, 30.0, tier: "pro")); // 20 weeks on the OLD plan
        for (int i = 20; i < 30; i++) cycles.Add(WeeklyCycle(i, 30.0, tier: "max")); // 10 weeks on the CURRENT plan

        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(10, result.WeeklyBudgetRecommendation.N); // never 30 -- the 20 "pro" weeks never count toward "max"'s pool
    }

    [Fact]
    public void WeeklyBudgetRecommendation_TenWeeksAlone_IsOnlyAHedgedEarlyHint_NeverThePlainYearStatement()
    {
        // Finding #5's exact input: exactly ten fully-covered weekly cycles (well short of a
        // year), seven at 30%, three at 80%, with clean session evidence.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: false)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly.Concat(session).ToList(), Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(StatisticsEngine.WeeklyBudgetDirection.Downsize, result.WeeklyBudgetRecommendation.Direction);
        Assert.True(result.WeeklyBudgetRecommendation.IsEarlyHint); // ~70 days, nowhere near a year
        Assert.True(result.WeeklyBudgetRecommendation.SpanDays < StatisticsEngine.PlanAdviceFullConfidenceDays);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_AFullYearOfSamePlanHistory_IsThePlainStatement_NeverHedged()
    {
        var weekly = Enumerable.Range(0, 60)
            .Select(i => WeeklyCycle(i, i < 45 ? 30.0 : 80.0)).ToList(); // 60 weeks (420 days), 75% at/below 50%
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: false)).ToList();

        StatisticsEngine.Result result = StatisticsEngine.Compute(weekly.Concat(session).ToList(), Array.Empty<HourlyRollupCsv.Row>(), "acct");

        Assert.True(result.WeeklyBudgetRecommendation.Ready);
        Assert.Equal(StatisticsEngine.WeeklyBudgetDirection.Downsize, result.WeeklyBudgetRecommendation.Direction);
        Assert.False(result.WeeklyBudgetRecommendation.IsEarlyHint);
        Assert.True(result.WeeklyBudgetRecommendation.SpanDays >= StatisticsEngine.PlanAdviceFullConfidenceDays);
    }

    [Fact]
    public void StatisticsEngine_ForecastEligible_UsedByPrecisionAndRecall_BothEmptyWhenNoPredictionsExist()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionCycle(i, hitCeiling: i < 4)).ToList();
        StatisticsEngine.Result result = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct");
        Assert.False(result.ForecastPrecision.Ready);
        Assert.False(result.ForecastRecall.Ready);
        Assert.Equal(0, result.ForecastPrecision.N);
        Assert.Equal(0, result.ForecastRecall.N);
    }

    // ==================== cycles.csv / hourly-*.csv format: the byte contract ====================

    [Fact]
    public void CycleArchiveCsv_RoundTrips_EveryFieldIncludingNulls()
    {
        var withNulls = new CycleArchiveCsv.Row(
            "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222", WindowKind.Weekly,
            new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 11, 5, 0, 0, TimeSpan.Zero),
            PeakPct: 87.5, FinalPct: 80.0, HitCeiling: false, BlockedMinutes: 0.0, CoveredMinutes: 9800.25,
            PlanTier: null, WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);
        var withValues = withNulls with
        {
            PlanTier = "max", WarnedDryEarly = true, PredictedPeakPct = 91.2, PredictedAtUtc = withNulls.ResetUtc.AddDays(-3),
            HitCeiling = true, CeilingReachedAtUtc = withNulls.ResetUtc.AddHours(-3), CeilingReachedCensored = true,
        };

        foreach (CycleArchiveCsv.Row original in new[] { withNulls, withValues })
        {
            string line = CycleArchiveCsv.FormatLine(original);
            Assert.True(CycleArchiveCsv.TryParse(line, out CycleArchiveCsv.Row parsed));
            Assert.Equal(original, parsed);
        }
    }

    [Fact]
    public void CycleArchiveCsv_AppendRow_WritesHeaderOnce_ThenAppendsEachRow()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"cyclecsv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, CycleArchiveCsv.FileName);
            var row = new CycleArchiveCsv.Row("acct", WindowKind.Session,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero),
                50.0, 50.0, false, 0.0, 295.0, "max", false, null, null);

            CycleArchiveCsv.AppendRow(path, row);
            CycleArchiveCsv.AppendRow(path, row with { ResetUtc = row.ResetUtc.AddHours(5) });

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length); // header + 2 rows
            Assert.Equal(string.Join(",", CycleArchiveCsv.Header), lines[0]);

            IReadOnlyList<CycleArchiveCsv.Row> rows = CycleArchiveCsv.ReadAll(path);
            Assert.Equal(2, rows.Count);

            // Codex review #25: '\n' line endings, no BOM.
            byte[] bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain((byte)0x0D, bytes);
            Assert.True(bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HourlyRollupCsv_RoundTrips_AndSplitsByCalendarYear()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hourlycsv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var row2026 = new HourlyRollupCsv.Row("acct", WindowKind.Weekly,
                new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero), 12.5, 4, 58.0);
            var row2027 = row2026 with { HourStartUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) };

            HourlyRollupCsv.AppendRow(dir, row2026);
            HourlyRollupCsv.AppendRow(dir, row2027);

            Assert.True(File.Exists(Path.Combine(dir, "hourly-2026.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "hourly-2027.csv")));

            IReadOnlyList<HourlyRollupCsv.Row> rows2026 = HourlyRollupCsv.ReadAll(Path.Combine(dir, "hourly-2026.csv"));
            HourlyRollupCsv.Row only2026 = Assert.Single(rows2026);
            Assert.Equal(row2026, only2026);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CycleArchiveCsv_TryParse_RejectsUnknownSchema()
    {
        // Finding #22's exact input: a row beginning with an unknown schema but otherwise the
        // current column layout must never be silently parsed as an earlier/later one.
        string line = "3,acct,session,2026-01-01T00:00:00.0000000+00:00,2026-01-01T05:00:00.0000000+00:00,50,50,0,0,295,,0,,,,";
        Assert.False(CycleArchiveCsv.TryParse(line, out _));
    }

    [Fact]
    public void CycleArchiveCsv_TryParse_Schema1RealFileShape_MigratesOnRead_MarkedCensored()
    {
        // A REAL row shape as written by the previous build (schema 1, 14 columns, no
        // ceiling_reached_at/ceiling_censored) -- these files exist on real disks right now.
        string schema1Line =
            "1,11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222,session," +
            "2026-09-11T06:20:00.7127900+00:00,2026-09-11T11:20:00.7127900+00:00,100,95,1,30,295,max,0,,";

        Assert.True(CycleArchiveCsv.TryParse(schema1Line, out CycleArchiveCsv.Row row));
        Assert.True(row.HitCeiling);
        Assert.Equal(30.0, row.BlockedMinutes);
        Assert.Equal(row.ResetUtc.AddMinutes(-30), row.CeilingReachedAtUtc); // migrated the old way: reset_utc - blocked_minutes
        Assert.True(row.CeilingReachedCensored); // unconditionally -- schema 1 never verified exactness
    }

    [Fact]
    public void CycleArchiveCsv_TryParse_Schema1_NoHit_MigratesWithNullCeilingFields()
    {
        string schema1Line =
            "1,acct,weekly,2026-09-04T05:00:00.0000000+00:00,2026-09-11T05:00:00.0000000+00:00,40,40,0,0,9800,max,0,,";
        Assert.True(CycleArchiveCsv.TryParse(schema1Line, out CycleArchiveCsv.Row row));
        Assert.False(row.HitCeiling);
        Assert.Null(row.CeilingReachedAtUtc);
        Assert.False(row.CeilingReachedCensored);
    }

    [Fact]
    public void CycleArchiveCsv_TryParse_MalformedBoolean_RejectsTheRow_NeverSilentlyFalse()
    {
        // Finding #23's exact input: hit_ceiling="true", warned_dry_early="x".
        string line = "2,acct,session,2026-01-01T00:00:00.0000000+00:00,2026-01-01T05:00:00.0000000+00:00,50,50,true,0,295,,x,,,,";
        Assert.False(CycleArchiveCsv.TryParse(line, out _));
    }

    [Fact]
    public void HourlyRollupCsv_TryParse_RejectsUnknownSchema()
    {
        Assert.False(HourlyRollupCsv.TryParse("2,acct,session,2026-01-01T00:00:00.0000000+00:00,10,1,55", out _));
    }

    [Fact]
    public void CsvUtil_Field_RejectsEmbeddedCrOrLf_NeverProducesAMultilineRecord()
    {
        // Finding #24's exact input: plan_tier containing an embedded newline.
        Assert.Throws<ArgumentException>(() => CsvUtil.Field("team\nenterprise"));
        Assert.Throws<ArgumentException>(() => CsvUtil.Field("team\renterprise"));
    }

    [Fact]
    public void CsvUtil_TimestampContract_WritesUtc_AndRejectsNonUtcOnRead()
    {
        // Finding #21's exact input: a +02:00 offset instant.
        var offset = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.FromHours(2));
        string formatted = CsvUtil.FormatUtc(offset);
        Assert.Equal("2026-09-21T10:00:00.0000000+00:00", formatted);

        Assert.False(CsvUtil.TryParseUtc("2026-09-21T12:00:00.0000000+02:00", out _));
        Assert.True(CsvUtil.TryParseUtc("2026-09-21T10:00:00.0000000+00:00", out DateTimeOffset parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Theory]
    [InlineData(100.0, "100")]
    [InlineData(0.0, "0")]
    [InlineData(33.333333, "33.33")]
    [InlineData(20.5, "20.5")]
    [InlineData(91.2, "91.2")]
    public void CsvUtil_FormatPercent_IntegersBare_OtherwiseAtMostTwoDecimals(double value, string expected)
    {
        Assert.Equal(expected, CsvUtil.FormatPercent(value));
    }

    // ==================== Codex review #25/#30: golden byte fixtures ====================

    [Fact]
    public void CycleArchiveCsv_GoldenFixture_FormatLineIsByteIdenticalToTheSharedContractFile()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "statistics", "cycles-golden.csv");
        byte[] expectedBytes = File.ReadAllBytes(fixturePath);

        var row1 = new CycleArchiveCsv.Row(
            "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222", WindowKind.Weekly,
            DateTimeOffset.Parse("2026-09-04T05:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse("2026-09-11T05:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PeakPct: 100.0, FinalPct: 100.0, HitCeiling: true, BlockedMinutes: 180.0, CoveredMinutes: 9800.25,
            PlanTier: "max", WarnedDryEarly: true, PredictedPeakPct: 91.2,
            PredictedAtUtc: DateTimeOffset.Parse("2026-09-08T05:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CeilingReachedAtUtc: DateTimeOffset.Parse("2026-09-11T02:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            CeilingReachedCensored: false);

        var row2 = new CycleArchiveCsv.Row(
            "33333333-3333-4333-8333-333333333333_44444444-4444-4444-8444-444444444444", WindowKind.Session,
            DateTimeOffset.Parse("2026-09-11T05:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse("2026-09-11T10:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PeakPct: 33.333333, FinalPct: 20.5, HitCeiling: false, BlockedMinutes: 0.0, CoveredMinutes: 271.5,
            PlanTier: null, WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);

        string built = string.Join(",", CycleArchiveCsv.Header) + "\n" + CycleArchiveCsv.FormatLine(row1) + "\n" + CycleArchiveCsv.FormatLine(row2) + "\n";
        byte[] builtBytes = new UTF8Encoding(false).GetBytes(built);
        Assert.Equal(expectedBytes, builtBytes);
    }

    [Fact]
    public void HourlyRollupCsv_GoldenFixture_FormatLineIsByteIdenticalToTheSharedContractFile()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "statistics", "hourly-golden.csv");
        byte[] expectedBytes = File.ReadAllBytes(fixturePath);

        var row1 = new HourlyRollupCsv.Row(
            "11111111-1111-4111-8111-111111111111_22222222-2222-4222-8222-222222222222", WindowKind.Session,
            DateTimeOffset.Parse("2026-09-11T14:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ConsumedPct: 12.5, Samples: 3, CoveredMinutes: 55.0);
        var row2 = new HourlyRollupCsv.Row(
            "33333333-3333-4333-8333-333333333333_44444444-4444-4444-8444-444444444444", WindowKind.Weekly,
            DateTimeOffset.Parse("2026-12-31T23:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ConsumedPct: 0.0, Samples: 0, CoveredMinutes: 0.0);

        string built = string.Join(",", HourlyRollupCsv.Header) + "\n" + HourlyRollupCsv.FormatLine(row1) + "\n" + HourlyRollupCsv.FormatLine(row2) + "\n";
        byte[] builtBytes = new UTF8Encoding(false).GetBytes(built);
        Assert.Equal(expectedBytes, builtBytes);
    }

    // ==================== Account selector label matches the tray label (task item 4) ====================

    static AccountIdentity Identity(string? orgName = null, string? email = null) => new(
        AccountUuid: "uuid-" + Guid.NewGuid().ToString("N")[..8],
        OrganizationUuid: null,
        OrganizationName: orgName,
        OrganizationType: null,
        SeatTier: null,
        BillingType: null,
        EmailAddress: email,
        DisplayName: null);

    [Fact]
    public void ResolveAccountLabel_LiveSubscriptionTypeKnown_ReturnsTheLiveLabelUnchanged()
    {
        var input = new AccountLabelInput(null, Identity(orgName: null, email: "alice@example.com"), "max");
        string liveLabel = "Acme (Max)";
        Assert.Equal(liveLabel, StatisticsDataLoader.ResolveAccountLabel(liveLabel, input, "C:\\does-not-exist", 0));
    }

    [Fact]
    public void ResolveAccountLabel_NoLiveSubscriptionType_FallsBackToThePlanTierLastRecordedOnDisk_MatchingTheTrayLabel()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"label-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            AccountIdentity identity = Identity(orgName: null, email: "alice@example.com");
            var liveInput = new AccountLabelInput(null, identity, SubscriptionType: null);
            string degradedLiveLabel = AccountLabel.Resolve(liveInput, 0);
            Assert.Equal("alice", degradedLiveLabel);

            CycleArchiveCsv.AppendRow(Path.Combine(dir, CycleArchiveCsv.FileName), new CycleArchiveCsv.Row(
                "acct", WindowKind.Session, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow,
                50.0, 50.0, false, 0.0, 295.0, PlanTier: "max", WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null));

            string selectorLabel = StatisticsDataLoader.ResolveAccountLabel(degradedLiveLabel, liveInput, dir, 0);
            string trayLabelOnceLive = AccountLabel.Resolve(liveInput with { SubscriptionType = "max" }, 0);

            Assert.Equal(trayLabelOnceLive, selectorLabel);
            Assert.Equal("Max", selectorLabel);
            Assert.NotEqual(degradedLiveLabel, selectorLabel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveAccountLabel_NoLiveSubscriptionType_NoDiskDataEither_FallsBackToTheLiveLabelAsGiven()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"label-fallback-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var input = new AccountLabelInput(null, Identity(orgName: null, email: "alice@example.com"), SubscriptionType: null);
            string liveLabel = AccountLabel.Resolve(input, 0);
            Assert.Equal(liveLabel, StatisticsDataLoader.ResolveAccountLabel(liveLabel, input, dir, 0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
