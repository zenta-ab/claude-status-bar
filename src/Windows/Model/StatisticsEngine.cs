using System.Linq;
using ClaudeStatusBar.Data;

namespace ClaudeStatusBar.Model;

/// <summary>
/// docs/statistics.md, decision 3: a pure, UI-free component that reads cycles.csv/hourly-*.csv
/// (already parsed, via CycleArchiveCsv/HourlyRollupCsv) and returns the named tiles and the two
/// pre-specified recommendations -- nothing here draws anything or knows about a window, a
/// timezone or a locale; that is the next builder's job.
///
/// Two rules apply to every single result this type produces:
///
///  - **Coverage gate** (task: "choose one, e.g. 80%, and write it into the doc"): a cycle or
///    hour below CoverageThreshold never contributes to any computation here at all -- filtered
///    out before any aggregate is taken, not down-weighted.
///  - **N gate** (docs/statistics.md decision 3: "a bucket with fewer than ~10 observations is
///    not a claim, and every statement shows the N behind it"): MinObservations is that ~10,
///    applied uniformly to every tile and recommendation's own qualifying population. Below it,
///    the result carries Ready=false plus the actual N and the threshold, never a value to
///    display as if it were trustworthy. The unit N is counted in is the tile's own natural
///    observation: a session cycle for the cycle-based tiles, DISTINCT COVERED LOCAL DAYS for
///    PeakWeekday/PeakHour (never a finer-grained hourly-row count).
///
/// Codex review governing rule for this round (docs/reviews/2026-09-21-codex-statistics.md):
/// "a statistic the app cannot defend is worse than no statistic" -- every fix below either makes
/// a claim honest or removes it in favour of the learning state.
/// </summary>
public static class StatisticsEngine
{
    /// <summary>docs/statistics.md: a cycle/hour observed less than this fraction of its own length is not counted toward any pattern.</summary>
    public const double CoverageThreshold = 0.8;

    /// <summary>docs/statistics.md decision 3's "~10 observations" rule, applied uniformly.</summary>
    public const int MinObservations = 10;

    /// <summary>
    /// docs/statistics.md, Codex review #11 follow-up (2026-09-21): a peak-HOUR claim requires at
    /// least this many of the 24 local clock hours to EACH individually clear MinObservations (in
    /// their own distinct-covered-local-days unit) -- replacing the earlier "any 2 qualifying
    /// buckets" rule, which a narrowly-focused usage pattern (e.g. the app only ever running
    /// 14:00-16:00) could satisfy while 21+ hours were never observed at all, still nominally
    /// "comparing" the winner against something. See PeakHourTile's own doc comment for the full
    /// rule, including the scope-clause wording this unlocks/blocks.
    /// </summary>
    public const int PeakHourMinQualifyingBuckets = 12;

    /// <summary>The fixed size of the peak-hour bucket space -- one per local clock hour.</summary>
    public const int PeakHourTotalBuckets = 24;

    /// <summary>Same rule as PeakHourMinQualifyingBuckets, out of the 7 local weekdays -- see PeakWeekdayTile's doc comment.</summary>
    public const int PeakWeekdayMinQualifyingBuckets = 5;

    /// <summary>The fixed size of the peak-weekday bucket space -- one per local weekday.</summary>
    public const int PeakWeekdayTotalBuckets = 7;

    /// <summary>docs/statistics.md recommendation 2's pre-specified downsize threshold: "at most X % of the weekly budget", X fixed at 50.</summary>
    public const double WeeklyBudgetDownsizeThresholdPct = 50.0;

    /// <summary>The share of qualifying weeks at/below WeeklyBudgetDownsizeThresholdPct that must be cleared before the app is willing to say a smaller plan would probably suffice.</summary>
    public const double WeeklyBudgetDownsizeSharePct = 70.0;

    /// <summary>A week counts as "at or near the ceiling" once its peak_pct reaches this.</summary>
    public const double WeeklyBudgetNearCeilingPct = 90.0;

    /// <summary>The share of qualifying weeks at/near the ceiling that must be cleared before the app is willing to argue for a larger plan (or a more even spread).</summary>
    public const double WeeklyBudgetUpsizeSharePct = 50.0;

    /// <summary>Codex review #5: N>=MinObservations same-plan qualifying weeks is only a HEDGED early hint ("tidig indikation"); the plain, unhedged plan-size statement additionally requires this much elapsed span across that same-plan pool.</summary>
    public const double PlanAdviceFullConfidenceDays = 365.0;

    /// <summary>docs/statistics.md decision 4, "Recent cycles": the last ~2 weeks, one line per closed cycle, independent of whichever period preset the rest of the statistics window is showing.</summary>
    public const int RecentCyclesDays = 14;

    /// <summary>StatisticsDataLoader/StatisticsText's period cutoff for each preset, as a span back from "now" -- the single place that maps StatisticsPeriod to an actual duration.</summary>
    public static TimeSpan PeriodSpan(StatisticsPeriod period) => period switch
    {
        StatisticsPeriod.TwoWeeks => TimeSpan.FromDays(14),
        StatisticsPeriod.ThreeMonths => TimeSpan.FromDays(90),
        _ => TimeSpan.FromDays(365),
    };

    /// <summary>
    /// N is the number of DISTINCT COVERED LOCAL DAYS the winning weekday was observed on -- e.g.
    /// 12 distinct Fridays -- never the raw hourly-row count. Ready requires at least
    /// PeakWeekdayMinQualifyingBuckets (5) of the PeakWeekdayTotalBuckets (7) weekdays to EACH
    /// individually clear MinObservations in this same days-of-that-weekday unit (the 2026-09-21
    /// follow-up to Codex review #11 -- see PeakWeekdayMinQualifyingBuckets' own doc comment for
    /// why "any 2 buckets" was not enough: an app that only ever ran on weekends could satisfy
    /// that rule while five weekdays were never observed at all). The winner is chosen ONLY among
    /// the qualifying weekdays, and is itself always one of them. Weekday/N/AverageConsumedPct are
    /// null/0 while Ready is false -- the new learning-state line names no candidate, only the
    /// honest progress count ("Samlar data -- 3 av 5 veckodagar med tillräcklig data"; see
    /// QualifyingBuckets). QualifyingBuckets itself is populated in BOTH states: it drives that
    /// progress count while learning, and (whenever it is less than PeakWeekdayTotalBuckets) the
    /// claim's scope clause once Ready ("tisdag -- av de 5 veckodagar med data").
    /// </summary>
    public readonly record struct PeakWeekdayTile(bool Ready, int N, int Threshold, DayOfWeek? Weekday, double? AverageConsumedPct, int QualifyingBuckets);
    /// <summary>
    /// Hour is bucketed in whatever TimeZoneInfo Compute()/ComputeForAccount() was called with
    /// (local wall-clock time in production, UTC by default). N is the number of DISTINCT
    /// COVERED LOCAL DAYS the winning hour was observed on -- same "days, not hourly rows, winner
    /// chosen only among qualifying buckets, QualifyingBuckets drives both the learning-state
    /// progress count and the Ready scope clause" gating as PeakWeekdayTile's N; see that record's
    /// doc comment and PeakHourMinQualifyingBuckets' (12 of 24, not PeakWeekdayTile's 5 of 7).
    /// </summary>
    public readonly record struct PeakHourTile(bool Ready, int N, int Threshold, int? Hour, double? AverageConsumedPct, int QualifyingBuckets);
    public readonly record struct TimesAtCeilingTile(bool Ready, int N, int Threshold, int Count);
    public readonly record struct WeeklyBudgetShareTile(bool Ready, int N, int Threshold, double? AveragePeakPct);

    /// <summary>Codex review #7/#8: "how many warnings were followed by a ceiling hit", over forecast-eligible cycles (both prediction fields present, predicted_at_utc inside the cycle and strictly before the outcome -- ForecastEligible below) that were actually warned. Never conflated with recall (ForecastRecallTile) -- a cycle with no warning is neither a hit nor a miss for THIS number.</summary>
    public readonly record struct ForecastPrecisionTile(bool Ready, int N, int Threshold, double? Precision);
    /// <summary>Codex review #7/#8: "how many ceiling hits were warned in advance", over forecast-eligible cycles that actually hit the ceiling. The other half of the pair ForecastPrecisionTile reports -- together they replace the old single "accuracy" number, which was precision alone and could hide almost every missed event.</summary>
    public readonly record struct ForecastRecallTile(bool Ready, int N, int Threshold, double? Recall);

    /// <summary>
    /// "You hit the ceiling N times in the last M weeks." Codex review #6: the joint weekday x
    /// hour "most often" clause is REMOVED entirely -- it was the exact 168-cell argmax scan
    /// docs/statistics.md's own "garden of forking paths" warning forbids, and could be won by a
    /// single observation. Weekday/hour patterns are still available, but only from the separate,
    /// independently-gated PeakWeekday/PeakHour tiles (their own N-per-bucket and at-least-two-
    /// buckets thresholds, Codex review #11).
    /// </summary>
    public readonly record struct CeilingRecommendation(bool Ready, int N, int Threshold, int TimesHit, int WeeksSpanned);

    /// <summary>Which conclusion the weekly-budget recommendation's own numbers support, if either. None means neither direction is honest to claim yet: no card is shown at all, not a card with no conclusion.</summary>
    public enum WeeklyBudgetDirection { None, Downsize, Upsize }

    /// <summary>
    /// "You used at most X % of the weekly budget in N of the last M weeks" (Downsize) or "You
    /// reached at least Y % in N of the last M weeks" (Upsize). Codex review #1/#2/#5:
    ///
    ///  - N/M/WeeksAtOrBelow/WeeksNearCeiling are counted over the CURRENT-PLAN pool only: the
    ///    trailing run of weekly cycles (by reset_utc) that share the plan tier of the most
    ///    recently closed cycle of EITHER kind -- N restarts the instant the tier changes, and a
    ///    backfilled row (plan_tier always empty) can never be part of it. An account with no
    ///    live-tracked cycle yet (no tier known anywhere) gets an empty pool, hence Ready=false:
    ///    "unknown current tier -&gt; no plan advice" falls straight out of this, no separate flag
    ///    needed.
    ///  - Downsize additionally requires SessionEvidenceAvailable (at least one qualifying
    ///    session cycle overlapping the same pool's time span) and forbids it outright when
    ///    AnySessionCeilingHitInPeriod is true -- percentage-of-the-weekly-budget alone says
    ///    nothing about whether the user is hitting the tighter five-hour session ceiling daily.
    ///  - IsEarlyHint (true whenever SpanDays &lt; PlanAdviceFullConfidenceDays) marks the
    ///    two-stage rule: N&gt;=10 alone is only a HEDGED early hint ("tidig indikation, baserat
    ///    på N veckor"); the plain, unhedged statement additionally needs a full year of
    ///    same-plan history.
    /// </summary>
    public readonly record struct WeeklyBudgetRecommendation(
        bool Ready, int N, int Threshold, double ThresholdPct, int WeeksAtOrBelow, int WeeksConsidered,
        int WeeksNearCeiling = 0, bool AnyCeilingHitInPeriod = false, WeeklyBudgetDirection Direction = WeeklyBudgetDirection.None,
        bool SessionEvidenceAvailable = false, bool AnySessionCeilingHitInPeriod = false,
        bool IsEarlyHint = true, double SpanDays = 0.0, int SessionEvidenceCount = 0);

    public sealed record Result(
        PeakWeekdayTile PeakWeekday,
        PeakHourTile PeakHour,
        TimesAtCeilingTile TimesAtCeiling,
        WeeklyBudgetShareTile WeeklyBudgetShare,
        ForecastPrecisionTile ForecastPrecision,
        ForecastRecallTile ForecastRecall,
        CeilingRecommendation CeilingRecommendation,
        WeeklyBudgetRecommendation WeeklyBudgetRecommendation);

    /// <summary>Convenience entry point: reads cycles.csv and every hourly-*.csv under one account's log directory, then computes Result for that account. The pure computation itself is Compute(), below -- this is only the disk-reading shell around it. tz drives PeakWeekday/PeakHour's local-time bucketing (see the class doc comment); null defaults to UTC.</summary>
    public static Result ComputeForAccount(string accountLogDir, string accountKey, TimeZoneInfo? tz = null)
    {
        IReadOnlyList<CycleArchiveCsv.Row> cycles = CycleArchiveCsv.ReadAll(Path.Combine(accountLogDir, CycleArchiveCsv.FileName));
        var hours = new List<HourlyRollupCsv.Row>();
        foreach (string yearFile in HourlyRollupCsv.FindYearFiles(accountLogDir))
            hours.AddRange(HourlyRollupCsv.ReadAll(yearFile));
        return Compute(cycles, hours, accountKey, tz);
    }

    /// <summary>The pure computation: no file IO, no UI -- everything IN is UTC, exactly as cycles.csv/hourly-*.csv store it. tz is used only to bucket PeakWeekday/PeakHour in local wall-clock time (see the class doc comment); null defaults to UTC.</summary>
    public static Result Compute(IReadOnlyList<CycleArchiveCsv.Row> cycles, IReadOnlyList<HourlyRollupCsv.Row> hours, string accountKey, TimeZoneInfo? tz = null)
    {
        TimeZoneInfo effectiveTz = tz ?? TimeZoneInfo.Utc;
        List<CycleArchiveCsv.Row> sessionCycles = cycles
            .Where(c => c.AccountKey == accountKey && c.Window == WindowKind.Session && IsCycleCovered(c)).ToList();
        List<CycleArchiveCsv.Row> weeklyCycles = cycles
            .Where(c => c.AccountKey == accountKey && c.Window == WindowKind.Weekly && IsCycleCovered(c)).ToList();
        // The forecast tiles pool both window kinds -- a DryEarly warning is a warning
        // regardless of which window it was about, and pooling keeps N from starving separately
        // when either kind alone would stay below MinObservations for a while.
        List<CycleArchiveCsv.Row> allCoveredCycles = cycles
            .Where(c => c.AccountKey == accountKey && IsCycleCovered(c)).ToList();
        List<CycleArchiveCsv.Row> forecastEligible = allCoveredCycles.Where(ForecastEligible).ToList();
        List<HourlyRollupCsv.Row> sessionHours = hours
            .Where(h => h.AccountKey == accountKey && h.Window == WindowKind.Session && IsHourCovered(h)).ToList();

        return new Result(
            PeakWeekday: ComputePeakWeekday(sessionHours, effectiveTz),
            PeakHour: ComputePeakHour(sessionHours, effectiveTz),
            TimesAtCeiling: ComputeTimesAtCeiling(sessionCycles),
            WeeklyBudgetShare: ComputeWeeklyBudgetShare(weeklyCycles),
            ForecastPrecision: ComputeForecastPrecision(forecastEligible),
            ForecastRecall: ComputeForecastRecall(forecastEligible),
            CeilingRecommendation: ComputeCeilingRecommendation(sessionCycles),
            WeeklyBudgetRecommendation: ComputeWeeklyBudgetRecommendation(weeklyCycles, sessionCycles));
    }

    /// <summary>Public (StatisticsText's heatmap needs the identical gate -- Codex review #12): a cycle counts only if it was observed for at least CoverageThreshold of its own window length.</summary>
    public static bool IsCycleCovered(CycleArchiveCsv.Row c)
    {
        double windowMinutes = c.Window == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes;
        return windowMinutes > 0 && c.CoveredMinutes / windowMinutes >= CoverageThreshold;
    }

    static bool IsHourCovered(HourlyRollupCsv.Row h) => h.CoveredMinutes / 60.0 >= CoverageThreshold;

    /// <summary>
    /// Codex review #8: a cycle is forecast-eligible only when BOTH prediction fields are
    /// present, predicted_at_utc falls inside the cycle's own [started_utc, reset_utc] span, and
    /// predicted_at_utc is strictly before the outcome it is being scored against -- reset_utc
    /// for a cycle that never hit the ceiling, or the (possibly interval-censored, that's fine
    /// for this purpose -- day/hour precision isn't needed here) ceiling-reached instant for one
    /// that did. A "prediction" timestamped after the ceiling was already reached is not an
    /// advance warning and must not count toward either precision or recall.
    /// </summary>
    static bool ForecastEligible(CycleArchiveCsv.Row c)
    {
        if (c.PredictedPeakPct is null || c.PredictedAtUtc is not { } predictedAt) return false;
        if (predictedAt < c.StartedUtc || predictedAt > c.ResetUtc) return false;
        DateTimeOffset outcomeAt = c.HitCeiling && c.CeilingReachedAtUtc is { } ca ? ca : c.ResetUtc;
        return predictedAt < outcomeAt;
    }

    /// <summary>The class doc comment's local-time bucketing: a UTC instant converted to tz always yields exactly one wall-clock reading, so this never double-counts or drops an hourly row across a DST change.</summary>
    static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz);

    /// <summary>
    /// Codex review #10/#16: aggregates hourly consumption to LOCAL-DAY TOTALS first (summing
    /// every row that lands on the same local calendar date within this bucket -- this is what
    /// correctly collapses a DST fall-back's two repeated local hours into one observation, #16,
    /// while still summing their genuinely-separate consumption into that one day's total), then
    /// averages ACROSS those distinct-day totals -- never a flat mean of the raw rows, which let
    /// a bucket with many short, low-usage hourly rows outweigh a bucket with fewer but larger
    /// ones (finding #10's Monday-1-hour-at-50% vs Tuesday-8-hours-at-10%-each example).
    /// </summary>
    static IReadOnlyList<(TKey Key, int Days, double Average)> AggregateByLocalDayTotals<TKey>(
        IEnumerable<(TKey Key, DateTimeOffset Local, double ConsumedPct)> rows) where TKey : notnull
    {
        return rows
            .GroupBy(x => x.Key)
            .Select(g =>
            {
                List<double> dayTotals = g.GroupBy(x => x.Local.Date).Select(dg => dg.Sum(x => x.ConsumedPct)).ToList();
                return (Key: g.Key, Days: dayTotals.Count, Average: dayTotals.Count > 0 ? dayTotals.Average() : 0.0);
            })
            .ToList();
    }

    /// <summary>
    /// 2026-09-21 follow-up to Codex review #11: Ready now requires at least
    /// PeakWeekdayMinQualifyingBuckets (5) of the 7 weekdays to EACH individually clear
    /// MinObservations in DISTINCT COVERED LOCAL DAYS -- not merely "any 2 buckets both
    /// qualify", which let a narrowly-focused pattern (e.g. weekend-only usage) claim a peak
    /// weekday while five weekdays were never observed. The winner is picked from the QUALIFYING
    /// set only, so a below-threshold weekday can never win by default.
    /// </summary>
    static PeakWeekdayTile ComputePeakWeekday(IReadOnlyList<HourlyRollupCsv.Row> sessionHours, TimeZoneInfo tz)
    {
        if (sessionHours.Count == 0) return new PeakWeekdayTile(false, 0, MinObservations, null, null, 0);

        var buckets = AggregateByLocalDayTotals(
            sessionHours.Select(h => (Key: ToLocal(h.HourStartUtc, tz).DayOfWeek, Local: ToLocal(h.HourStartUtc, tz), ConsumedPct: h.ConsumedPct)));

        var qualifying = buckets.Where(b => b.Days >= MinObservations).ToList();
        bool ready = qualifying.Count >= PeakWeekdayMinQualifyingBuckets;
        if (!ready) return new PeakWeekdayTile(false, 0, MinObservations, null, null, qualifying.Count);

        var leader = qualifying.OrderByDescending(b => b.Average).ThenBy(b => b.Key).First();
        return new PeakWeekdayTile(true, leader.Days, MinObservations, leader.Key, leader.Average, qualifying.Count);
    }

    /// <summary>Same fix as ComputePeakWeekday -- see that method's doc comment and PeakHourMinQualifyingBuckets' (12 of 24, not 5 of 7).</summary>
    static PeakHourTile ComputePeakHour(IReadOnlyList<HourlyRollupCsv.Row> sessionHours, TimeZoneInfo tz)
    {
        if (sessionHours.Count == 0) return new PeakHourTile(false, 0, MinObservations, null, null, 0);

        var buckets = AggregateByLocalDayTotals(
            sessionHours.Select(h => (Key: ToLocal(h.HourStartUtc, tz).Hour, Local: ToLocal(h.HourStartUtc, tz), ConsumedPct: h.ConsumedPct)));

        var qualifying = buckets.Where(b => b.Days >= MinObservations).ToList();
        bool ready = qualifying.Count >= PeakHourMinQualifyingBuckets;
        if (!ready) return new PeakHourTile(false, 0, MinObservations, null, null, qualifying.Count);

        var leader = qualifying.OrderByDescending(b => b.Average).ThenBy(b => b.Key).First();
        return new PeakHourTile(true, leader.Days, MinObservations, leader.Key, leader.Average, qualifying.Count);
    }

    static TimesAtCeilingTile ComputeTimesAtCeiling(IReadOnlyList<CycleArchiveCsv.Row> sessionCycles)
    {
        int n = sessionCycles.Count;
        if (n < MinObservations) return new TimesAtCeilingTile(false, n, MinObservations, 0);
        return new TimesAtCeilingTile(true, n, MinObservations, sessionCycles.Count(c => c.HitCeiling));
    }

    static WeeklyBudgetShareTile ComputeWeeklyBudgetShare(IReadOnlyList<CycleArchiveCsv.Row> weeklyCycles)
    {
        int n = weeklyCycles.Count;
        if (n < MinObservations) return new WeeklyBudgetShareTile(false, n, MinObservations, null);
        return new WeeklyBudgetShareTile(true, n, MinObservations, weeklyCycles.Average(c => c.PeakPct));
    }

    static ForecastPrecisionTile ComputeForecastPrecision(IReadOnlyList<CycleArchiveCsv.Row> forecastEligible)
    {
        List<CycleArchiveCsv.Row> warned = forecastEligible.Where(c => c.WarnedDryEarly).ToList();
        int n = warned.Count;
        if (n < MinObservations) return new ForecastPrecisionTile(false, n, MinObservations, null);
        return new ForecastPrecisionTile(true, n, MinObservations, warned.Count(c => c.HitCeiling) / (double)n);
    }

    static ForecastRecallTile ComputeForecastRecall(IReadOnlyList<CycleArchiveCsv.Row> forecastEligible)
    {
        List<CycleArchiveCsv.Row> hits = forecastEligible.Where(c => c.HitCeiling).ToList();
        int n = hits.Count;
        if (n < MinObservations) return new ForecastRecallTile(false, n, MinObservations, null);
        return new ForecastRecallTile(true, n, MinObservations, hits.Count(c => c.WarnedDryEarly) / (double)n);
    }

    /// <summary>Codex review #6: no weekday/hour clause here any more -- see CeilingRecommendation's doc comment.</summary>
    static CeilingRecommendation ComputeCeilingRecommendation(IReadOnlyList<CycleArchiveCsv.Row> sessionCycles)
    {
        int n = sessionCycles.Count;
        if (n < MinObservations) return new CeilingRecommendation(false, n, MinObservations, 0, 0);

        int timesHit = sessionCycles.Count(c => c.HitCeiling);
        DateTimeOffset earliest = sessionCycles.Min(c => c.ResetUtc);
        DateTimeOffset latest = sessionCycles.Max(c => c.ResetUtc);
        int weeksSpanned = Math.Max(1, (int)Math.Ceiling((latest - earliest).TotalDays / 7.0));

        return new CeilingRecommendation(true, n, MinObservations, timesHit, weeksSpanned);
    }

    /// <summary>The moment a hit cycle's ceiling was reached, per Codex review #14's stored column -- falls back to the old reset-minus-blocked derivation only for a row that somehow has HitCeiling=true with no stored instant (should not happen after the schema-2 migration, but never throw over it).</summary>
    static DateTimeOffset CeilingReachedAt(CycleArchiveCsv.Row c) => c.CeilingReachedAtUtc ?? c.ResetUtc.AddMinutes(-c.BlockedMinutes);

    /// <summary>Codex review #2: the tier of the most recently CLOSED cycle (session or weekly, whichever is more recent) that recorded one at all. Null when nothing ever has -- a 100% backfilled/legacy history, or an account that has never completed a single live-tracked cycle.</summary>
    static string? CurrentPlanTier(IReadOnlyList<CycleArchiveCsv.Row> weeklyCycles, IReadOnlyList<CycleArchiveCsv.Row> sessionCycles)
    {
        List<CycleArchiveCsv.Row> withTier = weeklyCycles.Concat(sessionCycles).Where(c => c.PlanTier != null).ToList();
        return withTier.Count == 0 ? null : withTier.OrderByDescending(c => c.ResetUtc).First().PlanTier;
    }

    /// <summary>Codex review #2: the trailing run of weekly cycles (by reset_utc, ascending) that all share currentTier -- N restarts at the first (scanning backward from the most recent) cycle whose plan_tier differs, INCLUDING a backfilled row (plan_tier always null, so it can never equal currentTier and always breaks the run).</summary>
    static List<CycleArchiveCsv.Row> CurrentPlanTrailingRun(IReadOnlyList<CycleArchiveCsv.Row> weeklyCycles, string currentTier)
    {
        List<CycleArchiveCsv.Row> sorted = weeklyCycles.OrderBy(c => c.ResetUtc).ToList();
        int start = sorted.Count;
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            if (sorted[i].PlanTier != currentTier) break;
            start = i;
        }
        return sorted.GetRange(start, sorted.Count - start);
    }

    /// <summary>
    /// Codex review #1/#2/#5 -- see WeeklyBudgetRecommendation's doc comment for the full rule.
    /// Downsize is checked first and is mutually exclusive with Upsize by construction (a week
    /// can't be both &lt;=50% and &gt;=90%). Neither share clearing its threshold, or Downsize's
    /// own share clearing but being vetoed by the session guard, leaves Direction=None -- the
    /// honest "neutral middle"/"can't defend it" outcome: no conclusion, no card at all.
    /// </summary>
    static WeeklyBudgetRecommendation ComputeWeeklyBudgetRecommendation(
        IReadOnlyList<CycleArchiveCsv.Row> weeklyCyclesAllCovered, IReadOnlyList<CycleArchiveCsv.Row> sessionCyclesAllCovered)
    {
        string? currentTier = CurrentPlanTier(weeklyCyclesAllCovered, sessionCyclesAllCovered);
        List<CycleArchiveCsv.Row> pool = currentTier is null
            ? new List<CycleArchiveCsv.Row>()
            : CurrentPlanTrailingRun(weeklyCyclesAllCovered, currentTier);

        int n = pool.Count;
        if (n < MinObservations)
            return new WeeklyBudgetRecommendation(false, n, MinObservations, WeeklyBudgetDownsizeThresholdPct, 0, 0);

        int atOrBelow = pool.Count(c => c.PeakPct <= WeeklyBudgetDownsizeThresholdPct);
        int nearCeiling = pool.Count(c => c.PeakPct >= WeeklyBudgetNearCeilingPct);
        bool anyWeeklyCeilingHit = pool.Any(c => c.HitCeiling);

        DateTimeOffset poolEarliest = pool.Min(c => c.StartedUtc);
        DateTimeOffset poolLatest = pool.Max(c => c.ResetUtc);

        // Codex review #1: session evidence over the SAME current-plan time span -- percentage of
        // the weekly budget says nothing about whether the tighter five-hour session ceiling was
        // also being hit throughout that period.
        List<CycleArchiveCsv.Row> sessionEvidence = sessionCyclesAllCovered
            .Where(c => c.ResetUtc >= poolEarliest && c.StartedUtc <= poolLatest).ToList();
        bool sessionEvidenceAvailable = sessionEvidence.Count > 0;
        bool anySessionCeilingHit = sessionEvidence.Any(c => c.HitCeiling);

        bool downsizeShareMet = !anyWeeklyCeilingHit && atOrBelow * 100.0 / n >= WeeklyBudgetDownsizeSharePct;
        bool downsizeAllowed = downsizeShareMet && sessionEvidenceAvailable && !anySessionCeilingHit;

        WeeklyBudgetDirection direction = WeeklyBudgetDirection.None;
        if (downsizeAllowed) direction = WeeklyBudgetDirection.Downsize;
        else if (nearCeiling * 100.0 / n >= WeeklyBudgetUpsizeSharePct) direction = WeeklyBudgetDirection.Upsize;

        double spanDays = (poolLatest - poolEarliest).TotalDays;
        bool isEarlyHint = spanDays < PlanAdviceFullConfidenceDays;

        return new WeeklyBudgetRecommendation(
            true, n, MinObservations, WeeklyBudgetDownsizeThresholdPct, atOrBelow, n,
            nearCeiling, anyWeeklyCeilingHit, direction,
            sessionEvidenceAvailable, anySessionCeilingHit, isEarlyHint, spanDays, sessionEvidence.Count);
    }
}
