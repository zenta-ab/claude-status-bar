using ClaudeStatusBar.Model;
using System.Globalization;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// The single shared time formatter for panel-v2 (docs/panel-v2.md, "Time text").
/// Every place the app shows a duration or a point in time -- the status box, the
/// per-window sections, the footer, the freshness line, and the tray tooltip --
/// must go through this, so a raw minute count above an hour ("6975 min") can
/// never leak out again. Pure functions of (value, now, tz) so tests are
/// deterministic regardless of the machine's real clock/timezone.
/// </summary>
public static class TimeText
{
    static readonly string[] WeekdayAbbrev = { "sön", "mån", "tis", "ons", "tor", "fre", "lör" }; // DayOfWeek 0..6

    /// <summary>
    /// A span of time: under 1 min -> "44 s"; under 2 min -> "1 min 30 s"; under
    /// 1 h -> "44 min"; under 24 h -> "20 h 15 min" (drops "0 min"); 24 h and up
    /// -> "4 d 20 h" (drops minutes). Negative spans clamp to zero.
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        double totalSeconds = Math.Max(0.0, span.TotalSeconds);
        var ts = TimeSpan.FromSeconds(Math.Round(totalSeconds)); // real subtraction can leave sub-second jitter; round once, up front

        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds} s";
        if (ts.TotalSeconds < 120) return $"1 min {ts.Seconds} s";
        if (ts.TotalHours < 1) return $"{(int)ts.TotalMinutes} min";
        if (ts.TotalHours < 24) return ts.Minutes == 0 ? $"{(int)ts.TotalHours} h" : $"{(int)ts.TotalHours} h {ts.Minutes} min";
        return $"{(int)ts.TotalDays} d {ts.Hours} h";
    }

    /// <summary>
    /// "kl 12:36" today; "i morgon kl 10:44" tomorrow; "sön kl 10:44" up to six days ahead; "12 okt kl
    /// 10:44" beyond that (a weekday alone would be ambiguous from a week away: it names today's own
    /// weekday); "igår kl 22:10" yesterday. Calendar-date comparison in tz, not elapsed time, so it
    /// handles crossing midnight (and DST changes) correctly. A time that is not today always carries its day.
    /// </summary>
    public static string PointInTime(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz) =>
        $"{DayPrefix(t, now, tz)}kl {ClockOnly(t, tz)}";

    /// <summary>"" today, else the day followed by a space: "i morgon ", "tis ", "12 okt ", "igår ".</summary>
    static string DayPrefix(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        string day = DayWord(t, now, tz);
        return day.Length == 0 ? "" : day + " ";
    }

    /// <summary>The day a time falls on, relative to now's date in tz: "" today, "i morgon", "igår", the weekday for 2-6 days ahead, otherwise the date ("12 okt").</summary>
    public static string DayWord(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        int diff = DayDiff(t, now, tz);
        return diff switch
        {
            0 => "",
            1 => "i morgon",
            -1 => "igår",
            >= 2 and <= 6 => Weekday(t, tz),
            _ => DateLabel(t, tz),
        };
    }

    /// <summary>"12 okt".</summary>
    public static string DateLabel(DateTimeOffset t, TimeZoneInfo tz)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(t, tz);
        return $"{local.Day} {SwedishText.MonthAbbrev[local.Month]}";
    }

    /// <summary>The weekday ("tis") for today and up to six days ahead; the date ("12 okt") otherwise -- for the weekly window, which always names its day.</summary>
    public static string WeekdayOrDate(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        int diff = DayDiff(t, now, tz);
        return diff is >= 0 and <= 6 ? Weekday(t, tz) : DateLabel(t, tz);
    }

    /// <summary>A bare clock with its day when it is not today: "15:00", "i morgon 07:00", "tis 15:00", "12 okt 15:00" -- the compact form for rows and tooltips.</summary>
    public static string ClockWithDay(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz) =>
        $"{DayPrefix(t, now, tz)}{ClockOnly(t, tz)}";

    /// <summary>Calendar-date difference between t and now, in tz -- 0 today, 1 tomorrow, etc. (negative if t is in the past relative to now's date).</summary>
    public static int DayDiff(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(t, tz);
        DateTimeOffset nowLocal = TimeZoneInfo.ConvertTime(now, tz);
        return (local.Date - nowLocal.Date).Days;
    }

    public static bool IsToday(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz) => DayDiff(t, now, tz) == 0;

    /// <summary>Just the weekday abbreviation, e.g. "sön" -- for compositions (Kvot-bar depletion label, weekly section header) that spell the weekday differently from PointInTime's "kl"-prefixed form.</summary>
    public static string Weekday(DateTimeOffset t, TimeZoneInfo tz) =>
        WeekdayAbbrev[(int)TimeZoneInfo.ConvertTime(t, tz).DayOfWeek];

    public static string ClockOnly(DateTimeOffset t, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Precise timestamp with seconds, for the footer's "senast avläst" line -- the one place a raw clock reading (not a rounded point in time) is wanted.</summary>
    public static string ClockWithSeconds(DateTimeOffset t, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"kl 12:16:54" today, "igår kl 22:10:03" / "12 okt kl ..." otherwise: the footer's last-read time never hides which day it was.</summary>
    public static string ClockWithSecondsAndDay(DateTimeOffset t, DateTimeOffset now, TimeZoneInfo tz) =>
        $"{DayPrefix(t, now, tz)}kl {ClockWithSeconds(t, tz)}";

    /// <summary>
    /// "A reset always states both when and how long until": "kl 13:20 (om 37 min)" /
    /// "fre kl 07:00 (om 6 d 18 h)". forceWeekday is set for the weekly window,
    /// which always carries its weekday even when the reset falls today or tomorrow.
    /// A reset at or before now (never a real future countdown -- a race between the
    /// clock and the next poll, or a window genuinely awaiting its next observation)
    /// collapses to "nu" rather than a stale clock time paired with "(om 0 s)" or a
    /// negative duration.
    /// </summary>
    public static string Reset(DateTimeOffset resetsAt, DateTimeOffset now, TimeZoneInfo tz, bool forceWeekday)
    {
        TimeSpan span = resetsAt - now;
        if (span.TotalSeconds <= 0) return "nu";
        string clock = forceWeekday ? $"{WeekdayOrDate(resetsAt, now, tz)} kl {ClockOnly(resetsAt, tz)}" : PointInTime(resetsAt, now, tz);
        return $"{clock} (om {Duration(span)})";
    }

    /// <summary>
    /// The "om &lt;duration&gt;" fragment for compositions that build their own reset-header
    /// shape rather than going through Reset/Depletion (Ui/PanelText.cs's per-window
    /// section header, "återställs om ... · kl ..."): "nu" when span is zero or
    /// negative, never "om 0 s" or a negative duration (docs/panel-v2.md, "Time text").
    /// </summary>
    public static string CountdownFragment(TimeSpan span) => span.TotalSeconds <= 0 ? "nu" : $"om {Duration(span)}";

    /// <summary>
    /// "Running out always states both when and how long until", regardless of day
    /// (docs/panel-v2.md, "Time text"): "kl 15:01 (om 1 h 43 min)" today, "sön kl
    /// 12:09 (om 1 d 22 h)" later -- the depletion-specific sibling of Reset, used
    /// wherever a "tar slut"/"slut" moment is shown (status box, Kvot-bar label,
    /// secondary line). Never today/tomorrow-aware like PointInTime -- only today
    /// vs. a later day, per the doc's two literal examples. Same "nu" collapse as
    /// Reset for a depletion at or before now.
    /// </summary>
    public static string Depletion(DateTimeOffset dep, DateTimeOffset now, TimeZoneInfo tz)
    {
        TimeSpan span = dep - now;
        if (span.TotalSeconds <= 0) return "nu";
        string clock = IsToday(dep, now, tz) ? $"kl {ClockOnly(dep, tz)}" : $"{WeekdayOrDate(dep, now, tz)} kl {ClockOnly(dep, tz)}";
        return $"{clock} (om {Duration(span)})";
    }
}
