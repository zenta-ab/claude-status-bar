using System.Linq;
using System.Text.RegularExpressions;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;
using RecentCycle = ClaudeStatusBar.Data.StatisticsDataLoader.RecentCycle;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/statistics.md decision 4: Ui/StatisticsText.Compose's exact wording per tile state
/// (learning vs unlocked), the two recommendations (hidden until Ready), the recent-cycles
/// incomplete-coverage marker, and the heatmap's per-day levels/tooltips. Every input here is
/// built from real CycleArchiveCsv.Row/HourlyRollupCsv.Row values run through the real
/// StatisticsEngine, mirroring how StatisticsTests.cs already exercises the engine itself.
/// Rewritten 2026-09-21 against docs/reviews/2026-09-21-codex-statistics.md.
/// </summary>
public class StatisticsTextTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.Utc;
    static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Same width-guard regex as PanelTextTests.cs: a raw minute count above 60 is unreadable and must never leak into any composed string.</summary>
    static readonly Regex RawMinutes = new(@"\b([6-9][0-9]|[1-9][0-9]{2,}) min\b");

    // Codex review #1: anchored on the SAME -400-day epoch as WeeklyCycle, not an independent
    // -60-day one -- the weekly-budget recommendation's session-evidence guard requires these to
    // temporally overlap the weekly pool's own span, and a mismatched anchor would silently make
    // "session evidence available" false for every test that does not override the dates itself.
    static CycleArchiveCsv.Row SessionCycle(int index, bool hitCeiling, double coveredFraction = 1.0, bool warnedDryEarly = false) => new(
        AccountKey: "acct", Window: WindowKind.Session,
        StartedUtc: Now.AddDays(-400).AddHours(6 * index),
        ResetUtc: Now.AddDays(-400).AddHours(6 * index).AddMinutes(QuotaWindows.SessionMinutes),
        PeakPct: hitCeiling ? 100.0 : 40.0, FinalPct: hitCeiling ? 100.0 : 40.0, HitCeiling: hitCeiling,
        BlockedMinutes: hitCeiling ? 30.0 : 0.0, CoveredMinutes: QuotaWindows.SessionMinutes * coveredFraction,
        PlanTier: "max", WarnedDryEarly: warnedDryEarly, PredictedPeakPct: null, PredictedAtUtc: null,
        CeilingReachedAtUtc: hitCeiling ? Now.AddDays(-400).AddHours(6 * index).AddMinutes(QuotaWindows.SessionMinutes - 30) : null,
        CeilingReachedCensored: false);

    static CycleArchiveCsv.Row WeeklyCycle(int index, double peakPct, string? tier = "max") => new(
        AccountKey: "acct", Window: WindowKind.Weekly,
        StartedUtc: Now.AddDays(-400).AddDays(7 * index),
        ResetUtc: Now.AddDays(-400).AddDays(7 * index).AddMinutes(QuotaWindows.WeeklyMinutes),
        PeakPct: peakPct, FinalPct: peakPct, HitCeiling: false,
        BlockedMinutes: 0.0, CoveredMinutes: QuotaWindows.WeeklyMinutes, PlanTier: tier,
        WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);

    static HourlyRollupCsv.Row SessionHour(DateTimeOffset hourStartUtc, double consumedPct) =>
        new("acct", WindowKind.Session, hourStartUtc, consumedPct, Samples: 2, CoveredMinutes: 55.0);

    static StatisticsDataLoader.LoadedData Loaded(
        IReadOnlyList<CycleArchiveCsv.Row> cycles, IReadOnlyList<HourlyRollupCsv.Row>? hours = null,
        IReadOnlyList<StatisticsDataLoader.RecentCycle>? recent = null) =>
        new(StatisticsEngine.Compute(cycles, hours ?? Array.Empty<HourlyRollupCsv.Row>(), "acct", Tz),
            cycles, recent ?? Array.Empty<StatisticsDataLoader.RecentCycle>());

    static void AssertNoRawMinutes(StatisticsWindowText text)
    {
        foreach (TileText t in text.Tiles)
        {
            Assert.False(RawMinutes.IsMatch(t.ValueLine), $"raw minute count in tile value: {t.ValueLine}");
            Assert.False(RawMinutes.IsMatch(t.NLine), $"raw minute count in tile N line: {t.NLine}");
        }
        foreach (RecommendationText? r in new[] { text.CeilingRecommendation, text.WeeklyBudgetRecommendation })
        {
            if (r is null) continue;
            Assert.False(RawMinutes.IsMatch(r.Line), $"raw minute count in recommendation: {r.Line}");
            Assert.False(RawMinutes.IsMatch(r.NLine), $"raw minute count in recommendation N line: {r.NLine}");
        }
        foreach (RecentCycleText c in text.RecentCycles)
        {
            Assert.False(RawMinutes.IsMatch(c.RangeLine), $"raw minute count in recent-cycle range: {c.RangeLine}");
            Assert.False(RawMinutes.IsMatch(c.PeakLine), $"raw minute count in recent-cycle peak: {c.PeakLine}");
            if (c.IncompleteLine != null) Assert.False(RawMinutes.IsMatch(c.IncompleteLine), $"raw minute count in incomplete line: {c.IncompleteLine}");
        }
    }

    // ==================== Tiles: learning vs unlocked ====================

    [Fact]
    public void TimesAtCeilingTile_BelowThreshold_ShowsLearningState_NeverTheRealCount()
    {
        var cycles = Enumerable.Range(0, 3).Select(i => SessionCycle(i, hitCeiling: i == 0)).ToList();
        StatisticsWindowText text = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now);

        TileText tile = text.Tiles.Single(t => t.Title == "Nådde taket");
        Assert.False(tile.Ready);
        Assert.Contains("Samlar data", tile.ValueLine);
        Assert.DoesNotContain("1 gång", tile.ValueLine);
        Assert.Equal("Samlar data — 3 av 10 sessioner", tile.NLine);
        AssertNoRawMinutes(text);
    }

    [Fact]
    public void TimesAtCeilingTile_AtThreshold_ShowsTheRealCount_WithN()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: i < 4)).ToList();
        StatisticsWindowText text = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now);

        TileText tile = text.Tiles.Single(t => t.Title == "Nådde taket");
        Assert.True(tile.Ready);
        Assert.Equal("4 gånger", tile.ValueLine);
        Assert.Equal("baserat på 10 sessioner", tile.NLine);
    }

    /// <summary>
    /// Pads `hours` up to StatisticsEngine.PeakHourMinQualifyingBuckets distinct qualifying HOUR
    /// buckets (own dedicated DST-safe year per hour -- see StatisticsTests.cs's identically-named
    /// helper for the full rationale). Every padding row lands on `anchorWeekday` (Saturday by
    /// default, since no test in this file asserts anything exact about Saturday) -- a test that
    /// ALSO pads weekday buckets in the same fixture should pass one of ITS OWN already-qualifying,
    /// non-winner weekdays here instead, so this padding is folded into a bucket the weekday
    /// assertions already account for rather than silently creating an extra (unasserted) one.
    /// </summary>
    static List<HourlyRollupCsv.Row> PadHourBuckets(int alreadyQualifying, IReadOnlyCollection<int> excludeHours, double pct = 1.0, DayOfWeek anchorWeekday = DayOfWeek.Saturday)
    {
        var rows = new List<HourlyRollupCsv.Row>();
        int needed = StatisticsEngine.PeakHourMinQualifyingBuckets - alreadyQualifying;
        int added = 0;
        for (int hour = 0; hour < 24 && added < needed; hour++)
        {
            if (excludeHours.Contains(hour)) continue;
            DateTimeOffset firstAnchorDay = FirstDateOfWeekday(2050 + hour, 12, anchorWeekday).AddHours(hour);
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                rows.Add(SessionHour(firstAnchorDay.AddDays(7 * w), pct));
            added++;
        }
        return rows;
    }

    /// <summary>
    /// Pads `hours` up to StatisticsEngine.PeakWeekdayMinQualifyingBuckets distinct qualifying
    /// WEEKDAY buckets (own dedicated DST-safe year per weekday). Every padding row lands at
    /// `anchorHour` (noon by default) -- a test that ALSO pads hour buckets in the same fixture
    /// should pass one of ITS OWN already-qualifying, non-winner hours here instead, for the same
    /// "fold into an accounted-for bucket, don't silently create an extra one" reason PadHourBuckets'
    /// own anchorWeekday parameter exists.
    /// </summary>
    static List<HourlyRollupCsv.Row> PadWeekdayBuckets(int alreadyQualifying, IReadOnlyCollection<DayOfWeek> excludeWeekdays, double pct = 1.0, int anchorHour = 12)
    {
        var rows = new List<HourlyRollupCsv.Row>();
        int needed = StatisticsEngine.PeakWeekdayMinQualifyingBuckets - alreadyQualifying;
        int added = 0;
        for (int dow = 0; dow < 7 && added < needed; dow++)
        {
            var weekday = (DayOfWeek)dow;
            if (excludeWeekdays.Contains(weekday)) continue;
            DateTimeOffset firstOccurrence = FirstDateOfWeekday(2100 + dow, 12, weekday).AddHours(anchorHour);
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                rows.Add(SessionHour(firstOccurrence.AddDays(7 * w), pct));
            added++;
        }
        return rows;
    }

    static DateTimeOffset FirstDateOfWeekday(int year, int month, DayOfWeek weekday)
    {
        var first = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
        int diff = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(diff);
    }

    [Fact]
    public void PeakWeekdayAndHourTiles_AtThreshold_ShowWeekdayAndHourRange()
    {
        DateTimeOffset firstMonday = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero); // Monday
        var hours = Enumerable.Range(0, StatisticsEngine.MinObservations)
            .Select(i => SessionHour(firstMonday.AddDays(7 * i), 20.0)).ToList();
        DateTimeOffset firstWednesday = new(2026, 1, 7, 9, 0, 0, TimeSpan.Zero);
        hours.AddRange(Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionHour(firstWednesday.AddDays(7 * i), 2.0)));
        // The new 5-of-7-weekdays / 12-of-24-hours rule (docs/statistics.md, 2026-09-21): pad up
        // to both quorums -- exactly to the minimum, so both tiles show their scope clause below.
        // Anchored on hour 9 -- Wednesday's own real bucket above, already qualifying and not the
        // winner -- so this weekday-side padding folds into an already-accounted-for hour instead
        // of silently creating a 13th one.
        hours.AddRange(PadWeekdayBuckets(alreadyQualifying: 2, excludeWeekdays: new[] { DayOfWeek.Monday, DayOfWeek.Wednesday }, pct: 2.0, anchorHour: 9));
        // Anchored on Sunday -- one of the three weekdays PadWeekdayBuckets just padded above --
        // so this hour-side padding folds into an already-qualifying, non-winner weekday instead
        // of silently creating a 6th one the assertion below doesn't expect.
        hours.AddRange(PadHourBuckets(alreadyQualifying: 2, excludeHours: new[] { 14, 9 }, pct: 2.0, anchorWeekday: DayOfWeek.Sunday));
        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);

        TileText weekday = text.Tiles.Single(t => t.Title == "Mest aktiva veckodagen");
        Assert.True(weekday.Ready);
        Assert.Equal("Måndag — av de 5 veckodagar med data", weekday.ValueLine);

        TileText hour = text.Tiles.Single(t => t.Title == "Mest aktiva timmen");
        Assert.True(hour.Ready);
        Assert.Equal("kl 14–15 — av de 12 timmar datorn var igång", hour.ValueLine);
    }

    // ==================== 2026-09-21 follow-up to Codex review #11: 12-of-24 / 5-of-7 quorum ====================

    [Fact]
    public void PeakHourTile_OnlyThreeHoursEverObserved_StaysInLearningState_WithProgressCount()
    {
        // The app ran only 14:00-16:00 for 20 days -- three well-covered hour buckets (14, 15,
        // 16), each far past MinObservations on its own, but nowhere near the new 12-of-24 quorum.
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset baseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int d = 0; d < 20; d++)
            for (int h = 14; h < 17; h++)
                hours.Add(SessionHour(baseDate.AddDays(d).AddHours(h), 20.0));

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva timmen");

        Assert.False(tile.Ready);
        Assert.Equal("Samlar data — 3 av 12 timmar med tillräcklig data", tile.NLine);
    }

    [Fact]
    public void PeakHourTile_FourteenQualifyingHours_MakesAClaim_WithScopeClause()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset baseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int h = 0; h < 14; h++)
            for (int d = 0; d < StatisticsEngine.MinObservations; d++)
                hours.Add(SessionHour(baseDate.AddDays(h * 15 + d).AddHours(h), h == 5 ? 90.0 : 5.0));

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva timmen");

        Assert.True(tile.Ready);
        Assert.Equal("kl 05–06 — av de 14 timmar datorn var igång", tile.ValueLine);
    }

    [Fact]
    public void PeakHourTile_AllTwentyFourHoursQualify_MakesAClaim_WithNoScopeClause()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset baseDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (int h = 0; h < 24; h++)
            for (int d = 0; d < StatisticsEngine.MinObservations; d++)
                hours.Add(SessionHour(baseDate.AddDays(h * 15 + d).AddHours(h), h == 10 ? 90.0 : 5.0));

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva timmen");

        Assert.True(tile.Ready);
        Assert.Equal("kl 10–11", tile.ValueLine);
        Assert.DoesNotContain("av de", tile.ValueLine);
    }

    [Fact]
    public void PeakWeekdayTile_OnlyWeekendDaysObserved_StaysInLearningState()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset firstSaturday = new(2026, 1, 3, 9, 0, 0, TimeSpan.Zero); // Saturday
        DateTimeOffset firstSunday = new(2026, 1, 4, 9, 0, 0, TimeSpan.Zero); // Sunday
        for (int w = 0; w < StatisticsEngine.MinObservations; w++)
        {
            hours.Add(SessionHour(firstSaturday.AddDays(7 * w), 20.0));
            hours.Add(SessionHour(firstSunday.AddDays(7 * w), 20.0));
        }

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva veckodagen");

        Assert.False(tile.Ready);
        Assert.Equal("Samlar data — 2 av 5 veckodagar med tillräcklig data", tile.NLine);
    }

    [Fact]
    public void PeakWeekdayTile_FiveQualifyingWeekdays_MakesAClaim_WithScopeClause()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset firstMonday = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero); // Monday
        for (int dow = 0; dow < 5; dow++)
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                hours.Add(SessionHour(firstMonday.AddDays(dow).AddDays(7 * w), dow == 0 ? 90.0 : 5.0));

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva veckodagen");

        Assert.True(tile.Ready);
        Assert.Equal("Måndag — av de 5 veckodagar med data", tile.ValueLine);
    }

    [Fact]
    public void PeakWeekdayTile_AllSevenWeekdaysQualify_MakesAClaim_WithNoScopeClause()
    {
        var hours = new List<HourlyRollupCsv.Row>();
        DateTimeOffset firstMonday = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero); // Monday
        for (int dow = 0; dow < 7; dow++)
            for (int w = 0; w < StatisticsEngine.MinObservations; w++)
                hours.Add(SessionHour(firstMonday.AddDays(dow).AddDays(7 * w), dow == 0 ? 90.0 : 5.0));

        StatisticsWindowText text = StatisticsText.Compose(Loaded(Array.Empty<CycleArchiveCsv.Row>(), hours), StatisticsPeriod.Year, Tz, Now);
        TileText tile = text.Tiles.Single(t => t.Title == "Mest aktiva veckodagen");

        Assert.True(tile.Ready);
        Assert.Equal("Måndag", tile.ValueLine);
        Assert.DoesNotContain("av de", tile.ValueLine);
    }

    [Fact]
    public void WeeklyBudgetShareTile_BelowThreshold_IsLearning_AtThreshold_ShowsAverage()
    {
        var few = Enumerable.Range(0, 3).Select(i => WeeklyCycle(i, 40.0)).ToList();
        StatisticsWindowText belowText = StatisticsText.Compose(Loaded(few), StatisticsPeriod.Year, Tz, Now);
        TileText belowTile = belowText.Tiles.Single(t => t.Title == "Veckobudget använd");
        Assert.False(belowTile.Ready);
        Assert.Equal("Samlar data — 3 av 10 veckor", belowTile.NLine);

        var enough = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, 40.0)).ToList();
        StatisticsWindowText atText = StatisticsText.Compose(Loaded(enough), StatisticsPeriod.Year, Tz, Now);
        TileText atTile = atText.Tiles.Single(t => t.Title == "Veckobudget använd");
        Assert.True(atTile.Ready);
        Assert.Equal("40 % i snitt", atTile.ValueLine);
    }

    [Fact]
    public void ForecastTiles_ReportPrecisionAndRecallSeparately()
    {
        // Codex review #7: precision ("varningar som stämde") and recall ("takträffar som
        // varnades") are two SEPARATE tiles now, never one "accuracy" number.
        // 20 warned+eligible cycles (clears precision's own N>=10), 10 of which hit the ceiling
        // (clears recall's own N>=10 too) -- precision = 10/20 = 50%, recall = 10/10 = 100%.
        var cycles = new List<CycleArchiveCsv.Row>();
        for (int i = 0; i < 20; i++)
        {
            bool hit = i < 10;
            cycles.Add(SessionCycle(i, hitCeiling: hit, warnedDryEarly: true) with
            {
                PredictedPeakPct = 95.0,
                PredictedAtUtc = Now.AddDays(-400).AddHours(6 * i).AddMinutes(QuotaWindows.SessionMinutes / 2.0),
            });
        }
        StatisticsWindowText text = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now);

        TileText precision = text.Tiles.Single(t => t.Title == "Varningar som stämde");
        Assert.True(precision.Ready);
        Assert.Equal("50 % följdes av takträff", precision.ValueLine);
        Assert.Equal("baserat på 20 varningar", precision.NLine);

        TileText recall = text.Tiles.Single(t => t.Title == "Takträffar som varnades");
        Assert.True(recall.Ready);
        Assert.Equal("100 % varnades i förväg", recall.ValueLine); // every one of the 10 hits was warned in this data
        Assert.Equal("baserat på 10 takträffar", recall.NLine);
    }

    // ==================== Recommendations: hidden until Ready ====================

    [Fact]
    public void CeilingRecommendation_Null_UntilItsOwnThreshold_ThenStatesTheCount_NeverAWeekdayOrHourClause()
    {
        var few = Enumerable.Range(0, 3).Select(i => SessionCycle(i, hitCeiling: i == 0)).ToList();
        Assert.Null(StatisticsText.Compose(Loaded(few), StatisticsPeriod.Year, Tz, Now).CeilingRecommendation);

        var cycles = new List<CycleArchiveCsv.Row>();
        for (int i = 0; i < 3; i++) cycles.Add(SessionCycle(i, hitCeiling: true));
        for (int i = 3; i < StatisticsEngine.MinObservations; i++) cycles.Add(SessionCycle(i, hitCeiling: false));

        RecommendationText? rec = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now).CeilingRecommendation;
        Assert.NotNull(rec);
        Assert.Contains("3 gånger", rec!.Line);
        Assert.Contains("baserat på 10 sessioner", rec.NLine);
        // Codex review #6: the joint weekday x hour "most often" clause is gone entirely.
        Assert.DoesNotContain("oftast", rec.Line);
        Assert.DoesNotContain("kl ", rec.Line);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_Null_UntilThreshold_ThenStatesTheSplit()
    {
        var few = Enumerable.Range(0, 3).Select(i => WeeklyCycle(i, 30.0)).ToList();
        Assert.Null(StatisticsText.Compose(Loaded(few), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation);

        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(cycles.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.NotNull(rec);
        Assert.Contains("50 %", rec!.Line);
        Assert.Contains("7 av de senaste 10", rec.Line);
    }

    // ==================== A "0 times" ceiling recommendation is not advice ====================

    [Fact]
    public void CeilingRecommendation_ZeroTimesHit_IsHidden_TheTileAlreadySaysZero()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();
        StatisticsWindowText text = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now);
        Assert.Null(text.CeilingRecommendation);

        TileText tile = text.Tiles.Single(t => t.Title == "Nådde taket");
        Assert.True(tile.Ready);
        Assert.Equal("0 gånger", tile.ValueLine);

        StatisticsEngine.Result engineResult = StatisticsEngine.Compute(cycles, Array.Empty<HourlyRollupCsv.Row>(), "acct", Tz);
        Assert.True(engineResult.CeilingRecommendation.Ready);
        Assert.Equal(0, engineResult.CeilingRecommendation.TimesHit);
        Assert.Null(StatisticsAdvice.CeilingSignature(engineResult.CeilingRecommendation));
    }

    [Fact]
    public void CeilingRecommendation_OneHit_OneWeek_UsesSingularSwedishWording()
    {
        var cycles = new List<CycleArchiveCsv.Row>
        {
            SessionCycle(0, hitCeiling: true) with { StartedUtc = Now.AddDays(-1), ResetUtc = Now.AddDays(-1).AddMinutes(QuotaWindows.SessionMinutes) },
        };
        for (int i = 1; i < StatisticsEngine.MinObservations; i++)
            cycles.Add(SessionCycle(i, hitCeiling: false) with
            {
                StartedUtc = Now.AddDays(-1).AddHours(-i), ResetUtc = Now.AddDays(-1).AddHours(-i).AddMinutes(QuotaWindows.SessionMinutes),
            });

        RecommendationText? rec = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now).CeilingRecommendation;
        Assert.NotNull(rec);
        Assert.Contains("1 gång", rec!.Line);
        Assert.DoesNotContain("1 gånger", rec.Line);
        Assert.Contains("senaste veckan", rec.Line);
        Assert.DoesNotContain("de senaste 1 veckorna", rec.Line);
    }

    // ==================== Recommendation 2 states its conclusion; #1/#2/#5/#9 wording ====================

    [Fact]
    public void WeeklyBudgetRecommendation_DownsizeDirection_AddsTheDownsizeConclusion_AndCitesTheSessionGuard()
    {
        // 8 of 10 qualifying weeks at/below 50% (80% >= the 70% downsize share), none hit the
        // ceiling, AND (Codex review #1) clean qualifying session evidence over the same span.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 8 ? 30.0 : 90.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.NotNull(rec);
        Assert.Contains("mindre plan", rec!.Line);
        Assert.Contains("session", rec.NLine); // Codex review #1: the card states which constraints were evaluated
    }

    [Fact]
    public void WeeklyBudgetRecommendation_NeverSuggestsDownsizing_WhenThePeriodAlsoHitTheCeiling()
    {
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 8 ? 30.0 : 90.0)).ToList();
        weekly[0] = weekly[0] with { HitCeiling = true };
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();

        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.Null(rec);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_NeverSuggestsDownsizing_WhenTheSessionCeilingWasHit()
    {
        // Codex review #1's exact concrete input.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 7 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: true)).ToList();

        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.Null(rec);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_UpsizeDirection_UsesNearCeilingWording_NeverSlarITaket()
    {
        // Codex review #9's exact bug shape: near-ceiling weeks with ZERO actual ceiling hits
        // must never say "0 av 10 veckor -- du slår ofta i taket". Upsize states ITS OWN evidence
        // (weeks near the ceiling), never the downsize framing's numbers.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 6 ? 95.0 : 40.0)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.NotNull(rec);
        Assert.Contains("större plan", rec!.Line);
        Assert.Contains("nära taket", rec.Line);
        Assert.Contains("6 av de senaste 10", rec.Line); // the near-ceiling count, never "0 av 10"
        Assert.DoesNotContain("slår i taket", rec.Line);
        Assert.DoesNotContain("slår ofta i taket", rec.Line);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_NeutralMiddle_ShowsNoCard()
    {
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 5 ? 30.0 : 70.0)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.Null(rec);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_UnknownPlanTier_ShowsNoCard()
    {
        // Codex review #2: a 100% backfilled history (no plan tier known anywhere) must never
        // produce a plan-size recommendation, however downsize-shaped the raw numbers look.
        var cycles = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, 30.0, tier: null)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.Null(rec);
    }

    [Fact]
    public void WeeklyBudgetRecommendation_TenWeeksAlone_ShowsTheEarlyHintPrefix()
    {
        // Codex review #5's two-stage rule: N>=10 alone is a HEDGED early hint, not the plain
        // one-year statement.
        var weekly = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => WeeklyCycle(i, i < 8 ? 30.0 : 90.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.NotNull(rec);
        Assert.StartsWith("Tidig indikation", rec!.Line);
        Assert.Contains("mindre plan", rec.Line); // the core evidence sentence is still fully present, just hedged
    }

    [Fact]
    public void WeeklyBudgetRecommendation_AFullYear_NeverShowsTheEarlyHintPrefix()
    {
        var weekly = Enumerable.Range(0, 60).Select(i => WeeklyCycle(i, i < 45 ? 30.0 : 80.0)).ToList();
        var session = Enumerable.Range(0, StatisticsEngine.MinObservations).Select(i => SessionCycle(i, hitCeiling: false)).ToList();
        RecommendationText? rec = StatisticsText.Compose(Loaded(weekly.Concat(session).ToList()), StatisticsPeriod.Year, Tz, Now).WeeklyBudgetRecommendation;
        Assert.NotNull(rec);
        Assert.DoesNotContain("Tidig indikation", rec!.Line);
    }

    // ==================== Recent cycles: incomplete-coverage marker ====================

    [Fact]
    public void RecentCycle_BelowCoverageThreshold_IsMarkedIncomplete_WithItsActualCoveragePct()
    {
        CycleArchiveCsv.Row cycle = SessionCycle(0, hitCeiling: false, coveredFraction: 0.5);
        var recent = new List<StatisticsDataLoader.RecentCycle> { new(cycle, Array.Empty<RecentCycleSparkline.Point>()) };
        StatisticsWindowText text = StatisticsText.Compose(Loaded(new[] { cycle }, recent: recent), StatisticsPeriod.Year, Tz, Now);

        RecentCycleText row = Assert.Single(text.RecentCycles);
        Assert.True(row.Incomplete);
        Assert.NotNull(row.IncompleteLine);
        Assert.Contains("ofullständig", row.IncompleteLine!);
        Assert.Contains("50 %", row.IncompleteLine!);
    }

    [Fact]
    public void RecentCycle_AtOrAboveCoverageThreshold_IsNotMarkedIncomplete()
    {
        CycleArchiveCsv.Row cycle = SessionCycle(0, hitCeiling: false, coveredFraction: 0.95);
        var recent = new List<StatisticsDataLoader.RecentCycle> { new(cycle, Array.Empty<RecentCycleSparkline.Point>()) };
        StatisticsWindowText text = StatisticsText.Compose(Loaded(new[] { cycle }, recent: recent), StatisticsPeriod.Year, Tz, Now);

        RecentCycleText row = Assert.Single(text.RecentCycles);
        Assert.False(row.Incomplete);
        Assert.Null(row.IncompleteLine);
    }

    [Fact]
    public void RecentCycle_HitCeiling_CarriesTheMarkerFlag()
    {
        CycleArchiveCsv.Row cycle = SessionCycle(0, hitCeiling: true);
        var recent = new List<StatisticsDataLoader.RecentCycle> { new(cycle, Array.Empty<RecentCycleSparkline.Point>()) };
        RecentCycleText row = Assert.Single(StatisticsText.Compose(Loaded(new[] { cycle }, recent: recent), StatisticsPeriod.Year, Tz, Now).RecentCycles);
        Assert.True(row.HitCeiling);
    }

    // ==================== Heatmap: coverage gate (#12) and day-of-hit attribution (#13) ====================

    [Fact]
    public void Heatmap_MarksHitCeilingDays_AndLeavesUnobservedDaysAsNoData()
    {
        DateTimeOffset today = Now;
        CycleArchiveCsv.Row hitDay = SessionCycle(0, hitCeiling: true) with { StartedUtc = today.AddDays(-1), ResetUtc = today.AddDays(-1).AddMinutes(QuotaWindows.SessionMinutes) };
        hitDay = hitDay with { CeilingReachedAtUtc = hitDay.ResetUtc.AddMinutes(-30) };
        CycleArchiveCsv.Row normalDay = SessionCycle(1, hitCeiling: false) with { StartedUtc = today.AddDays(-3), ResetUtc = today.AddDays(-3).AddMinutes(QuotaWindows.SessionMinutes) };
        var cycles = new[] { hitDay, normalDay };

        StatisticsWindowText text = StatisticsText.Compose(Loaded(cycles), StatisticsPeriod.TwoWeeks, Tz, Now);

        DateOnly hitLocalDay = DateOnly.FromDateTime(hitDay.StartedUtc.UtcDateTime);
        DateOnly normalLocalDay = DateOnly.FromDateTime(normalDay.StartedUtc.UtcDateTime);
        DateOnly emptyDay = DateOnly.FromDateTime(today.AddDays(-2).UtcDateTime);

        Assert.Equal(HeatmapLevel.HitCeiling, text.Heatmap.Single(d => d.LocalDate == hitLocalDay).Level);
        Assert.Equal(HeatmapLevel.Normal, text.Heatmap.Single(d => d.LocalDate == normalLocalDay).Level);
        Assert.Equal(HeatmapLevel.NoData, text.Heatmap.Single(d => d.LocalDate == emptyDay).Level);
        Assert.Contains("nådde taket", text.Heatmap.Single(d => d.LocalDate == hitLocalDay).Tooltip);
    }

    [Fact]
    public void Heatmap_AppliesTheCoverageGate_AnUnderCoveredHitDayIsIncomplete_NeverHitCeilingOrNormal()
    {
        // Codex review #12's exact input: a session cycle with one observed sample,
        // CoveredMinutes=0, PeakPct=100, HitCeiling=true -- must never paint the same red
        // "nådde taket" cell as a fully-observed day.
        DateTimeOffset today = Now;
        CycleArchiveCsv.Row underCoveredHit = SessionCycle(0, hitCeiling: true, coveredFraction: 0.0) with
        {
            StartedUtc = today.AddDays(-1), ResetUtc = today.AddDays(-1).AddMinutes(QuotaWindows.SessionMinutes),
        };

        StatisticsWindowText text = StatisticsText.Compose(Loaded(new[] { underCoveredHit }), StatisticsPeriod.TwoWeeks, Tz, Now);

        DateOnly day = DateOnly.FromDateTime(underCoveredHit.StartedUtc.UtcDateTime);
        HeatmapDayText cell = text.Heatmap.Single(d => d.LocalDate == day);
        Assert.Equal(HeatmapLevel.Incomplete, cell.Level);
        Assert.NotEqual(HeatmapLevel.HitCeiling, cell.Level);
        Assert.NotEqual(HeatmapLevel.Normal, cell.Level);
        Assert.Contains("okänd", cell.Tooltip);
    }

    [Fact]
    public void Heatmap_AHitCrossingMidnight_IsMarkedOnTheDayTheCeilingWasActuallyReached_NeverTheStartDay()
    {
        // Codex review #13's exact input: a session starts Monday 22:00, reaches 100% Tuesday
        // 02:00, resets Tuesday 03:00 -- the hit must mark TUESDAY, never Monday.
        DateTimeOffset started = new(2026, 9, 14, 22, 0, 0, TimeSpan.Zero); // Monday
        DateTimeOffset ceilingReachedAt = new(2026, 9, 15, 2, 0, 0, TimeSpan.Zero); // Tuesday
        DateTimeOffset reset = new(2026, 9, 15, 3, 0, 0, TimeSpan.Zero); // Tuesday
        CycleArchiveCsv.Row cycle = new(
            "acct", WindowKind.Session, started, reset,
            PeakPct: 100.0, FinalPct: 100.0, HitCeiling: true, BlockedMinutes: (reset - ceilingReachedAt).TotalMinutes,
            CoveredMinutes: QuotaWindows.SessionMinutes, PlanTier: "max", WarnedDryEarly: false,
            PredictedPeakPct: null, PredictedAtUtc: null, CeilingReachedAtUtc: ceilingReachedAt, CeilingReachedCensored: false);

        DateTimeOffset utcNow = reset.AddDays(1);
        StatisticsWindowText text = StatisticsText.Compose(Loaded(new[] { cycle }), StatisticsPeriod.TwoWeeks, Tz, utcNow);

        var monday = new DateOnly(2026, 9, 14);
        var tuesday = new DateOnly(2026, 9, 15);
        Assert.Equal(HeatmapLevel.HitCeiling, text.Heatmap.Single(d => d.LocalDate == tuesday).Level);
        Assert.NotEqual(HeatmapLevel.HitCeiling, text.Heatmap.Single(d => d.LocalDate == monday).Level);
        // Monday still shows genuine (non-hit) activity -- the session DID start there.
        Assert.Equal(HeatmapLevel.Normal, text.Heatmap.Single(d => d.LocalDate == monday).Level);
    }

    // ==================== Proactive advice (docs/statistics.md decision 4) ====================

    [Fact]
    public void Advice_FiresOnceForANewSignature_NotAgainUntilItChanges()
    {
        var ceiling = new StatisticsEngine.CeilingRecommendation(true, 10, 10, TimesHit: 3, WeeksSpanned: 4);
        var weekly = new StatisticsEngine.WeeklyBudgetRecommendation(false, 3, 10, 50.0, 0, 0);

        (StatisticsAdvice.Advice? first, StatisticsAdvice.LastShown state1) = StatisticsAdvice.Decide(ceiling, weekly, StatisticsAdvice.LastShown.Empty);
        Assert.NotNull(first);
        Assert.Contains("3", first!.Value.Line);

        (StatisticsAdvice.Advice? second, StatisticsAdvice.LastShown state2) = StatisticsAdvice.Decide(ceiling, weekly, state1);
        Assert.Null(second);
        Assert.Equal(state1, state2);

        var changedCeiling = ceiling with { TimesHit = 4 };
        (StatisticsAdvice.Advice? third, StatisticsAdvice.LastShown state3) = StatisticsAdvice.Decide(changedCeiling, weekly, state2);
        Assert.NotNull(third);
        Assert.Contains("4", third!.Value.Line);
        Assert.NotEqual(state2, state3);
    }

    [Fact]
    public void Advice_NeitherRecommendationReady_ProducesNoLine()
    {
        var ceiling = new StatisticsEngine.CeilingRecommendation(false, 2, 10, 0, 0);
        var weekly = new StatisticsEngine.WeeklyBudgetRecommendation(false, 2, 10, 50.0, 0, 0);
        (StatisticsAdvice.Advice? advice, StatisticsAdvice.LastShown next) = StatisticsAdvice.Decide(ceiling, weekly, StatisticsAdvice.LastShown.Empty);
        Assert.Null(advice);
        Assert.Equal(StatisticsAdvice.LastShown.Empty, next);
    }

    [Fact]
    public void Advice_WeeklyBudgetChangeFiresIndependently_OfCeilingRecommendationsOwnState()
    {
        var ceiling = new StatisticsEngine.CeilingRecommendation(true, 10, 10, 2, 4);
        var weeklyA = new StatisticsEngine.WeeklyBudgetRecommendation(true, 10, 10, 50.0, 5, 10, Direction: StatisticsEngine.WeeklyBudgetDirection.Downsize);

        (StatisticsAdvice.Advice? first, StatisticsAdvice.LastShown state1) = StatisticsAdvice.Decide(ceiling, weeklyA, StatisticsAdvice.LastShown.Empty);
        Assert.NotNull(first); // ceiling fires first (unchanged tie-break rule)

        var weeklyB = weeklyA with { WeeksAtOrBelow = 6 };
        (StatisticsAdvice.Advice? second, StatisticsAdvice.LastShown state2) = StatisticsAdvice.Decide(ceiling, weeklyB, state1);
        Assert.NotNull(second);
        Assert.Contains("6 av", second!.Value.Line);
        Assert.Equal(state1.CeilingSignature, state2.CeilingSignature);
    }

    // ==================== "no raw minute counts above 60 anywhere" -- swept across a full year ====================

    [Fact]
    public void ComposedText_AcrossAFullYearOfRealisticData_NeverShowsARawMinuteCount()
    {
        TimeZoneInfo tz = TimeZoneInfo.Utc;
        StatisticsDemoData.DemoAccountData demo = StatisticsDemoData.BuildFullYear(Now, tz);

        DateTimeOffset recentCutoff = Now.AddDays(-StatisticsEngine.RecentCyclesDays);
        List<RecentCycle> recent = demo.Cycles
            .Where(c => c.AccountKey == StatisticsDemoData.AccountKey && c.StartedUtc >= recentCutoff)
            .OrderByDescending(c => c.ResetUtc)
            .Select(c => new RecentCycle(c, Array.Empty<RecentCycleSparkline.Point>()))
            .ToList();

        var data = new StatisticsDataLoader.LoadedData(
            StatisticsEngine.Compute(demo.Cycles, demo.Hours, StatisticsDemoData.AccountKey, tz),
            demo.Cycles, recent);

        foreach (StatisticsPeriod period in new[] { StatisticsPeriod.TwoWeeks, StatisticsPeriod.ThreeMonths, StatisticsPeriod.Year })
            AssertNoRawMinutes(StatisticsText.Compose(data, period, tz, Now));
    }
}
