using System.Globalization;
using System.Linq;
using System.Text;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The on-disk shape of <c>hourly-YYYY.csv</c> (docs/statistics.md, decision 2): one row per
/// account directory, window kind and clock hour, one file per calendar year, forever retention.
/// Same conventions as CycleArchiveCsv (see that type's doc comment) and the same reason for
/// existing as its own small home rather than a CsvReplay/DiskLogSink refactor.
///
/// Column order (exact): schema, account_key, window, hour_start_utc, consumed_pct, samples,
/// covered_minutes. Unchanged by the schema-2 cycles.csv bump (nothing here needed a new column),
/// but TryParse still dispatches explicitly on the row's own schema field (Codex review #22) --
/// only "1" is supported; anything else is a malformed row, never silently accepted.
/// </summary>
public static class HourlyRollupCsv
{
    public const string Schema = "1";

    public static readonly string[] Header =
    {
        "schema", "account_key", "window", "hour_start_utc", "consumed_pct", "samples", "covered_minutes",
    };

    public readonly record struct Row(
        string AccountKey,
        WindowKind Window,
        DateTimeOffset HourStartUtc,
        double ConsumedPct,
        int Samples,
        double CoveredMinutes);

    /// <summary>hourly-YYYY.csv is one file per calendar year of hourStartUtc, so a leap year boundary or a long-running app never grows one file without bound.</summary>
    public static string FileName(int year) => $"hourly-{year}.csv";

    public static string FormatLine(Row r) => string.Join(",", new[]
    {
        Schema,
        CsvUtil.Field(r.AccountKey),
        CycleArchiveCsv.WindowToken(r.Window),
        CsvUtil.FormatUtc(r.HourStartUtc),
        CsvUtil.FormatPercent(r.ConsumedPct),
        r.Samples.ToString(CultureInfo.InvariantCulture),
        CsvUtil.FormatDouble(r.CoveredMinutes),
    });

    public static bool TryParse(string line, out Row row)
    {
        row = default;
        List<string>? f = CsvUtil.SplitLine(line);
        if (f is null || f.Count < Header.Length) return false;
        if (f[0] != Schema) return false; // Codex review #22: dispatch on the row's own schema, reject anything unsupported

        string accountKey = f[1];
        if (!CycleArchiveCsv.TryParseWindowToken(f[2], out WindowKind kind)) return false;
        if (!CsvUtil.TryParseUtc(f[3], out DateTimeOffset hourStart)) return false;
        if (!double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double consumedPct)) return false;
        if (!int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int samples)) return false;
        if (!double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double covered)) return false;

        row = new Row(accountKey, kind, hourStart, consumedPct, samples, covered);
        return true;
    }

    /// <summary>Appends one row to the file for its own calendar year, writing the header first if that file does not exist yet. Codex review #4: shares the per-account lock with WriteAllAtomically/StatisticsBackfill.</summary>
    public static void AppendRow(string logDir, Row row)
    {
        lock (CsvFileLock.For(logDir))
        {
            string path = Path.Combine(logDir, FileName(row.HourStartUtc.UtcDateTime.Year));
            bool exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" }; // Codex review #25
            if (!exists) writer.WriteLine(string.Join(",", Header));
            writer.WriteLine(FormatLine(row));
            writer.Flush();
        }
    }

    /// <summary>Reads every well-formed row from one year's file. Never throws -- same contract as CycleArchiveCsv.ReadAll.</summary>
    public static IReadOnlyList<Row> ReadAll(string path)
    {
        var rows = new List<Row>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            reader.ReadLine();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryParse(line, out Row row)) rows.Add(row);
            }
        }
        catch
        {
            // degrade to "no rows"
        }
        return rows;
    }

    /// <summary>Every hourly-*.csv file already on disk in a directory, newest first.</summary>
    public static IReadOnlyList<string> FindYearFiles(string logDir)
    {
        try
        {
            return Directory.EnumerateFiles(logDir, "hourly-*.csv")
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Crash-safe full rewrite of one year's file (docs/statistics.md, decision 5): temp file, flush, rename. Used by StatisticsBackfill. Codex review #4: shares the per-account lock with AppendRow.</summary>
    public static void WriteAllAtomically(string logDir, int year, IEnumerable<Row> rows)
    {
        lock (CsvFileLock.For(logDir))
        {
            Directory.CreateDirectory(logDir);
            string path = Path.Combine(logDir, FileName(year));
            string tmp = Path.Combine(logDir, $"{FileName(year)}.tmp-{Guid.NewGuid():N}");
            using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
            {
                writer.WriteLine(string.Join(",", Header));
                foreach (Row row in rows) writer.WriteLine(FormatLine(row));
                writer.Flush();
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}
