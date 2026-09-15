using System.Globalization;
using System.Linq;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Reads logs\window-shape.csv for QuotaModel's warm start (docs/forecast-and-
/// states.md, "Ingest" rule 6). Pure file IO + filtering -- it never applies
/// fingerprint dedup, the monotone envelope or the estimator itself; those stay
/// WindowTracker.Ingest's job so a replayed row and a live poll go through
/// identical logic. Never throws: a missing, locked or corrupt file just yields
/// no warm-start data, since a cold start is always a valid (if slower) start.
/// </summary>
public static class CsvReplay
{
    /// <summary>One CSV row, columns as ClaudeCliChannel.WindowShapeCsvHeader writes them.</summary>
    public readonly record struct RawRow(
        DateTimeOffset UtcIso,
        long MonoMs,
        double? SessionPct,
        string? SessionResetsAtRaw,
        double? WeeklyPct,
        string? WeeklyResetsAtRaw);

    /// <summary>One row that belongs to a specific current window, ready to feed into WindowTracker.Ingest.</summary>
    public readonly record struct MatchedRow(DateTimeOffset UtcIso, long MonoMs, double Pct, string ResetsAtRaw);

    /// <summary>
    /// Reads and parses every well-formed row. Opened with FileShare.ReadWrite
    /// because ClaudeCliChannel may hold the same file open for append.
    /// </summary>
    /// <summary>
    /// Warm-start entry point once window-shape.csv is rotated monthly (Codex review
    /// Medium #10): reads the current calendar month's file plus the previous month's,
    /// since a window (up to 7 days for the weekly quota) can straddle a month boundary
    /// and warm start must still be able to see rows from just before it. Rows from both
    /// files are simply concatenated -- FilterForWindow already re-sorts and re-validates
    /// everything it is handed, so file order here does not matter. Missing/locked files
    /// degrade to fewer rows, never an exception (same contract as ReadRows).
    /// </summary>
    public static IReadOnlyList<RawRow> ReadRowsForWarmStart(string logDir, DateTimeOffset now)
    {
        string currentPath = DiskLogSink.MonthlyCsvPath(logDir, now);
        string previousPath = DiskLogSink.MonthlyCsvPath(logDir, now.AddMonths(-1));

        var rows = new List<RawRow>(ReadRows(currentPath));
        if (!string.Equals(currentPath, previousPath, StringComparison.OrdinalIgnoreCase))
            rows.AddRange(ReadRows(previousPath));
        return rows;
    }

    public static IReadOnlyList<RawRow> ReadRows(string path)
    {
        var rows = new List<RawRow>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            reader.ReadLine(); // header
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryParseRow(line, out RawRow row)) rows.Add(row);
                // malformed rows (bad timestamp, wrong column count, ...) are silently skipped
            }
        }
        catch
        {
            // missing file, locked file, IO error, whatever -- warm start is best-effort only.
        }
        return rows;
    }

    /// <summary>
    /// Rows for one window: matching resets_at within the same +-120s jitter tolerance a
    /// live poll would use for "same window" (decision 6, round 2 -- the same rule as
    /// WindowTracker.JitterTolerance, not exact truncated-second equality, so a row a live
    /// poll would have accepted is never excluded from warm start on a sub-second/jitter
    /// technicality), bounded to the window's own start (decision 8: NOT "now minus
    /// windowMinutes" -- the inferred window start is windowKey minus windowMinutes, which
    /// is what a row genuinely belonging to this window can have), with rows later than the
    /// live observation's own UTC rejected outright (decision 5, round 2: the old "now + 60s"
    /// allowance let a near-future CSV row install a corrupted envelope that then made the
    /// genuine live sample look out-of-order and get rejected -- there is no safe tolerance
    /// here, only "now" itself), utilization finite and in [0,100] (decision 9, re-checked
    /// here even though ReadRows/TryParseDouble already reject non-finite values, so a future
    /// caller building MatchedRows any other way stays safe too), and windowKey itself
    /// bounds-checked (decision 10: an extreme/garbled resets_at must never reach the
    /// AddMinutes below). Sorted oldest first, then by MonoMs for deterministic ordering when
    /// two rows share the same UtcIso (decision 8), so replay reconstructs history in the
    /// order it happened.
    /// </summary>
    public static IReadOnlyList<MatchedRow> FilterForWindow(
        IReadOnlyList<RawRow> rows, WindowKind kind, DateTimeOffset windowKey, double windowMinutes, DateTimeOffset now)
    {
        if (!QuotaTimeUtil.IsPlausibleResetsAt(windowKey, now)) return Array.Empty<MatchedRow>(); // decision 10

        DateTimeOffset earliestAllowed = windowKey.AddMinutes(-windowMinutes); // the window's own start, not now-W
        DateTimeOffset latestAllowed = now; // decision 5: never later than the live observation itself
        var matches = new List<MatchedRow>();

        foreach (RawRow row in rows)
        {
            double? pct = kind == WindowKind.Session ? row.SessionPct : row.WeeklyPct;
            string? raw = kind == WindowKind.Session ? row.SessionResetsAtRaw : row.WeeklyResetsAtRaw;
            if (pct is null || raw is null) continue;
            if (!QuotaTimeUtil.IsValidPct(pct.Value)) continue;
            if (!QuotaTimeUtil.TryParseResetsAt(raw, out DateTimeOffset resetsAt)) continue;
            if (Math.Abs((resetsAt - windowKey).TotalSeconds) > WindowTracker.JitterTolerance.TotalSeconds) continue; // decision 6
            if (row.UtcIso < earliestAllowed) continue;
            if (row.UtcIso > latestAllowed) continue; // decision 5: reject rows later than the live observation

            matches.Add(new MatchedRow(row.UtcIso, row.MonoMs, pct.Value, raw));
        }

        return matches.OrderBy(m => m.UtcIso).ThenBy(m => m.MonoMs).ToList();
    }

    static bool TryParseRow(string line, out RawRow row)
    {
        row = default;
        List<string>? fields = SplitCsvLine(line);
        if (fields is null || fields.Count < 8) return false;

        if (!DateTimeOffset.TryParse(
                fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset utcIso))
            return false;
        if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long monoMs))
            return false;

        row = new RawRow(
            utcIso,
            monoMs,
            TryParseDouble(fields[2]),
            NullIfEmpty(fields[3]),
            TryParseDouble(fields[4]),
            NullIfEmpty(fields[5]));
        return true;
    }

    /// <summary>
    /// Decision 9: reject non-finite/out-of-range utilization at the CSV boundary too
    /// (double.TryParse otherwise happily accepts the literal strings "NaN" and
    /// "Infinity"). A rejected field becomes null -- normal "field absent" handling
    /// downstream, not a row-mutating failure.
    /// </summary>
    static double? TryParseDouble(string field) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && QuotaTimeUtil.IsValidPct(value)
            ? value
            : null;

    static string? NullIfEmpty(string field) => field.Length == 0 ? null : field;

    /// <summary>Minimal quote-aware CSV split. Returns null on an unterminated quote (malformed row).</summary>
    static List<string>? SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        if (inQuotes) return null; // unterminated quote: malformed
        fields.Add(current.ToString());
        return fields;
    }
}
