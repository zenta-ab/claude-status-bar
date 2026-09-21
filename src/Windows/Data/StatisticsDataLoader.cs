using System.Linq;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The IO shell around StatisticsEngine for the statistics window (docs/statistics.md, decision
/// 4): reads one account's cycles.csv/hourly-*.csv, applies the chosen period preset's cutoff,
/// runs the pure engine (local-time bucketed -- see StatisticsEngine's class doc comment), and
/// separately prepares the "recent cycles" list (always the last ~2 weeks, independent of the
/// period preset) with each cycle's sparkline pre-loaded. Ui/StatisticsText.Compose stays a pure
/// function of this type's output -- no file IO of its own.
/// </summary>
public static class StatisticsDataLoader
{
    public readonly record struct RecentCycle(CycleArchiveCsv.Row Row, IReadOnlyList<RecentCycleSparkline.Point> Sparkline);

    public sealed record LoadedData(
        StatisticsEngine.Result Engine,
        IReadOnlyList<CycleArchiveCsv.Row> PeriodCycles,
        IReadOnlyList<RecentCycle> RecentCycles);

    /// <summary>The cutoff instant (StartedUtc/HourStartUtc &gt;= cutoff qualifies) for a period preset, as of utcNow.</summary>
    public static DateTimeOffset PeriodCutoff(StatisticsPeriod period, DateTimeOffset utcNow) => utcNow - StatisticsEngine.PeriodSpan(period);

    public static LoadedData Load(string accountLogDir, string accountKey, StatisticsPeriod period, TimeZoneInfo tz, DateTimeOffset utcNow)
    {
        IReadOnlyList<CycleArchiveCsv.Row> allCycles = CycleArchiveCsv.ReadAll(Path.Combine(accountLogDir, CycleArchiveCsv.FileName));
        var allHours = new List<HourlyRollupCsv.Row>();
        foreach (string yearFile in HourlyRollupCsv.FindYearFiles(accountLogDir))
            allHours.AddRange(HourlyRollupCsv.ReadAll(yearFile));

        // Codex review #18: a cycle qualifies for a period preset by when it CLOSED
        // (reset_utc > cutoff), not by when it started -- a weekly cycle that started five days
        // before the cutoff and closed two days after it is a completed week that genuinely
        // belongs to "the last N days", and excluding it entirely (the old StartedUtc >= cutoff
        // filter) silently dropped most of a real, recently-closed cycle.
        DateTimeOffset cutoff = PeriodCutoff(period, utcNow);
        List<CycleArchiveCsv.Row> periodCycles = allCycles
            .Where(c => c.AccountKey == accountKey && c.ResetUtc > cutoff).ToList();
        List<HourlyRollupCsv.Row> periodHours = allHours
            .Where(h => h.AccountKey == accountKey && h.HourStartUtc >= cutoff).ToList();

        StatisticsEngine.Result engineResult = StatisticsEngine.Compute(periodCycles, periodHours, accountKey, tz);

        DateTimeOffset recentCutoff = utcNow.AddDays(-StatisticsEngine.RecentCyclesDays);
        List<RecentCycle> recent = allCycles
            .Where(c => c.AccountKey == accountKey && c.StartedUtc >= recentCutoff)
            .OrderByDescending(c => c.ResetUtc)
            .Select(c => new RecentCycle(c, RecentCycleSparkline.Load(
                accountLogDir, c.Window, c.StartedUtc, c.ResetUtc, (c.ResetUtc - c.StartedUtc).TotalMinutes)))
            .ToList();

        return new LoadedData(engineResult, periodCycles, recent);
    }

    /// <summary>
    /// Task item 4's fix: the statistics window's account selector must show the exact same
    /// label the tray/panel already show for a configured account -- never resolve one
    /// independently. liveLabel is that label (AccountRuntime.Label, already whole-set
    /// disambiguated when this account collides with another one -- see
    /// StatusBarApplicationContext.RefreshDisambiguatedLabels); whenever input.SubscriptionType
    /// is already known (a live poll has landed), liveLabel is returned completely unchanged, so
    /// disambiguation is never lost by recomputing from input alone.
    ///
    /// The one case this DOES resolve independently is an account this call has no live plan
    /// tier for yet (input.SubscriptionType null -- most commonly a `--capture-statistics` run,
    /// which deliberately never pumps a message loop before building the statistics window, so a
    /// just-started account's async first poll has had no chance to land; see
    /// StatusBarApplicationContext.RunStatisticsCapture). There, instead of letting liveLabel
    /// stand as whatever AccountLabel.Resolve's chain fell through to without a plan tier (the
    /// bare email local part the task's real screenshot caught), this re-resolves using whatever
    /// plan tier this account's own archive already recorded on disk -- cycles.csv's plan_tier
    /// column, from any PAST live poll -- "AccountLabel with the data available on disk" for an
    /// account this call otherwise has no live confirmation for. Not whole-set disambiguated
    /// (no other account's data is in scope here); still strictly better than the email fallback.
    /// </summary>
    public static string ResolveAccountLabel(string liveLabel, AccountLabelInput input, string accountLogDir, int accountIndex)
    {
        if (input.SubscriptionType != null) return liveLabel;

        string? diskPlanTier = LastKnownPlanTier(accountLogDir);
        return diskPlanTier is null ? liveLabel : AccountLabel.Resolve(input with { SubscriptionType = diskPlanTier }, accountIndex);
    }

    static string? LastKnownPlanTier(string accountLogDir)
    {
        IReadOnlyList<CycleArchiveCsv.Row> cycles = CycleArchiveCsv.ReadAll(Path.Combine(accountLogDir, CycleArchiveCsv.FileName));
        for (int i = cycles.Count - 1; i >= 0; i--)
            if (cycles[i].PlanTier != null) return cycles[i].PlanTier;
        return null;
    }
}
