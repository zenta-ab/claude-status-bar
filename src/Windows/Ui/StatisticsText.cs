using System.Linq;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>One named tile (docs/statistics.md decision 4): Title is fixed; Ready mirrors the engine tile's own N-gate. ValueLine is the honest metric when Ready, or "Samlar data ..." when not -- never a number that looks like the real statistic while unready. NLine always shows the count behind it, in both states ("EVERY tile shows its N").</summary>
public sealed record TileText(string Title, bool Ready, string ValueLine, string NLine);

/// <summary>One of the two pre-specified recommendations (docs/statistics.md decision 3), only ever produced when Ready -- ComposeRecommendations returns null for one that hasn't cleared its threshold ("only when unlocked").</summary>
public sealed record RecommendationText(string Line, string NLine);

/// <summary>One row of the "recent cycles" list (docs/statistics.md decision 4): local date/time range, session/weekly, peak %, whether it hit the ceiling, and an honest incomplete-coverage marker. The sparkline itself is numeric, not text -- Ui/StatisticsForm.cs pairs this record (by index) with the matching StatisticsDataLoader.RecentCycle for its points.</summary>
public sealed record RecentCycleText(string RangeLine, string KindLabel, string PeakLine, bool HitCeiling, bool Incomplete, string? IncompleteLine);

/// <summary>Codex review #12: Incomplete is its own neutral state -- a day that had session activity but never cleared the coverage gate (so its own claim would be no more defensible than a NoData day) is shown as "okänd", never silently folded into Normal or NoData.</summary>
public enum HeatmapLevel { NoData, Incomplete, Normal, HitCeiling }

/// <summary>One local calendar day of the GitHub-style heatmap (docs/statistics.md decision 4), with its hover tooltip text pre-composed.</summary>
public sealed record HeatmapDayText(DateOnly LocalDate, HeatmapLevel Level, string Tooltip);

public sealed record StatisticsWindowText(
    IReadOnlyList<TileText> Tiles,
    RecommendationText? CeilingRecommendation,
    RecommendationText? WeeklyBudgetRecommendation,
    IReadOnlyList<RecentCycleText> RecentCycles,
    IReadOnlyList<HeatmapDayText> Heatmap);

/// <summary>
/// The statistics window's text layer (docs/statistics.md decision 4, the task's item 1): a pure
/// function of already-loaded data (Data/StatisticsDataLoader.LoadedData) plus the chosen period
/// and timezone -> every string the window shows, no drawing, no file IO -- mirrors
/// Ui/PanelText.Compose's split from Ui/PanelForm.cs. Every number goes through the same plain
/// "F0 %"/InvariantCulture-flavoured formatting the rest of this app already uses (this csproj
/// sets InvariantGlobalization, so a real sv-SE CultureInfo cannot be constructed at all -- the
/// existing TimeText/PanelText convention of Swedish words with period-decimal numbers already
/// IS this app's "sv-SE" formatting).
/// </summary>
public static class StatisticsText
{
    public static StatisticsWindowText Compose(StatisticsDataLoader.LoadedData data, StatisticsPeriod period, TimeZoneInfo tz, DateTimeOffset utcNow)
    {
        var tiles = new List<TileText>
        {
            ComposePeakWeekday(data.Engine.PeakWeekday),
            ComposePeakHour(data.Engine.PeakHour),
            ComposeTimesAtCeiling(data.Engine.TimesAtCeiling),
            ComposeWeeklyBudgetShare(data.Engine.WeeklyBudgetShare),
            ComposeForecastPrecision(data.Engine.ForecastPrecision),
            ComposeForecastRecall(data.Engine.ForecastRecall),
        };

        return new StatisticsWindowText(
            tiles,
            ComposeCeilingRecommendation(data.Engine.CeilingRecommendation),
            ComposeWeeklyBudgetRecommendation(data.Engine.WeeklyBudgetRecommendation),
            data.RecentCycles.Select(c => ComposeRecentCycle(c.Row, tz)).ToList(),
            ComposeHeatmap(data.PeriodCycles, period, tz, utcNow));
    }

    static string LearningLine(int n, int threshold) => $"Samlar data — {n} av {threshold}";

    // ---- tiles ----

    /// <summary>
    /// 2026-09-21 follow-up to Codex review #11: below the new 5-of-7-weekdays threshold, no
    /// candidate weekday is named at all any more -- the NLine (every tile's own "EVERY tile
    /// shows its N" contract) instead states the honest progress toward the new qualifying-bucket
    /// threshold ("Samlar data — 3 av 5 veckodagar med tillräcklig data"), since naming a leading
    /// candidate before a real comparison exists is exactly the overstatement this rule closes.
    /// Once Ready, the claim adds a scope clause ("tisdag — av de 5 veckodagar med data") whenever
    /// QualifyingBuckets is below PeakWeekdayTotalBuckets (7) -- omitted only when every weekday
    /// qualified.
    /// </summary>
    static TileText ComposePeakWeekday(StatisticsEngine.PeakWeekdayTile t)
    {
        const string title = "Mest aktiva veckodagen";
        if (!t.Ready)
            return new TileText(title, false, "Samlar data om vilken veckodag du använder mest", QualifyingProgressLine(t.QualifyingBuckets, StatisticsEngine.PeakWeekdayMinQualifyingBuckets, "veckodagar"));

        string weekday = Capitalize(SwedishText.WeekdayFull[(int)t.Weekday!.Value]);
        string scope = t.QualifyingBuckets < StatisticsEngine.PeakWeekdayTotalBuckets
            ? $" — av de {t.QualifyingBuckets} veckodagar med data" : "";
        return new TileText(title, true, $"{weekday}{scope}", $"baserat på {t.N} {SwedishText.WeekdayPluralLower(t.Weekday.Value)}");
    }

    /// <summary>Same fix as ComposePeakWeekday, out of PeakHourMinQualifyingBuckets (12) of PeakHourTotalBuckets (24) hours -- see that method's doc comment.</summary>
    static TileText ComposePeakHour(StatisticsEngine.PeakHourTile t)
    {
        const string title = "Mest aktiva timmen";
        if (!t.Ready)
            return new TileText(title, false, "Samlar data om vilken timme du använder mest", QualifyingProgressLine(t.QualifyingBuckets, StatisticsEngine.PeakHourMinQualifyingBuckets, "timmar"));

        int hour = t.Hour!.Value;
        string hourRange = $"kl {hour:00}–{(hour + 1) % 24:00}";
        string scope = t.QualifyingBuckets < StatisticsEngine.PeakHourTotalBuckets
            ? $" — av de {t.QualifyingBuckets} timmar datorn var igång" : "";
        return new TileText(title, true, $"{hourRange}{scope}", $"baserat på {t.N} dagar");
    }

    /// <summary>The shared learning-state NLine for both peak tiles: "Samlar data — X av N {unit} med tillräcklig data" -- honest progress toward the new qualifying-bucket threshold, never a leading candidate's name (see ComposePeakWeekday's doc comment for why).</summary>
    static string QualifyingProgressLine(int qualifying, int minQualifying, string unit) =>
        $"{LearningLine(qualifying, minQualifying)} {unit} med tillräcklig data";

    static TileText ComposeTimesAtCeiling(StatisticsEngine.TimesAtCeilingTile t)
    {
        const string title = "Nådde taket";
        if (!t.Ready) return new TileText(title, false, "Samlar data om hur ofta du når taket", $"{LearningLine(t.N, t.Threshold)} sessioner");
        return new TileText(title, true, $"{t.Count} {(t.Count == 1 ? "gång" : "gånger")}", $"baserat på {t.N} sessioner");
    }

    static TileText ComposeWeeklyBudgetShare(StatisticsEngine.WeeklyBudgetShareTile t)
    {
        const string title = "Veckobudget använd";
        if (!t.Ready) return new TileText(title, false, "Samlar data om veckobudgeten", $"{LearningLine(t.N, t.Threshold)} veckor");
        return new TileText(title, true, $"{t.AveragePeakPct!.Value:F0} % i snitt", $"baserat på {t.N} veckor");
    }

    /// <summary>Codex review #7: "how many warnings were followed by a ceiling hit" -- precision, never labelled just "accuracy" (which used to hide recall entirely, finding #7's "100% rätt, baserat på 10 varningar" while 90 unwarned hits went unmentioned).</summary>
    static TileText ComposeForecastPrecision(StatisticsEngine.ForecastPrecisionTile t)
    {
        const string title = "Varningar som stämde";
        if (!t.Ready) return new TileText(title, false, "Samlar data om varningarnas träffsäkerhet", $"{LearningLine(t.N, t.Threshold)} varningar");
        return new TileText(title, true, $"{t.Precision!.Value * 100:F0} % följdes av takträff", $"baserat på {t.N} varningar");
    }

    /// <summary>Codex review #7: the other half of the pair -- "how many ceiling hits were warned in advance" (recall), over forecast-eligible cycles only (Codex review #8).</summary>
    static TileText ComposeForecastRecall(StatisticsEngine.ForecastRecallTile t)
    {
        const string title = "Takträffar som varnades";
        if (!t.Ready) return new TileText(title, false, "Samlar data om hur många takträffar som varnades", $"{LearningLine(t.N, t.Threshold)} takträffar");
        return new TileText(title, true, $"{t.Recall!.Value * 100:F0} % varnades i förväg", $"baserat på {t.N} takträffar");
    }

    // ---- recommendations (only when unlocked -- Ready) ----

    /// <summary>
    /// A 0-times ceiling recommendation is not advice -- the "Nådde taket" tile already says 0 --
    /// so it is hidden entirely here, never shown as a green "0 gånger" card. Also fixes the "de
    /// senaste 1 veckorna" defect (WeeksSpanned can genuinely be 1) via SwedishText.
    /// RecentWeeksPhrase/Weeks. Codex review #6: the joint weekday x hour "most often" clause is
    /// gone -- StatisticsEngine.CeilingRecommendation no longer carries it at all.
    /// </summary>
    static RecommendationText? ComposeCeilingRecommendation(StatisticsEngine.CeilingRecommendation r)
    {
        if (!r.Ready || r.TimesHit == 0) return null;
        string line = $"Du nådde taket {r.TimesHit} {SwedishText.Times(r.TimesHit)} {SwedishText.RecentWeeksPhrase(r.WeeksSpanned)}.";
        return new RecommendationText(line, $"baserat på {r.N} sessioner över {r.WeeksSpanned} {SwedishText.Weeks(r.WeeksSpanned)}");
    }

    /// <summary>
    /// Codex review #1/#2/#5/#9: Downsize and Upsize each state their OWN evidence number
    /// (never "0 av 10 veckor" for an upsize case, finding #9) with a conditionally-worded
    /// conclusion, or are hidden entirely (Direction.None) rather than shown as a bare,
    /// conclusion-less statistic. The NLine names every constraint actually evaluated for
    /// Downsize specifically -- both the weekly-budget pool AND the session-ceiling guard
    /// (Codex review #1's "the card states which constraints were evaluated") -- and an early-
    /// hint prefix (Codex review #5) marks the two-stage rule whenever the same-plan pool has
    /// not yet reached a full year of history.
    /// </summary>
    static RecommendationText? ComposeWeeklyBudgetRecommendation(StatisticsEngine.WeeklyBudgetRecommendation r)
    {
        if (!r.Ready || r.Direction == StatisticsEngine.WeeklyBudgetDirection.None) return null;

        string prefix = r.IsEarlyHint ? SwedishText.EarlyHintPrefix(r.WeeksConsidered) : "";
        string nLine;
        string line;
        if (r.Direction == StatisticsEngine.WeeklyBudgetDirection.Downsize)
        {
            line = $"Du använde högst {r.ThresholdPct:F0} % av veckobudgeten {r.WeeksAtOrBelow} av de senaste {r.WeeksConsidered} veckorna.{SwedishText.WeeklyBudgetConclusion(r.Direction)}";
            nLine = $"baserat på {r.WeeksConsidered} {SwedishText.Weeks(r.WeeksConsidered)} och {r.SessionEvidenceCount} {(r.SessionEvidenceCount == 1 ? "session" : "sessioner")} (femtimmarstaket kontrollerat)";
        }
        else
        {
            line = $"Du nådde minst {StatisticsEngine.WeeklyBudgetNearCeilingPct:F0} % av veckobudgeten {r.WeeksNearCeiling} av de senaste {r.WeeksConsidered} veckorna — nära taket.{SwedishText.WeeklyBudgetConclusion(r.Direction)}";
            nLine = $"baserat på {r.WeeksConsidered} {SwedishText.Weeks(r.WeeksConsidered)}";
        }
        return new RecommendationText($"{prefix}{line}", nLine);
    }

    // ---- recent cycles ----

    static RecentCycleText ComposeRecentCycle(CycleArchiveCsv.Row c, TimeZoneInfo tz)
    {
        string kindLabel = c.Window == WindowKind.Session ? "Session" : "Vecka";
        DateTimeOffset startedLocal = TimeZoneInfo.ConvertTime(c.StartedUtc, tz);
        string dateLabel = $"{TimeText.Weekday(c.StartedUtc, tz)} {startedLocal.Day} {SwedishText.MonthAbbrev[startedLocal.Month]}";

        // A weekly cycle spans 7 days -- "same-day HH:mm-HH:mm" would show a misleading
        // "00:00-00:00"-shaped range (the clock time is identical, only the DATE differs), so
        // it gets its own date-to-date range instead of a session's same-day clock range.
        string range;
        if (c.Window == WindowKind.Session)
        {
            range = $"{dateLabel}, {TimeText.ClockOnly(c.StartedUtc, tz)}–{TimeText.ClockOnly(c.ResetUtc, tz)}";
        }
        else
        {
            DateTimeOffset resetLocal = TimeZoneInfo.ConvertTime(c.ResetUtc, tz);
            string endLabel = $"{TimeText.Weekday(c.ResetUtc, tz)} {resetLocal.Day} {SwedishText.MonthAbbrev[resetLocal.Month]}";
            range = $"{dateLabel} – {endLabel}";
        }

        double windowMinutes = c.Window == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes;
        double coverageFraction = windowMinutes > 0 ? c.CoveredMinutes / windowMinutes : 0.0;
        bool incomplete = coverageFraction < StatisticsEngine.CoverageThreshold;
        string? incompleteLine = incomplete
            ? $"ofullständig — appen körde {Math.Clamp(coverageFraction, 0.0, 1.0) * 100:F0} % av tiden"
            : null;

        string peakLine = $"Topp {c.PeakPct:F0} %";
        return new RecentCycleText(range, kindLabel, peakLine, c.HitCeiling, incomplete, incompleteLine);
    }

    // ---- heatmap ----

    /// <summary>
    /// Codex review #12/#13. Two independent things are bucketed by DIFFERENT days on purpose:
    ///  - "did the app have session activity this day, and was it well enough covered to say
    ///    anything at all" -- grouped by StartedUtc's local date, same as before.
    ///  - "was the ceiling actually reached this day" -- grouped by the moment the ceiling was
    ///    reached (StatisticsEngine's CeilingReachedAt: reset_utc - blocked_minutes, or the
    ///    stored/possibly-censored column once #14 lands), NOT the cycle's start day -- a session
    ///    that starts Monday 22:00 and reaches the ceiling Tuesday 02:00 must mark TUESDAY, never
    ///    Monday (finding #13's exact example).
    /// Only COVERED cycles (StatisticsEngine.IsCycleCovered) count for either grouping -- Codex
    /// review #12: the heatmap used to ignore the coverage gate entirely, so a single-sample,
    /// zero-coverage cycle that happened to read 100% could paint a red "hit the ceiling" day
    /// indistinguishable from a fully-observed one. A day with ONLY under-covered cycles gets its
    /// own neutral Incomplete ("okänd") cell -- never silently folded into Normal (which would
    /// claim more confidence than the data supports) or NoData (which would erase that something
    /// was going on that day at all).
    /// </summary>
    static IReadOnlyList<HeatmapDayText> ComposeHeatmap(IReadOnlyList<CycleArchiveCsv.Row> periodCycles, StatisticsPeriod period, TimeZoneInfo tz, DateTimeOffset utcNow)
    {
        // Session cycles only -- a weekly cycle spans 7 days and has no single "day" of its own
        // to attribute a ceiling hit to; the heatmap tracks daily session activity, same scope as
        // the peak-weekday/peak-hour tiles.
        List<CycleArchiveCsv.Row> sessionCycles = periodCycles.Where(c => c.Window == WindowKind.Session).ToList();

        Dictionary<DateOnly, List<CycleArchiveCsv.Row>> byStartDay = sessionCycles
            .GroupBy(c => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c.StartedUtc, tz).Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        HashSet<DateOnly> hitDays = sessionCycles
            .Where(c => c.HitCeiling && StatisticsEngine.IsCycleCovered(c))
            .Select(c => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(CeilingReachedAtLocal(c), tz).Date))
            .ToHashSet();

        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, tz).Date);
        DateOnly start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow - StatisticsEngine.PeriodSpan(period), tz).Date);

        var days = new List<HeatmapDayText>();
        for (DateOnly d = start; d <= today; d = d.AddDays(1))
        {
            string dateLabel = $"{d.Day} {SwedishText.MonthAbbrev[d.Month]} {d.Year}";
            byStartDay.TryGetValue(d, out List<CycleArchiveCsv.Row>? cyclesThatDay);
            List<CycleArchiveCsv.Row> covered = cyclesThatDay?.Where(StatisticsEngine.IsCycleCovered).ToList() ?? new List<CycleArchiveCsv.Row>();

            if (hitDays.Contains(d))
            {
                double peak = covered.Count > 0 ? covered.Max(c => c.PeakPct) : 100.0;
                days.Add(new HeatmapDayText(d, HeatmapLevel.HitCeiling, $"{dateLabel} — nådde taket, topp {peak:F0} %"));
            }
            else if (covered.Count > 0)
            {
                double peak = covered.Max(c => c.PeakPct);
                days.Add(new HeatmapDayText(d, HeatmapLevel.Normal, $"{dateLabel} — topp {peak:F0} %"));
            }
            else if (cyclesThatDay is { Count: > 0 })
            {
                days.Add(new HeatmapDayText(d, HeatmapLevel.Incomplete, $"{dateLabel} — okänd (appen körde för lite av tiden)"));
            }
            else
            {
                days.Add(new HeatmapDayText(d, HeatmapLevel.NoData, $"{dateLabel} — ingen data"));
            }
        }
        return days;
    }

    /// <summary>Mirrors StatisticsEngine's private CeilingReachedAt (reset_utc - blocked_minutes, or the stored column once available) -- day-level attribution only, so interval-censoring (Codex review #14, which excludes a censored crossing from HOUR-of-hit patterns) does not need to matter here: a gap wide enough to make even the DAY doubtful would already have failed the coverage gate above.</summary>
    static DateTimeOffset CeilingReachedAtLocal(CycleArchiveCsv.Row c) => c.CeilingReachedAtUtc ?? c.ResetUtc.AddMinutes(-c.BlockedMinutes);

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
