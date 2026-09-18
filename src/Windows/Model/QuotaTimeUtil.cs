using System.Globalization;

namespace ClaudeStatusBar.Model;

/// <summary>
/// Small time helpers shared by the ingest pipeline (WindowTracker) and the CSV
/// warm-start reader (Data/CsvReplay.cs), so both parse and key resets_at
/// identically. See docs/forecast-and-states.md, "Ingest" section.
/// </summary>
public static class QuotaTimeUtil
{
    /// <summary>
    /// Parses a raw resets_at wire string, e.g. "2026-09-11T11:20:00.524090+00:00"
    /// (6-digit fraction). Tolerant of the exact fractional digit count -- never
    /// hardcodes "O"-format's 7 digits, since the protocol writes 6.
    /// </summary>
    public static bool TryParseResetsAt(string? raw, out DateTimeOffset value)
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = default;
            return false;
        }
        return DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>
    /// Window key: resets_at truncated to the second, per the doc's ingest rule 1.
    /// Truncates in UTC so it is independent of the offset the string carried.
    /// Still used for CSV window-membership filtering (Data/CsvReplay.cs); the
    /// live tracker itself no longer uses exact truncated-second equality for
    /// rollover detection -- see WindowTracker.Ingest and review decision 1
    /// (docs/forecast-and-states.md's decisions table).
    /// </summary>
    public static DateTimeOffset TruncateToSecond(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        long wholeSeconds = utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond);
        return new DateTimeOffset(new DateTime(wholeSeconds, DateTimeKind.Utc));
    }

    /// <summary>Decision 9: utilization must be finite and in [0,100] at both the CSV and tracker boundaries.</summary>
    public static bool IsValidPct(double pct) => double.IsFinite(pct) && pct >= 0.0 && pct <= 100.0;

    /// <summary>
    /// Guards a derived-minutes offset before it reaches DateTimeOffset.AddMinutes,
    /// which throws on NaN/infinite/out-of-range input (decision 9: "derived
    /// timestamps are guarded").
    /// </summary>
    public static DateTimeOffset? SafeAddMinutes(DateTimeOffset utcNow, double minutes)
    {
        if (!double.IsFinite(minutes)) return null;
        try { return utcNow.AddMinutes(minutes); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// Decision 10 (round 2): resets_at is rejected outside this window around "now" --
    /// generous enough for any real session (5h) or weekly (7d) deadline, but never large
    /// enough to let a garbled or adversarial value (a year-9999 or year-1 timestamp still
    /// parses successfully) reach downstream DateTimeOffset arithmetic that could overflow.
    /// Checked at both live ingest (WindowTracker.Ingest) and warm-start replay
    /// (Data/CsvReplay.FilterForWindow, QuotaModel.ReplayWindow).
    /// </summary>
    public static bool IsPlausibleResetsAt(DateTimeOffset resetsAt, DateTimeOffset now) =>
        resetsAt >= now.AddDays(-1) && resetsAt <= now.AddDays(8);

    /// <summary>
    /// Guards a DateTimeOffset + TimeSpan addition that would otherwise throw on overflow
    /// (decision 10: "guard all derived deadline arithmetic, not only depletion time"). With
    /// resets_at now bounds-checked at ingest this should never actually overflow in practice,
    /// but every derived deadline stays defensively guarded regardless.
    /// </summary>
    public static DateTimeOffset? SafeAdd(DateTimeOffset dt, TimeSpan span)
    {
        try { return dt + span; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
