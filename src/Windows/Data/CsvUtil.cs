using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Small CSV field-quoting/splitting/timestamp helpers shared by the two statistics-archive
/// formats (CycleArchiveCsv, HourlyRollupCsv). Deliberately a fresh, small helper rather than a
/// refactor of DiskLogSink.CsvField or CsvReplay.SplitCsvLine -- both of those are unchanged,
/// heavily-tested, and outside this task's scope; the algorithms here are the same minimal
/// quote-aware CSV convention, just given one shared home for the two new formats.
/// </summary>
static class CsvUtil
{
    /// <summary>
    /// Quotes a field only if it contains a comma/quote -- matches DiskLogSink.CsvField. CR/LF
    /// are never quoted-and-embedded (Codex review #24/#25 -- the byte contract): the reader is
    /// strictly line-oriented (StreamReader.ReadLine), so a quoted embedded newline would split
    /// silently across two "rows" and corrupt the file. account_key/plan_tier are the only free
    /// text here (a UUID pair, a short slug) and never legitimately contain a newline, so this is
    /// a hard contract violation, not a formatting choice -- reject rather than silently produce
    /// an unreadable file.
    /// </summary>
    public static string Field(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.IndexOfAny(new[] { '\n', '\r' }) >= 0)
            throw new ArgumentException("cycles.csv/hourly-*.csv fields may never contain CR or LF (the byte contract, docs/statistics.md)", nameof(raw));
        return raw.IndexOfAny(new[] { ',', '"' }) >= 0
            ? "\"" + raw.Replace("\"", "\"\"") + "\""
            : raw;
    }

    /// <summary>The byte contract (docs/statistics.md, Codex review #21): every timestamp is written in UTC, never a local offset -- ToUniversalTime() normalizes regardless of what the caller happened to hold.</summary>
    public static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The byte contract's read side: a value that does not parse, or that parses to a non-zero
    /// UTC offset, is rejected outright (Codex review #21 -- "the alleged byte-compatible
    /// contract does not force UTC"). A reader must never silently normalize a +02:00 wall clock
    /// into something that looks like UTC; the whole row is malformed instead.
    /// </summary>
    public static bool TryParseUtc(string raw, out DateTimeOffset value)
    {
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            value = default;
            return false;
        }
        value = parsed;
        return true;
    }

    public static string FormatDouble(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Codex review #25, the byte contract: a PERCENTAGE column (peak_pct, final_pct,
    /// consumed_pct, predicted_peak_pct -- never blocked_minutes/covered_minutes, which are plain
    /// minute counts and keep FormatDouble's full precision) is written as a bare integer when
    /// its value is integral, otherwise InvariantCulture with AT MOST two decimal places
    /// (rounded, not truncated) -- never .NET double.ToString()'s unbounded-precision default,
    /// which a Swift `String(describing:)`/`"\(x)"` need not reproduce digit-for-digit.
    /// </summary>
    public static string FormatPercent(double value)
    {
        double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return rounded == Math.Truncate(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Codex review #23: a boolean column is the literal "1" or "0" and nothing else -- "true"/"TRUE"/"x"/anything else is a malformed row, never a silent false.</summary>
    public static bool TryParseBool01(string raw, out bool value)
    {
        switch (raw)
        {
            case "1": value = true; return true;
            case "0": value = false; return true;
            default: value = false; return false;
        }
    }

    /// <summary>Minimal quote-aware CSV split (same algorithm as CsvReplay.SplitCsvLine). Returns null on an unterminated quote (malformed row).</summary>
    public static List<string>? SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
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
        if (inQuotes) return null;
        fields.Add(current.ToString());
        return fields;
    }
}

/// <summary>
/// Codex review #4: one lock per account log directory, shared by every live append
/// (CycleArchiveCsv/HourlyRollupCsv.AppendRow, driven by DiskLogSink's background queue) and
/// every backfill rewrite (StatisticsBackfill's read-merge-write-atomically sequence) that
/// touches files under that directory. Coarser than "one lock per file" on purpose: a single
/// per-account lock is simplest to reason about and cheap enough (a window closes at most a
/// handful of times a day; backfill runs once per install), and it lets StatisticsBackfill hold
/// the SAME lock across its own read, then re-read, then write, so a live append landing in
/// between can never be silently dropped by a backfill rewrite that started before it.
/// </summary>
static class CsvFileLock
{
    static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>directory is normalized (Path.GetFullPath) so two different-but-equivalent path strings for the same account directory always resolve to the same lock object.</summary>
    public static object For(string directory) => Locks.GetOrAdd(Path.GetFullPath(directory), _ => new object());
}
