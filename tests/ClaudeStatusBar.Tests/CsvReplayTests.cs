using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class CsvReplayTests
{
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "window-shape-2026-09-11.csv");

    static string WriteRows(params string[] dataLines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"csvreplay-{Guid.NewGuid():N}.csv");
        var lines = new List<string> { "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source" };
        lines.AddRange(dataLines);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ReadRows_SucceedsWhileAnotherFileStreamHoldsTheFileOpenForAppend()
    {
        string path = Path.Combine(Path.GetTempPath(), $"window-shape-{Guid.NewGuid():N}.csv");
        File.Copy(FixturePath, path);

        using var appendHandle = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            Assert.True(rows.Count > 50, $"expected many rows, got {rows.Count}");
        }
        finally
        {
            appendHandle.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRows_OnMissingFile_ReturnsEmpty_NeverThrows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.csv");
        IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
        Assert.Empty(rows);
    }

    [Fact]
    public void ReadRows_SkipsMalformedLines_KeepsWellFormedOnes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[]
        {
            "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source",
            "not-a-timestamp,123,13,2026-09-11T11:20:00.500000+00:00,3,2026-09-18T05:00:00.500000+00:00,20.0,get_usage", // bad timestamp
            "2026-09-11T08:28:20.4533557+00:00,not-a-number,13,2026-09-11T11:20:00.500000+00:00,3,2026-09-18T05:00:00.500000+00:00,20.0,get_usage", // bad mono
            "too,few,columns",
            "2026-09-11T08:28:49.4858810+00:00,27785125,13,2026-09-11T11:20:00.528454+00:00,3,2026-09-18T05:00:00.528479+00:00,256.1,get_usage", // well-formed
        });

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            Assert.Single(rows);
            Assert.Equal(13.0, rows[0].SessionPct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- decision 9: NaN/out-of-range utilization must never poison the model ----

    [Fact]
    public void ReadRows_RejectsNaNUtilization_FieldBecomesNull_RowNotDropped()
    {
        string path = WriteRows(
            "2026-01-01T02:00:00.0000000+00:00,0,NaN,2026-01-01T05:00:00.000000+00:00,,,20,get_usage");
        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            Assert.Single(rows);
            Assert.Null(rows[0].SessionPct); // rejected, not silently propagated as NaN
            Assert.NotNull(rows[0].SessionResetsAtRaw); // the rest of the row is still usable
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadRows_RejectsOutOfRangeUtilization()
    {
        string path = WriteRows(
            "2026-01-01T02:00:00.0000000+00:00,0,150,2026-01-01T05:00:00.000000+00:00,-5,2026-01-08T05:00:00.000000+00:00,20,get_usage");
        IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
        try
        {
            Assert.Single(rows);
            Assert.Null(rows[0].SessionPct); // 150 > 100: rejected
            Assert.Null(rows[0].WeeklyPct);  // -5 < 0: rejected
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- decision 8: future rows are rejected; replay is bound to the window's own start ----

    [Fact]
    public void FilterForWindow_RejectsRowsFromTheFuture()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string raw = resetsAt.ToString("O");
        string path = WriteRows(
            $"{now.AddMinutes(10):yyyy-MM-ddTHH:mm:ss.fffffffK},0,99.6,{raw},,,20,get_usage", // 10 min in the future
            $"{now.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss.fffffffK},0,20.0,{raw},,,20,get_usage");  // a normal, valid row

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);

            Assert.Single(matched);
            Assert.Equal(20.0, matched[0].Pct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FilterForWindow_BoundsReplayToTheWindowsOwnStart_NotNowMinusW()
    {
        // A row that is within the last `windowMinutes` of NOW but before the window's own
        // start (windowKey - windowMinutes) must be excluded -- it cannot belong to the
        // current window even though "now minus W" alone would have let it through.
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero); // window start = 2026-01-01T00:00:00
        DateTimeOffset now = new(2026, 1, 1, 4, 0, 0, TimeSpan.Zero); // now - W = 2025-12-31T23:00:00 (before the window's own start, so it alone would let the row below through)
        DateTimeOffset rowUtc = new(2025, 12, 31, 23, 30, 0, TimeSpan.Zero); // after now-W, but before the window's own start
        string raw = resetsAt.ToString("O");
        string path = WriteRows($"{rowUtc:yyyy-MM-ddTHH:mm:ss.fffffffK},0,50.0,{raw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);

            Assert.Empty(matched);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- decision 5 (round 2): no +60s allowance -- rejects everything later than "now" itself ----

    [Fact]
    public void FilterForWindow_RejectsARowOneSecondAfterNow()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string raw = resetsAt.ToString("O");
        string path = WriteRows($"{now.AddSeconds(1):yyyy-MM-ddTHH:mm:ss.fffffffK},0,99.0,{raw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);
            Assert.Empty(matched); // even 1s later than "now" is rejected -- the old now+60s tolerance is gone
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FilterForWindow_RejectsARowSixtySecondsAfterNow_TheOldToleranceIsGone()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string raw = resetsAt.ToString("O");
        string path = WriteRows($"{now.AddSeconds(60):yyyy-MM-ddTHH:mm:ss.fffffffK},0,99.0,{raw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);
            Assert.Empty(matched);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FilterForWindow_AcceptsARowExactlyAtNow_TheEqualLiveBoundary()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string raw = resetsAt.ToString("O");
        string path = WriteRows($"{now:yyyy-MM-ddTHH:mm:ss.fffffffK},0,55.0,{raw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);
            Assert.Single(matched); // exactly "now" itself is still valid history
        }
        finally { File.Delete(path); }
    }

    // ---- decision 6 (round 2): replay membership uses the same +-120s jitter tolerance as live ingest ----

    [Fact]
    public void FilterForWindow_MatchesAJitteredRow_AcrossATruncatedSecondBoundary()
    {
        // "2026-01-01T04:59:59.450402" is ~0.55s BEFORE "2026-01-01T05:00:00.000000" -- same
        // window under the live +-120s jitter rule, but a DIFFERENT truncated second. The old
        // exact-truncated-second match excluded it; membership must now agree with what a live
        // poll would have accepted as "the same window".
        DateTimeOffset windowKey = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string jitteredRaw = "2026-01-01T04:59:59.450402+00:00";
        string path = WriteRows($"{now.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss.fffffffK},0,90.0,{jitteredRaw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, windowKey, QuotaWindows.SessionMinutes, now);
            Assert.Single(matched);
            Assert.Equal(90.0, matched[0].Pct);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FilterForWindow_StillExcludesARowMoreThanOneHundredTwentySecondsAway()
    {
        DateTimeOffset windowKey = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string farRaw = "2026-01-01T04:57:00.000000+00:00"; // 180s before windowKey: outside the jitter tolerance
        string path = WriteRows($"{now.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss.fffffffK},0,90.0,{farRaw},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, windowKey, QuotaWindows.SessionMinutes, now);
            Assert.Empty(matched);
        }
        finally { File.Delete(path); }
    }

    // ---- decision 10 (round 2): an implausible windowKey must never reach AddMinutes ----

    [Fact]
    public void FilterForWindow_RejectsAnImplausibleWindowKey_NeverThrows()
    {
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        DateTimeOffset extremeKey = DateTimeOffset.Parse("0001-01-01T00:00:00.000000+00:00");
        string path = WriteRows($"{now.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss.fffffffK},0,50.0,{extremeKey:O},,,20,get_usage");

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            var ex = Record.Exception(() =>
                CsvReplay.FilterForWindow(rows, WindowKind.Session, extremeKey, QuotaWindows.WeeklyMinutes, now));
            Assert.Null(ex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FilterForWindow_EqualTimestamps_OrdersDeterministically()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset rowAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = rowAt.AddMinutes(30);
        string raw = resetsAt.ToString("O");
        string path = WriteRows(
            $"{rowAt:yyyy-MM-ddTHH:mm:ss.fffffffK},200,15.0,{raw},,,20,get_usage",
            $"{rowAt:yyyy-MM-ddTHH:mm:ss.fffffffK},100,10.0,{raw},,,20,get_usage"); // same utc_iso, written in reverse mono order

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            DateTimeOffset key = QuotaTimeUtil.TruncateToSecond(resetsAt);
            IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, WindowKind.Session, key, QuotaWindows.SessionMinutes, now);

            Assert.Equal(2, matched.Count);
            Assert.Equal(100, matched[0].MonoMs); // ordered by MonoMs as the tiebreaker, regardless of file order
            Assert.Equal(200, matched[1].MonoMs);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
