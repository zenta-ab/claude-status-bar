using System.Globalization;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>
/// The words of the availability timeline (docs/multi-account.md "Availability timeline"): the one-line answer
/// above the strip, the hour ticks with day-aware labels, and the hover label of a segment. Pure functions of
/// (timeline, tz) -- no drawing -- and every clock time goes through TimeText, so "i morgon", weekdays and dates
/// read the same as everywhere else in the app.
/// </summary>
public static class TimelineText
{
    public const string Title = "TILLGÄNGLIGHET";
    public const string CoverageLabel = "Täckning";
    public const string NowLabel = "nu";

    // ---- the answer line ----

    /// <summary>
    /// The answer first, one line:
    ///   "Ingen lucka de närmaste 24 h"
    ///   "Lucka fre 23:22–06:59 (7 h 37 min) – inget konto tillgängligt"
    ///   "Ingen lucka – Konto B täcker 23:22–06:59"   (only one account is available for a stretch)
    /// An empty string when there are no accounts at all.
    /// </summary>
    public static string Answer(AvailabilityTimeline t, TimelineHorizon horizon, TimeZoneInfo tz)
    {
        if (t.Accounts.Count == 0) return "";
        if (t.AllUnknown) return "Tillgängligheten är okänd";

        string unknown = t.UnknownCount switch { 0 => "", 1 => " · 1 konto är okänt", var n => $" · {n} konton är okända" };

        IReadOnlyList<CoverageInterval> gaps = t.Gaps;
        if (gaps.Count > 0)
        {
            CoverageInterval gap = gaps[0];
            bool toHorizon = gap.End >= t.HorizonEnd;
            bool fromNow = gap.Start <= t.Now;
            string more = gaps.Count > 1 ? $" (+{gaps.Count - 1} till)" : "";

            string text;
            if (toHorizon && fromNow)
                text = $"Inget konto tillgängligt de närmaste {horizon.Label()}";
            else if (toHorizon)
                text = $"Lucka från {StartText(gap.Start, t.Now, tz)} – inget konto tillgängligt (minst {TimeText.Duration(gap.End - gap.Start)})";
            else
                text = $"Lucka {Range(gap.Start, gap.End, t.Now, tz)} ({TimeText.Duration(gap.Length)}) – inget konto tillgängligt";
            return text + more + unknown;
        }

        if (t.Accounts.Count >= 2)
        {
            List<CoverageInterval> sole = t.Coverage.Where(c => c.Available.Count == 1).ToList();
            if (sole.Count > 0)
            {
                CoverageInterval first = sole[0];
                string who = t.Accounts[first.Available[0]].Label;
                bool wholeHorizon = first.Start <= t.Now && first.End >= t.HorizonEnd;
                string when = wholeHorizon ? $"hela {horizon.Label()}"
                    : first.End >= t.HorizonEnd ? $"från {StartText(first.Start, t.Now, tz)}"
                    : Range(first.Start, first.End, t.Now, tz);
                string more = sole.Count > 1 ? $" (+{sole.Count - 1} till)" : "";
                return $"Ingen lucka – {who} täcker {when}{more}{unknown}";
            }
        }

        return $"Ingen lucka de närmaste {horizon.Label()}{unknown}";
    }

    /// <summary>"nu" for a start at or before now, otherwise the clock with its day when not today.</summary>
    static string StartText(DateTimeOffset start, DateTimeOffset now, TimeZoneInfo tz) =>
        start <= now ? NowLabel : TimeText.ClockWithDay(start, now, tz);

    /// <summary>
    /// "23:22–06:59", "fre 23:22–06:59", "nu–fre 06:59": the start day-aware (or "nu"), the end with its day unless
    /// it is the same day as the start or the very next morning (an overnight stretch reads naturally without it).
    /// </summary>
    public static string Range(DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, TimeZoneInfo tz)
    {
        string from = StartText(start, now, tz);
        if (start <= now) return $"{from}–{TimeText.ClockWithDay(end, now, tz)}";

        int days = TimeText.DayDiff(end, start, tz);
        bool overnight = days == 1 && TimeZoneInfo.ConvertTime(end, tz).TimeOfDay < TimeZoneInfo.ConvertTime(start, tz).TimeOfDay;
        string to = days == 0 || overnight ? TimeText.ClockOnly(end, tz) : TimeText.ClockWithDay(end, now, tz);
        return $"{from}–{to}";
    }

    // ---- the axis ----

    /// <summary>One tick of the time axis: where, what it says (empty for an unlabelled tick), and whether it is a day boundary.</summary>
    public readonly record struct Tick(DateTimeOffset At, string Label, bool Major);

    /// <summary>
    /// Axis ticks between now and the end of the horizon, on LOCAL times (so they stay on the hour / at midnight
    /// across a daylight-saving change). Up to 36 h: every third local hour, labelled with the hour ("18") and, at
    /// midnight, with the day ("i morgon"). Longer: one tick per local midnight labelled with the day ("tor", "fre").
    /// </summary>
    public static IReadOnlyList<Tick> Ticks(DateTimeOffset now, DateTimeOffset end, TimeZoneInfo tz)
    {
        var ticks = new List<Tick>();
        if (end <= now) return ticks;

        if (end - now <= TimeSpan.FromHours(36))
        {
            // whole local hours: walk UTC hour boundaries and keep those that are local minute 0 and a multiple of 3 hours
            DateTimeOffset t = new(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
            if (t <= now) t = t.AddHours(1);
            for (; t < end; t = t.AddHours(1))
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(t, tz);
                if (local.Minute != 0 || local.Hour % 3 != 0) continue;
                bool midnight = local.Hour == 0;
                ticks.Add(new Tick(t, midnight ? TimeText.DayWord(t, now, tz) : local.Hour.ToString("00", CultureInfo.InvariantCulture), midnight));
            }
            return ticks;
        }

        DateTime nowLocal = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        for (int d = 1; d <= 60; d++)
        {
            DateTime localMidnight = nowLocal.Date.AddDays(d);
            DateTimeOffset at = LocalToUtc(localMidnight, tz);
            if (at >= end) break;
            ticks.Add(new Tick(at, TimeText.DayWord(at, now, tz), true));
        }
        return ticks;
    }

    static DateTimeOffset LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        DateTime unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);   // midnight does not exist that day (a zone that springs forward at 00:00)
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, tz), TimeSpan.Zero);
    }

    // ---- the row labels ----

    const string Ellipsis = "…";

    /// <summary>
    /// The labels as they are written on the left of the rows: a label that fits `maxWidth` is left alone; one that does
    /// not is shortened in the MIDDLE ("Max 20x (…gmail.com)"), because the distinguishing part of an account label --
    /// the address's domain, a plan suffix -- is at its END, and an end-truncated "Max 20x (alex@exam…" is the same for two
    /// accounts. Two different labels never come out the same: when they would, the tail each keeps grows (and its head
    /// shrinks, so it still fits) until they differ. `measure` is the width of a string in the font it is drawn in.
    /// </summary>
    public static IReadOnlyList<string> FitLabels(IReadOnlyList<string> labels, Func<string, float> measure, float maxWidth)
    {
        int n = labels.Count;
        var head = new int[n];
        var tail = new int[n];
        var whole = new bool[n];
        for (int i = 0; i < n; i++)
        {
            string label = labels[i] ?? "";
            whole[i] = measure(label) <= maxWidth || label.Length < 4;   // a label of three characters cannot be shortened
            if (!whole[i]) (head[i], tail[i]) = BestSplit(label, measure, maxWidth);
        }

        string Show(int i) => whole[i] ? labels[i] : Shorten(labels[i], head[i], tail[i]);

        // never the same text for two different labels: grow the tails of the ones that collide
        for (int round = 0; round < 200; round++)
        {
            var shown = Enumerable.Range(0, n).Select(Show).ToList();
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (whole[i]) continue;
                bool collides = Enumerable.Range(0, n).Any(j => j != i && labels[j] != labels[i] && shown[j] == shown[i]);
                if (!collides) continue;

                // trade a head character for a tail character while it still fits (down to a bare "…tail": the end is what tells accounts apart)
                int newHead = Math.Max(0, head[i] - 1);
                int newTail = tail[i] + 1;
                if (newHead + newTail >= labels[i].Length - 1) continue;
                if (measure(Shorten(labels[i], newHead, newTail)) > maxWidth)
                {
                    // the substituted character was wider: drop one more head character if there is one to drop
                    if (newHead <= 0) continue;
                    newHead--;
                    if (measure(Shorten(labels[i], newHead, newTail)) > maxWidth) continue;
                }
                head[i] = newHead;
                tail[i] = newTail;
                changed = true;
            }
            if (!changed) break;
        }

        return Enumerable.Range(0, n).Select(Show).ToList();
    }

    static string Shorten(string label, int head, int tail) => label[..head] + Ellipsis + label[^tail..];

    /// <summary>The longest head + tail (a little more tail than head: the end is what tells accounts apart) that fits.</summary>
    static (int Head, int Tail) BestSplit(string label, Func<string, float> measure, float maxWidth)
    {
        for (int kept = label.Length - 2; kept >= 2; kept--)
        {
            int head = Math.Max(1, (int)Math.Floor(kept * 0.4));
            int tail = kept - head;
            if (measure(Shorten(label, head, tail)) <= maxWidth) return (head, tail);
        }
        return (1, 1);
    }

    // ---- the hover label of a segment ----

    /// <summary>The first line of a segment's hover label: whose, and from-to (day-aware).</summary>
    public static string SegmentTitle(string account, AvailabilitySegment s, DateTimeOffset now, TimeZoneInfo tz) =>
        $"{account} · {Range(s.Start, s.RealEnd, now, tz)}";

    /// <summary>The second line: why. "veckokvoten slut (prognos) · öppnar fre 06:59", "ingen prognos efter reset", ...</summary>
    public static string SegmentReason(AvailabilitySegment s, DateTimeOffset now, TimeZoneInfo tz)
    {
        string quota = s.Window == WindowKind.Weekly ? "veckokvoten" : "sessionens kvot";
        string opens = $"öppnar {TimeText.ClockWithDay(s.RealEnd, now, tz)}";
        return s.Reason switch
        {
            AvailabilityReason.Open => "kvot kvar",
            AvailabilityReason.RunsOutForecast => $"{quota} tar slut ~{TimeText.ClockWithDay(s.End, now, tz)} (prognos)",
            AvailabilityReason.NoForecastAfterReset => "ingen prognos efter reset",
            AvailabilityReason.Spent => $"{quota} slut · {opens}",
            AvailabilityReason.BlockedByForecast => $"{quota} slut (prognos) · {opens}",
            AvailabilityReason.SpentResetUnknown => $"{quota} slut · återställning okänd",
            _ => "okänt – inte inloggad eller inte avläst",
        };
    }

    /// <summary>The hover label of a coverage-row interval: "Täckning · 23:22–06:59".</summary>
    public static string CoverageTitle(CoverageInterval c, AvailabilityTimeline t, TimeZoneInfo tz) =>
        $"{CoverageLabel} · {Range(c.Start, c.End, t.Now, tz)}";

    /// <summary>... and what covers it: "lucka – inget konto tillgängligt", "tillgängligt: Konto B", "alla konton tillgängliga".</summary>
    public static string CoverageReason(CoverageInterval c, AvailabilityTimeline t) =>
        c.Available.Count == 0 ? "lucka – inget konto tillgängligt"
        : c.Available.Count == t.Accounts.Count ? "alla konton tillgängliga"
        : $"tillgängligt: {string.Join(", ", c.Available.Select(i => t.Accounts[i].Label))}";
}
