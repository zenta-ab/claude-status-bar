using System.Globalization;
using System.Text;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The on-disk shape of <c>cycles.csv</c> (docs/statistics.md, decision 2): one row per account
/// directory, appended when a window closes, forever retention. This is the single place both
/// the live writer (DiskLogSink) and the readers (StatisticsBackfill, StatisticsEngine) format
/// and parse a row, so they can never silently disagree with each other -- and, per the doc, this
/// format is also the byte-compatible contract the Swift implementation will read/write later.
///
/// Column order (exact, schema 2): schema, account_key, window, started_utc, reset_utc, peak_pct,
/// final_pct, hit_ceiling, blocked_minutes, covered_minutes, plan_tier, warned_dry_early,
/// predicted_peak_pct, predicted_at_utc, ceiling_reached_at, ceiling_censored.
///
/// Schema 2 (Codex review #14) adds the last two columns: ceiling_reached_at is the moment the
/// envelope actually reached 100% (empty when HitCeiling is false), and ceiling_censored ("1"/"0")
/// says whether that instant is EXACT (observed across a continuously-polled gap no larger than
/// the continuity allowance) or INTERVAL-CENSORED (the crossing happened somewhere inside a
/// larger gap -- the app was cold-started or resumed after a break already at/above 100%, so the
/// true crossing instant is only known to lie somewhere in that unobserved interval).
/// blocked_minutes stays the reset-minus-reached figure either way, but is only a LOWER BOUND
/// when censored -- the ceiling could have been reached earlier, inside the gap.
///
/// A schema-1 file (written before this bump; real files exist on disk from the previous build)
/// is read by MIGRATING each row on the fly: ceiling_reached_at is derived the old way
/// (reset_utc - blocked_minutes) when HitCeiling, and always marked CENSORED -- schema 1 never
/// recorded whether the old backfill/live math was itself continuous, so a migrated row cannot
/// honestly claim exactness it never verified. TryParse dispatches on schema (Codex review #22):
/// only "1" and "2" are supported; anything else is a malformed row, never silently parsed as 1.
///
/// Conventions, matching window-shape-*.csv (DiskLogSink) exactly:
///  - Timestamps: DateTimeOffset "O" (round-trip) format, ALWAYS UTC (CsvUtil.FormatUtc/TryParseUtc
///    normalize on write and reject a non-zero offset on read -- Codex review #21).
///  - Numbers: InvariantCulture ToString() (no fixed decimal places), never a thousands separator.
///  - Booleans (hit_ceiling, warned_dry_early, ceiling_censored): the literal "1" or "0" only --
///    anything else is a malformed row (Codex review #23), never silently false.
///  - A missing/unknown value (plan_tier when never observed; predicted_peak_pct/predicted_at_utc
///    when the app was never running at the window's 50% mark, or the row was backfilled; ceiling_
///    reached_at/ceiling_censored when HitCeiling is false) is the empty string, never "null"/
///    "NaN"/"-1".
///  - account_key/plan_tier are quoted only if they contain a comma/quote; CR/LF are forbidden
///    outright (CsvUtil.Field throws) -- the reader is strictly line-oriented (Codex review #24).
/// </summary>
public static class CycleArchiveCsv
{
    /// <summary>The schema this build WRITES. A reader still accepts schema 1 (migrating it) -- see the class doc comment.</summary>
    public const string Schema = "2";
    public const string FileName = "cycles.csv";

    public static readonly string[] Header =
    {
        "schema", "account_key", "window", "started_utc", "reset_utc", "peak_pct", "final_pct",
        "hit_ceiling", "blocked_minutes", "covered_minutes", "plan_tier", "warned_dry_early",
        "predicted_peak_pct", "predicted_at_utc", "ceiling_reached_at", "ceiling_censored",
    };

    /// <summary>Column count of a schema-1 row (the header this type used to write, before ceiling_reached_at/ceiling_censored existed).</summary>
    const int Schema1ColumnCount = 14;

    public readonly record struct Row(
        string AccountKey,
        WindowKind Window,
        DateTimeOffset StartedUtc,
        DateTimeOffset ResetUtc,
        double PeakPct,
        double FinalPct,
        bool HitCeiling,
        double BlockedMinutes,
        double CoveredMinutes,
        string? PlanTier,
        bool WarnedDryEarly,
        double? PredictedPeakPct,
        DateTimeOffset? PredictedAtUtc,
        DateTimeOffset? CeilingReachedAtUtc = null,
        bool CeilingReachedCensored = false);

    public static string FormatLine(Row r) => string.Join(",", new[]
    {
        Schema,
        CsvUtil.Field(r.AccountKey),
        WindowToken(r.Window),
        CsvUtil.FormatUtc(r.StartedUtc),
        CsvUtil.FormatUtc(r.ResetUtc),
        CsvUtil.FormatPercent(r.PeakPct),
        CsvUtil.FormatPercent(r.FinalPct),
        r.HitCeiling ? "1" : "0",
        CsvUtil.FormatDouble(r.BlockedMinutes),
        CsvUtil.FormatDouble(r.CoveredMinutes),
        CsvUtil.Field(r.PlanTier ?? ""),
        r.WarnedDryEarly ? "1" : "0",
        r.PredictedPeakPct is { } p ? CsvUtil.FormatPercent(p) : "",
        r.PredictedAtUtc is { } a ? CsvUtil.FormatUtc(a) : "",
        r.CeilingReachedAtUtc is { } ca ? CsvUtil.FormatUtc(ca) : "",
        r.CeilingReachedAtUtc is null ? "" : (r.CeilingReachedCensored ? "1" : "0"),
    });

    public static bool TryParse(string line, out Row row)
    {
        row = default;
        List<string>? f = CsvUtil.SplitLine(line);
        if (f is null || f.Count == 0) return false;

        // Codex review #22: dispatch explicitly on the row's OWN schema field -- never assume
        // "looks like schema 1" from column count alone, and never silently accept an unknown
        // future schema as if it were this one.
        return f[0] switch
        {
            "1" => TryParseSchema1(f, out row),
            "2" => TryParseSchema2(f, out row),
            _ => false,
        };
    }

    static bool TryParseSchema1(List<string> f, out Row row)
    {
        row = default;
        if (f.Count < Schema1ColumnCount) return false;
        if (!TryParseCommonFields(f, out Row common)) return false;

        // Migration (class doc comment): schema 1 never stored the ceiling-reached instant
        // directly -- derive it the old way and mark it unconditionally censored, since schema 1
        // never verified whether that derivation crossed an unobserved gap.
        DateTimeOffset? ceilingReachedAt = common.HitCeiling ? common.ResetUtc.AddMinutes(-common.BlockedMinutes) : null;
        row = common with { CeilingReachedAtUtc = ceilingReachedAt, CeilingReachedCensored = common.HitCeiling };
        return true;
    }

    static bool TryParseSchema2(List<string> f, out Row row)
    {
        row = default;
        if (f.Count < Header.Length) return false;
        if (!TryParseCommonFields(f, out Row common)) return false;

        DateTimeOffset? ceilingReachedAt = null;
        if (f[14].Length != 0)
        {
            if (!CsvUtil.TryParseUtc(f[14], out DateTimeOffset ca)) return false;
            ceilingReachedAt = ca;
        }
        bool censored = false;
        if (ceilingReachedAt is not null)
        {
            if (f[15].Length == 0 || !CsvUtil.TryParseBool01(f[15], out censored)) return false;
        }

        row = common with { CeilingReachedAtUtc = ceilingReachedAt, CeilingReachedCensored = censored };
        return true;
    }

    /// <summary>Columns 1..13, identical layout in both schema 1 and schema 2.</summary>
    static bool TryParseCommonFields(List<string> f, out Row row)
    {
        row = default;
        string accountKey = f[1];
        if (!TryParseWindowToken(f[2], out WindowKind kind)) return false;
        if (!CsvUtil.TryParseUtc(f[3], out DateTimeOffset started)) return false;
        if (!CsvUtil.TryParseUtc(f[4], out DateTimeOffset reset)) return false;
        if (!double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double peak)) return false;
        if (!double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double final)) return false;
        if (!CsvUtil.TryParseBool01(f[7], out bool hitCeiling)) return false;
        if (!double.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out double blocked)) return false;
        if (!double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out double covered)) return false;
        string? planTier = f[10].Length == 0 ? null : f[10];
        if (!CsvUtil.TryParseBool01(f[11], out bool warnedDryEarly)) return false;
        double? predictedPeak = f[12].Length != 0
            && double.TryParse(f[12], NumberStyles.Float, CultureInfo.InvariantCulture, out double pp) ? pp : null;
        if (f[12].Length != 0 && predictedPeak is null) return false;
        DateTimeOffset? predictedAt = null;
        if (f[13].Length != 0)
        {
            if (!CsvUtil.TryParseUtc(f[13], out DateTimeOffset pa)) return false;
            predictedAt = pa;
        }

        row = new Row(accountKey, kind, started, reset, peak, final, hitCeiling, blocked, covered,
            planTier, warnedDryEarly, predictedPeak, predictedAt);
        return true;
    }

    public static string WindowToken(WindowKind kind) => kind == WindowKind.Session ? "session" : "weekly";

    public static bool TryParseWindowToken(string token, out WindowKind kind)
    {
        switch (token)
        {
            case "session": kind = WindowKind.Session; return true;
            case "weekly": kind = WindowKind.Weekly; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// Appends one row, writing the header first if the file does not exist yet. Flushed
    /// synchronously -- must survive a crash or a kill, not just a clean exit (matches
    /// DiskLogSink.WriteCsv). Codex review #4: serialized against StatisticsBackfill's rewrite of
    /// this same account's files via the shared per-account CsvFileLock.
    /// </summary>
    public static void AppendRow(string path, Row row)
    {
        lock (CsvFileLock.For(Path.GetDirectoryName(path) ?? "."))
        {
            bool exists = File.Exists(path);
            using var writer = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" }; // Codex review #25: the byte contract fixes '\n', never platform-dependent Environment.NewLine
            if (!exists) writer.WriteLine(string.Join(",", Header));
            writer.WriteLine(FormatLine(row));
            writer.Flush();
        }
    }

    /// <summary>Reads every well-formed row. Never throws: a missing/locked/corrupt file yields no rows (same contract as CsvReplay.ReadRows), and a malformed line is silently skipped.</summary>
    public static IReadOnlyList<Row> ReadAll(string path)
    {
        var rows = new List<Row>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            reader.ReadLine(); // header
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (TryParse(line, out Row row)) rows.Add(row);
            }
        }
        catch
        {
            // missing file, locked file, IO error -- degrade to "no rows", same as every other reader here.
        }
        return rows;
    }

    /// <summary>
    /// Rewrites the whole file from scratch, crash-safely: write to a temp file in the same
    /// directory, flush, then rename over the real path (docs/statistics.md, decision 5's
    /// backfill contract). Used by StatisticsBackfill, never by the live per-row appender. Codex
    /// review #4: shares AppendRow's per-account lock, so a rewrite can never land between a live
    /// append's own read-modify-write (there isn't one -- AppendRow only ever appends) but,
    /// crucially, StatisticsBackfill's own read-then-write sequence (see MergeCycles) holds this
    /// same lock across BOTH calls, so a live append can never be dropped by an in-flight rewrite
    /// that read the file before that append landed.
    /// </summary>
    public static void WriteAllAtomically(string path, IEnumerable<Row> rows)
    {
        lock (CsvFileLock.For(Path.GetDirectoryName(path) ?? "."))
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
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
