namespace ClaudeStatusBar.Icons;

/// <summary>
/// The short text in the middle of the exhausted tray icon (docs/forecast-and-states.md "Exhausted"):
/// how long until the quota can be used again, as "7d", "23h" or "45m" -- at most three characters,
/// because that is all a 16 px icon has room for. Pure function of a duration (never of a clock time),
/// so a DST change cannot move it.
/// </summary>
public static class CountdownText
{
    const long Minute = TimeSpan.TicksPerMinute;
    const long Hour = TimeSpan.TicksPerHour;
    const long Day = TimeSpan.TicksPerDay;

    /// <summary>
    /// A day or more: "Nd"; an hour or more: "Nh"; otherwise "Nm". Always rounded UP, so the text never
    /// promises an earlier opening than the real one (59 s left is "1m", 1 h 1 min is "2h", never "0m"), and
    /// a round-up that reaches the next unit moves to it (59 min 30 s is "1h", 23 h 30 min is "1d", not "60m"
    /// or "24h"). Nothing left (or already past) is "1m". Days are capped at 99 to keep three characters.
    /// </summary>
    public static string Format(TimeSpan remaining)
    {
        long t = remaining.Ticks;
        if (t <= 0) return "1m";

        if (t >= Day) return $"{Math.Min(CeilDiv(t, Day), 99)}d";

        if (t >= Hour)
        {
            long hours = CeilDiv(t, Hour);
            return hours >= 24 ? "1d" : $"{hours}h";
        }

        long minutes = CeilDiv(t, Minute);
        return minutes >= 60 ? "1h" : $"{minutes}m";
    }

    static long CeilDiv(long value, long unit) => (value + unit - 1) / unit;
}
