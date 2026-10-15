namespace ClaudeStatusBar.Model;

/// <summary>How far ahead the availability timeline looks (docs/multi-account.md "Availability timeline"). Persisted as "24h" | "3d" | "7d".</summary>
public enum TimelineHorizon { Hours24, Days3, Days7 }

public static class TimelineHorizons
{
    public static TimeSpan Span(this TimelineHorizon horizon) => horizon switch
    {
        TimelineHorizon.Days3 => TimeSpan.FromDays(3),
        TimelineHorizon.Days7 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>"24 h" / "3 d" / "7 d": the chip text and the end of "de närmaste 24 h".</summary>
    public static string Label(this TimelineHorizon horizon) => horizon switch
    {
        TimelineHorizon.Days3 => "3 d",
        TimelineHorizon.Days7 => "7 d",
        _ => "24 h",
    };
}

public enum AvailabilityKind { Available, Blocked, Unknown }

/// <summary>
/// Why a segment is what it is -- the explanation the hover label gives, and what separates a CERTAIN edge
/// (a reset time the server reported) from an estimated one (a forecast run-out).
/// </summary>
public enum AvailabilityReason
{
    /// <summary>Quota left now and no run-out in sight: available from now.</summary>
    Open,
    /// <summary>Quota left now, but a forecast says it runs out where this segment ends (an estimate).</summary>
    RunsOutForecast,
    /// <summary>Available again after a reset; nothing is known about what happens after it (future sessions are not modelled).</summary>
    NoForecastAfterReset,
    /// <summary>Spent now, until the reset (certain).</summary>
    Spent,
    /// <summary>Forecast to be spent from here (an estimate), until the reset (certain).</summary>
    BlockedByForecast,
    /// <summary>Spent, with no reset time known: blocked to the end of the horizon.</summary>
    SpentResetUnknown,
    /// <summary>Not logged in, not read yet, or unreadable: nothing is known.</summary>
    UnknownAccount,
}

/// <summary>
/// One stretch of an account's timeline. `IsForecast` marks a segment whose existence or one of whose edges
/// depends on a forecast (the run-out); a reset time is never a forecast. `Window` is the quota the segment is
/// about: the quota that is spent / forecast to run out, null for Open/unknown.
/// Segments are clipped to the horizon; `FullEnd` keeps the real end of a blocked segment the horizon cut short (the
/// reset time the hover label still has to say), null when nothing was cut.
/// </summary>
public sealed record AvailabilitySegment(
    DateTimeOffset Start, DateTimeOffset End, AvailabilityKind Kind, AvailabilityReason Reason, WindowKind? Window, bool IsForecast,
    DateTimeOffset? FullEnd = null)
{
    public TimeSpan Length => End - Start;

    /// <summary>The segment's real end: where it would stop if the horizon were not in the way.</summary>
    public DateTimeOffset RealEnd => FullEnd ?? End;
    public bool Contains(DateTimeOffset t) => t >= Start && t < End;
}

public sealed record AccountAvailability(string Label, IReadOnlyList<AvailabilitySegment> Segments)
{
    public bool IsUnknown => Segments.Count > 0 && Segments.All(s => s.Kind == AvailabilityKind.Unknown);
}

/// <summary>A stretch of the horizon in which exactly the accounts with these indexes are available (an empty list is a gap).</summary>
public sealed record CoverageInterval(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<int> Available)
{
    public TimeSpan Length => End - Start;
}

/// <summary>
/// The availability model behind the timeline strip (docs/multi-account.md "Availability timeline"): for every
/// account a list of segments from now to the horizon, and across the accounts the intervals in which no account is
/// available (the gaps) and in which only some are (who covers). Pure: a function of the QuotaViews and the clock,
/// all in UTC instants, so a daylight-saving change cannot move or split anything.
/// </summary>
public sealed record AvailabilityTimeline(
    DateTimeOffset Now, DateTimeOffset HorizonEnd, IReadOnlyList<AccountAvailability> Accounts, IReadOnlyList<CoverageInterval> Coverage)
{
    /// <summary>The intervals in which no account is available (none when every account is unknown: then nothing is known about coverage).</summary>
    public IReadOnlyList<CoverageInterval> Gaps => AllUnknown ? Array.Empty<CoverageInterval>() : Coverage.Where(c => c.Available.Count == 0).ToList();

    /// <summary>Every account is unknown: there is nothing to say about coverage.</summary>
    public bool AllUnknown => Accounts.Count > 0 && Accounts.All(a => a.IsUnknown);

    public int UnknownCount => Accounts.Count(a => a.IsUnknown);

    public static AvailabilityTimeline Build(IReadOnlyList<(string Label, QuotaView View)> accounts, DateTimeOffset now, TimelineHorizon horizon) =>
        Build(accounts, now, now + horizon.Span());

    public static AvailabilityTimeline Build(IReadOnlyList<(string Label, QuotaView View)> accounts, DateTimeOffset now, DateTimeOffset horizonEnd)
    {
        var list = accounts.Select(a => new AccountAvailability(a.Label, SegmentsFor(a.View, now, horizonEnd))).ToList();
        return new AvailabilityTimeline(now, horizonEnd, list, CoverageOf(list, now, horizonEnd));
    }

    // ---- one account ----

    static bool IsUnknown(QuotaView view) => view.Loading || view.NeedsLogin || view.Freshness == Freshness.Unknown;

    /// <summary>A stretch of blocked time before it is merged with the other window's.</summary>
    readonly record struct Block(DateTimeOffset Start, DateTimeOffset End, bool StartIsForecast, bool ResetUnknown, WindowKind Cause);

    /// <summary>
    /// From now to the horizon, contiguous and in order:
    ///   * blocked from now (a spent window) or from a forecast run-out, until the binding reset -- the weekly
    ///     reset when the week is spent / forecast to run out, otherwise the session's; both windows' blocks are
    ///     merged, the later reset binds, and the reset is CERTAIN;
    ///   * available before that (up to a forecast run-out, marked as a forecast) and after it (marked "no forecast":
    ///     nothing is modelled beyond a reset);
    ///   * a window that is Measuring (including the weekly window's first 24 h) has no forecast, so it has no
    ///     forecast run-out either: only what is certain (Spent) blocks;
    ///   * a not-logged-in, loading or unreadable account is one "unknown" segment.
    /// </summary>
    public static IReadOnlyList<AvailabilitySegment> SegmentsFor(QuotaView view, DateTimeOffset now, DateTimeOffset horizonEnd)
    {
        if (horizonEnd <= now) return Array.Empty<AvailabilitySegment>();
        if (IsUnknown(view))
            return new[] { new AvailabilitySegment(now, horizonEnd, AvailabilityKind.Unknown, AvailabilityReason.UnknownAccount, null, false) };

        var blocks = new List<Block>();
        AddBlock(blocks, view.Session, WindowKind.Session, now, horizonEnd);
        AddBlock(blocks, view.Weekly, WindowKind.Weekly, now, horizonEnd);
        blocks.Sort((a, b) => a.Start.CompareTo(b.Start));

        // merge overlapping / touching blocks: the earliest start (and whether that start is a forecast, and which quota causes it),
        // the latest end (the binding reset, which is certain)
        var merged = new List<Block>();
        foreach (Block b in blocks)
        {
            if (merged.Count > 0 && b.Start <= merged[^1].End)
            {
                Block last = merged[^1];
                if (b.End > last.End)
                    merged[^1] = last with
                    {
                        End = b.End,
                        ResetUnknown = b.ResetUnknown,
                        Cause = b.Start == last.Start ? b.Cause : last.Cause,   // blocked from the same instant: the quota that binds is the cause
                    };
            }
            else merged.Add(b);
        }

        var segments = new List<AvailabilitySegment>();
        DateTimeOffset cursor = now;
        bool afterReset = false;
        foreach (Block m in merged)
        {
            DateTimeOffset start = Max(m.Start, now), end = Min(m.End, horizonEnd);
            if (start >= horizonEnd) break;
            if (start > cursor)
            {
                segments.Add(m.StartIsForecast
                    ? new AvailabilitySegment(cursor, start, AvailabilityKind.Available, AvailabilityReason.RunsOutForecast, m.Cause, IsForecast: true)
                    : new AvailabilitySegment(cursor, start, AvailabilityKind.Available, afterReset ? AvailabilityReason.NoForecastAfterReset : AvailabilityReason.Open, null, false));
            }
            if (end > start)
            {
                AvailabilityReason reason = m.ResetUnknown ? AvailabilityReason.SpentResetUnknown
                    : m.StartIsForecast ? AvailabilityReason.BlockedByForecast : AvailabilityReason.Spent;
                segments.Add(new AvailabilitySegment(start, end, AvailabilityKind.Blocked, reason, m.Cause, m.StartIsForecast,
                    FullEnd: m.End > end && !m.ResetUnknown ? m.End : null));
            }
            cursor = Max(cursor, end);
            afterReset = true;
        }
        if (cursor < horizonEnd)
            segments.Add(new AvailabilitySegment(cursor, horizonEnd, AvailabilityKind.Available,
                afterReset ? AvailabilityReason.NoForecastAfterReset : AvailabilityReason.Open, null, false));
        return segments;
    }

    static void AddBlock(List<Block> blocks, WindowView w, WindowKind kind, DateTimeOffset now, DateTimeOffset horizonEnd)
    {
        switch (w.State)
        {
            case QuotaState.Spent:
                if (w.ResetsAt is not { } reset) blocks.Add(new Block(now, horizonEnd, StartIsForecast: false, ResetUnknown: true, kind));
                else if (reset > now) blocks.Add(new Block(now, reset, StartIsForecast: false, ResetUnknown: false, kind));
                break;

            // The forecast run-out, from the same place the status box reads it (WindowView.ForecastRunOutAt): only while the
            // window has a forecast at all (Measuring carries none) and only when it runs out BEFORE its own reset -- a
            // Tight window that lasts to its reset ("räcker precis") has no run-out here, one with a shortfall has.
            case QuotaState.Tight or QuotaState.DryEarly:
                if (w.ForecastRunOutAt is { } dep && w.ResetsAt is { } rst && rst > now)
                    blocks.Add(new Block(Max(dep, now), rst, StartIsForecast: true, ResetUnknown: false, kind));
                break;
        }
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    // ---- across the accounts ----

    /// <summary>
    /// Cuts the horizon at every segment boundary and lists, for each piece, the accounts that are available in
    /// it; neighbouring pieces with the same set are merged. A boundary where one account's availability ends exactly
    /// when another's begins (touching) leaves no gap; overlapping stretches simply list both.
    /// </summary>
    static IReadOnlyList<CoverageInterval> CoverageOf(IReadOnlyList<AccountAvailability> accounts, DateTimeOffset now, DateTimeOffset horizonEnd)
    {
        if (horizonEnd <= now) return Array.Empty<CoverageInterval>();

        var cuts = new SortedSet<DateTimeOffset> { now, horizonEnd };
        foreach (AccountAvailability a in accounts)
            foreach (AvailabilitySegment s in a.Segments)
            {
                if (s.Start > now && s.Start < horizonEnd) cuts.Add(s.Start);
                if (s.End > now && s.End < horizonEnd) cuts.Add(s.End);
            }

        var result = new List<CoverageInterval>();
        DateTimeOffset[] points = cuts.ToArray();
        for (int i = 0; i + 1 < points.Length; i++)
        {
            DateTimeOffset a = points[i], b = points[i + 1];
            var available = new List<int>();
            for (int k = 0; k < accounts.Count; k++)
                if (accounts[k].Segments.Any(s => s.Kind == AvailabilityKind.Available && s.Start <= a && s.End >= b)) available.Add(k);

            if (result.Count > 0 && result[^1].Available.SequenceEqual(available))
                result[^1] = result[^1] with { End = b };
            else
                result.Add(new CoverageInterval(a, b, available));
        }
        return result;
    }
}
