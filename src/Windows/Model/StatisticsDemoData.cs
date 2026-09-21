using ClaudeStatusBar.Data;

namespace ClaudeStatusBar.Model;

/// <summary>
/// docs/statistics.md, the task's "Demo / capture" item: synthetic cycles.csv/hourly-*.csv rows
/// for the statistics window's demo captures, built directly as CycleArchiveCsv.Row/
/// HourlyRollupCsv.Row and run through the REAL StatisticsEngine -- never a hand-asserted UI
/// state, same discipline Model/DemoQuotaSource.cs already applies to the panel's demo states.
///
/// Every timestamp is built from a LOCAL wall-clock instant (in the given tz) converted to UTC,
/// exactly like the real app's own data -- never asserted directly in UTC -- so a capture is
/// correct regardless of which machine/timezone renders it.
///
/// Two scenarios (the task's two required captures):
///   - FreshInstall: ~2 weeks of deliberately thin data. Every tile's own qualifying pool stays
///     below StatisticsEngine.MinObservations by construction (8 session cycles, 8 session
///     hourly rows, 1 weekly cycle, 0 warned cycles), so the window's "still learning" state is
///     what a brand-new install actually looks like, not a hand-picked appearance.
///   - FullYear: 365 days of a plausible pattern -- heavier Tuesday afternoons (so peak
///     weekday/hour has a clear, deterministic winner), the ceiling reached almost every Friday
///     evening (a clean "most often" cluster for the ceiling recommendation), and a mix of weeks
///     above/below half the weekly budget (a real weekly-budget recommendation either way).
///     Every tile and both recommendations clear their threshold.
/// </summary>
public static class StatisticsDemoData
{
    public const string AccountKey = "demo-account";

    public readonly record struct DemoAccountData(IReadOnlyList<CycleArchiveCsv.Row> Cycles, IReadOnlyList<HourlyRollupCsv.Row> Hours);

    public static DemoAccountData BuildFreshInstall(DateTimeOffset utcNow, TimeZoneInfo tz)
    {
        var cycles = new List<CycleArchiveCsv.Row>();
        var hours = new List<HourlyRollupCsv.Row>();

        // 8 session cycles over the last ~13 days (a few gap days, like a real light user),
        // each contributing exactly one hourly row -- 8 < StatisticsEngine.MinObservations (10)
        // for both the session-cycle pool and the session-hourly pool.
        int[] daysAgo = { 13, 11, 9, 7, 6, 4, 2, 1 };
        for (int i = 0; i < daysAgo.Length; i++)
        {
            DateTime hourStartLocal = LocalDate(utcNow, tz, -daysAgo[i]).AddHours(20); // an evening session
            (CycleArchiveCsv.Row cycle, HourlyRollupCsv.Row hour) = BuildSessionCycleWithOneHour(
                tz, hourStartLocal, usedPct: 25.0 + 3.0 * i, hitCeiling: false, warnedDryEarly: false);
            cycles.Add(cycle);
            hours.Add(hour);
        }

        // One closed weekly cycle, started 13 days ago (inside the default "2 veckor" period
        // preset's own cutoff, so it actually shows up in the recent-cycles list and the
        // weekly-budget tile's N=1 by default) -- well under MinObservations on its own.
        DateTime weeklyStartLocal = LocalDate(utcNow, tz, -13);
        cycles.Add(BuildWeeklyCycle(tz, weeklyStartLocal, peakPct: 22.0));

        return new DemoAccountData(cycles, hours);
    }

    public static DemoAccountData BuildFullYear(DateTimeOffset utcNow, TimeZoneInfo tz)
    {
        var cycles = new List<CycleArchiveCsv.Row>();
        var hours = new List<HourlyRollupCsv.Row>();

        DateTime startLocalDate = LocalDate(utcNow, tz, -365);
        for (int i = 0; i < 365; i++)
        {
            DateTime day = startLocalDate.AddDays(i);
            DayOfWeek dow = day.DayOfWeek;
            bool isFriday = dow == DayOfWeek.Friday;

            // 2026-09-21 follow-up to Codex review #11: a peak-hour claim now needs
            // StatisticsEngine.PeakHourMinQualifyingBuckets (12) distinct qualifying hours, not
            // just "the winner plus one competitor" -- the other five weekdays (Mon/Wed/Thu/Sat/
            // Sun) now rotate through TEN distinct hours instead of four, so together with
            // Tuesday's 14 and Friday's 18 the demo clears exactly 12 qualifying hours, each with
            // comfortably more than MinObservations distinct days (~365 days / 10 hours ~= 26
            // each), while Tuesday's 45% average still wins outright against every other hour's
            // low-to-mid-teens average (Friday's 25% included).
            (int hour, double pct) = dow switch
            {
                DayOfWeek.Tuesday => (14, 45.0),
                DayOfWeek.Friday => (18, 25.0),
                _ => (new[] { 0, 3, 6, 8, 9, 11, 16, 19, 21, 23 }[i % 10], 12.0 + 3.0 * (i % 5)),
            };

            DateTime hourStartLocal = day.AddHours(hour);
            (CycleArchiveCsv.Row cycle, HourlyRollupCsv.Row hourRow) = BuildSessionCycleWithOneHour(
                tz, hourStartLocal, usedPct: pct, hitCeiling: isFriday,
                warnedDryEarly: isFriday || (!isFriday && i % 11 == 0));
            cycles.Add(cycle);
            hours.Add(hourRow);
        }

        for (int w = 0; w < 52; w++)
        {
            DateTime weekStartLocal = startLocalDate.AddDays(7 * w);
            bool lowWeek = w % 3 != 0;
            cycles.Add(BuildWeeklyCycle(tz, weekStartLocal, peakPct: lowWeek ? 35.0 : 68.0));
        }

        return new DemoAccountData(cycles, hours);
    }

    /// <summary>The local calendar date `offsetDays` from utcNow's own local date, at midnight local -- a plain building block, not itself a real instant (callers add an hour before converting to UTC).</summary>
    static DateTime LocalDate(DateTimeOffset utcNow, TimeZoneInfo tz, int offsetDays) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(utcNow, tz).Date.AddDays(offsetDays), DateTimeKind.Unspecified);

    static DateTimeOffset ToUtc(DateTime localWallClock, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified), tz);

    /// <summary>
    /// One closed session cycle whose active hour is exactly `hourStartLocal`, plus the matching
    /// single hourly-rollup row -- the session is modelled as starting 1h before that hour and
    /// running the full 5h session window from there, so the target hour always falls inside it.
    /// </summary>
    static (CycleArchiveCsv.Row Cycle, HourlyRollupCsv.Row Hour) BuildSessionCycleWithOneHour(
        TimeZoneInfo tz, DateTime hourStartLocal, double usedPct, bool hitCeiling, bool warnedDryEarly)
    {
        DateTime sessionStartLocal = hourStartLocal.AddHours(-1);
        DateTime sessionResetLocal = sessionStartLocal.AddMinutes(QuotaWindows.SessionMinutes);
        DateTimeOffset startedUtc = ToUtc(sessionStartLocal, tz);
        DateTimeOffset resetUtc = ToUtc(sessionResetLocal, tz);
        DateTimeOffset hourStartUtc = ToUtc(hourStartLocal, tz);

        double peakPct = hitCeiling ? 100.0 : usedPct;
        double blockedMinutes = hitCeiling ? (sessionResetLocal - hourStartLocal).TotalMinutes : 0.0;

        var cycle = new CycleArchiveCsv.Row(
            AccountKey, WindowKind.Session, startedUtc, resetUtc,
            PeakPct: peakPct, FinalPct: peakPct * 0.95, HitCeiling: hitCeiling,
            BlockedMinutes: blockedMinutes, CoveredMinutes: QuotaWindows.SessionMinutes * 0.9,
            PlanTier: "max", WarnedDryEarly: warnedDryEarly,
            PredictedPeakPct: warnedDryEarly ? Math.Min(100.0, peakPct + 5.0) : null,
            PredictedAtUtc: warnedDryEarly ? startedUtc.AddMinutes(QuotaWindows.SessionMinutes / 2.0) : null);

        var hour = new HourlyRollupCsv.Row(AccountKey, WindowKind.Session, hourStartUtc, usedPct, Samples: 3, CoveredMinutes: 55.0);
        return (cycle, hour);
    }

    static CycleArchiveCsv.Row BuildWeeklyCycle(TimeZoneInfo tz, DateTime weekStartLocal, double peakPct)
    {
        DateTime weekResetLocal = weekStartLocal.AddMinutes(QuotaWindows.WeeklyMinutes);
        DateTimeOffset startedUtc = ToUtc(weekStartLocal, tz);
        DateTimeOffset resetUtc = ToUtc(weekResetLocal, tz);
        return new CycleArchiveCsv.Row(
            AccountKey, WindowKind.Weekly, startedUtc, resetUtc,
            PeakPct: peakPct, FinalPct: peakPct, HitCeiling: false,
            BlockedMinutes: 0.0, CoveredMinutes: QuotaWindows.WeeklyMinutes * 0.94,
            PlanTier: "max", WarnedDryEarly: false, PredictedPeakPct: null, PredictedAtUtc: null);
    }
}
