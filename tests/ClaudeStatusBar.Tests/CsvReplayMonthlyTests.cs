using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Codex review Medium #10: window-shape.csv is rotated monthly
/// (window-shape-YYYY-MM.csv, see DiskLogSink.MonthlyCsvPath). CsvReplay.ReadRowsForWarmStart
/// is the minimal forced change (docs/reviews/2026-09-11-codex-runtime.md Decisions) that
/// keeps warm start working across that rotation: it must read the current month's file and
/// fall back to (really: also include) the previous month's when a window straddles the
/// boundary.
/// </summary>
public class CsvReplayMonthlyTests
{
    static string NewLogDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"csv-monthly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void WriteMonthlyFile(string logDir, DateTimeOffset month, params string[] dataLines)
    {
        string path = DiskLogSink.MonthlyCsvPath(logDir, month);
        var lines = new List<string> { "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source" };
        lines.AddRange(dataLines);
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public void ReadRowsForWarmStart_ReadsCurrentMonthFile()
    {
        string dir = NewLogDir();
        try
        {
            DateTimeOffset now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            WriteMonthlyFile(dir, now,
                "2026-09-11T08:00:00.0000000+00:00,0,13,2026-09-11T11:20:00.000000+00:00,,,20,get_usage");

            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRowsForWarmStart(dir, now);
            Assert.Single(rows);
            Assert.Equal(13.0, rows[0].SessionPct);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadRowsForWarmStart_AlsoIncludesThePreviousMonthsFile()
    {
        // A weekly window can straddle a month boundary (up to 7 days back): a row written in
        // late August must still be visible to a warm start running in early September.
        string dir = NewLogDir();
        try
        {
            DateTimeOffset now = new(2026, 9, 1, 3, 0, 0, TimeSpan.Zero);
            DateTimeOffset lastMonth = now.AddMonths(-1);

            WriteMonthlyFile(dir, now,
                "2026-09-01T02:00:00.0000000+00:00,0,20,2026-09-01T05:00:00.000000+00:00,,,20,get_usage");
            WriteMonthlyFile(dir, lastMonth,
                "2026-08-30T10:00:00.0000000+00:00,0,55,2026-09-01T05:00:00.000000+00:00,,,20,get_usage");

            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRowsForWarmStart(dir, now);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.SessionPct == 20.0);
            Assert.Contains(rows, r => r.SessionPct == 55.0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadRowsForWarmStart_MissingFiles_ReturnsEmpty_NeverThrows()
    {
        string dir = NewLogDir();
        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRowsForWarmStart(dir, DateTimeOffset.UtcNow);
            Assert.Empty(rows);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MonthlyCsvPath_IsStableForTheSameCalendarMonth()
    {
        string dir = NewLogDir();
        try
        {
            DateTimeOffset early = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset late = new(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
            Assert.Equal(DiskLogSink.MonthlyCsvPath(dir, early), DiskLogSink.MonthlyCsvPath(dir, late));
            Assert.EndsWith("window-shape-2026-09.csv", DiskLogSink.MonthlyCsvPath(dir, early));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
