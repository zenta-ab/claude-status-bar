using System.Linq;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// docs/statistics.md, decision 4 "Recent cycles": a small burndown sparkline per recent cycle,
/// drawn from the raw window-shape-*.csv rows (available for 60 days, DiskLogSink.CsvRetentionDays)
/// rather than from cycles.csv's own peak_pct/final_pct alone -- cycles.csv only has the start/end/
/// peak numbers, not the shape in between. Reuses CsvReplay.FilterForWindow (the exact same
/// jitter-tolerant "which raw rows belong to this window" rule QuotaModel.WarmStart already
/// relies on) rather than re-deriving window membership a third way.
///
/// A cycle older than the 60-day raw retention, or a demo/synthetic account with no raw
/// window-shape files at all, simply yields no points -- an empty sparkline is the honest
/// result, never a fabricated shape.
/// </summary>
public static class RecentCycleSparkline
{
    /// <summary>One point: ElapsedFraction in [0,1] of the window's own length, Pct the monotone envelope (running max) of the raw utilization seen by that point -- a burndown shape, not the raw (possibly replica-jittery) reading.</summary>
    public readonly record struct Point(double ElapsedFraction, double Pct);

    public static IReadOnlyList<Point> Load(string accountLogDir, WindowKind kind, DateTimeOffset startedUtc, DateTimeOffset resetUtc, double windowMinutes)
    {
        if (windowMinutes <= 0) return Array.Empty<Point>();

        var rows = new List<CsvReplay.RawRow>();
        foreach (string file in RelevantMonthlyFiles(accountLogDir, startedUtc, resetUtc))
            rows.AddRange(CsvReplay.ReadRows(file));
        if (rows.Count == 0) return Array.Empty<Point>();

        // "now" for FilterForWindow's own upper bound is the cycle's own reset -- a closed
        // historical cycle has no later rows that could legitimately belong to it anyway (a
        // new window has a different resets_at, outside jitter tolerance), so this simply
        // means "every row up through the window's own close".
        IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, kind, resetUtc, windowMinutes, resetUtc);
        if (matched.Count == 0) return Array.Empty<Point>();

        var points = new List<Point>(matched.Count);
        double envelope = 0.0;
        foreach (CsvReplay.MatchedRow m in matched)
        {
            envelope = Math.Max(envelope, m.Pct);
            double frac = Math.Clamp((m.UtcIso - startedUtc).TotalMinutes / windowMinutes, 0.0, 1.0);
            points.Add(new Point(frac, envelope));
        }
        return points;
    }

    /// <summary>Every window-shape-YYYY-MM.csv that could hold a row inside [startedUtc, resetUtc], in calendar order.</summary>
    static IEnumerable<string> RelevantMonthlyFiles(string logDir, DateTimeOffset startedUtc, DateTimeOffset resetUtc)
    {
        var months = new List<(int Year, int Month)>();
        DateTimeOffset cursor = new(startedUtc.Year, startedUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = new(resetUtc.Year, resetUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        while (cursor <= end)
        {
            months.Add((cursor.Year, cursor.Month));
            cursor = cursor.AddMonths(1);
        }

        foreach ((int year, int month) in months)
        {
            string path = Path.Combine(logDir, $"window-shape-{year:D4}-{month:D2}.csv");
            if (File.Exists(path)) yield return path;
        }
    }
}
