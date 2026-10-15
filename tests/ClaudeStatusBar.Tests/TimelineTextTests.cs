using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>docs/multi-account.md "Availability timeline": the answer line, the axis ticks and the hover labels.</summary>
public class TimelineTextTests
{
    // Tuesday 2026-10-13 10:00 UTC; with UTC as the zone every clock reads the same as the instant.
    static readonly DateTimeOffset Now = TimelineViews.Now;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static AvailabilityTimeline Timeline(TimelineHorizon horizon, params (string Label, QuotaView View)[] accounts) =>
        AvailabilityTimeline.Build(accounts, Now, horizon);

    static string Answer(TimelineHorizon horizon, params (string Label, QuotaView View)[] accounts) =>
        TimelineText.Answer(Timeline(horizon, accounts), horizon, Utc);

    static readonly TimeSpan H = TimeSpan.FromHours(1);

    // ---- the answer line ----

    [Fact]
    public void NoGap_SaysSoForTheHorizon()
    {
        Assert.Equal("Ingen lucka de närmaste 24 h", Answer(TimelineHorizon.Hours24, ("A", TimelineViews.Open()), ("B", TimelineViews.Open())));
        Assert.Equal("Ingen lucka de närmaste 3 d", Answer(TimelineHorizon.Days3, ("A", TimelineViews.Open()), ("B", TimelineViews.Open())));
        Assert.Equal("Ingen lucka de närmaste 7 d", Answer(TimelineHorizon.Days7, ("A", TimelineViews.Open())));
    }

    [Fact]
    public void AGap_NamesWhenItStartsAndEnds_HowLongItIs_AndThatNoAccountIsAvailable()
    {
        // today 16:00-20:00 (A runs out at 16:00, B is blocked until 20:00; C spent for a day)
        string text = Answer(TimelineHorizon.Hours24,
            ("A", TimelineViews.WeeklyRunsOut(6 * H, TimeSpan.FromDays(3))),
            ("B", TimelineViews.WeeklyRunsOut(6 * H, 10 * H)),
            ("C", TimelineViews.SpentWeekly(30 * H)));

        Assert.Equal("Lucka 16:00–20:00 (4 h) – inget konto tillgängligt", text);
    }

    [Fact]
    public void AGapTomorrow_IsDayAwareAtItsStart_AndTheEndNeedsNoDayWhenItIsTheSameDay()
    {
        // A runs out in 14 h (midnight), B reopens in 22 h (08:00 tomorrow): the gap is tomorrow 00:00-08:00
        string text = Answer(TimelineHorizon.Days3,
            ("A", TimelineViews.WeeklyRunsOut(14 * H, TimeSpan.FromDays(3))),
            ("B", TimelineViews.SpentWeekly(22 * H)));

        Assert.Equal("Lucka i morgon 00:00–08:00 (8 h) – inget konto tillgängligt", text);
    }
    [Fact]
    public void AGapThatStartsNow_SaysNu()
    {
        string text = Answer(TimelineHorizon.Hours24,
            ("A", TimelineViews.SpentWeekly(20 * H)),
            ("B", TimelineViews.SpentSession(2 * H)));

        Assert.Equal("Lucka nu–12:00 (2 h) – inget konto tillgängligt", text);
    }

    [Fact]
    public void AGapThatRunsToTheEndOfTheHorizon_SaysFromWhenAndAtLeastHowLong()
    {
        string text = Answer(TimelineHorizon.Hours24,
            ("A", TimelineViews.WeeklyRunsOut(18 * H, TimeSpan.FromDays(3))),
            ("B", TimelineViews.SpentWeekly(TimeSpan.FromDays(2))));

        Assert.Equal("Lucka från i morgon 04:00 – inget konto tillgängligt (minst 6 h)", text);
    }

    [Fact]
    public void EveryoneBlockedForTheWholeHorizon_SaysNoAccountIsAvailableNow()
    {
        string text = Answer(TimelineHorizon.Hours24, ("A", TimelineViews.SpentWeekly(TimeSpan.FromDays(2))), ("B", TimelineViews.SpentWeekly(TimeSpan.FromDays(3))));

        Assert.Equal("Inget konto tillgängligt de närmaste 24 h", text);
    }

    [Fact]
    public void OnlyOneAccountCoveringAStretch_SaysWhoCoversWhen()
    {
        // the real case: A runs out at 22:00, C is spent until 06:00 tomorrow, B is available throughout
        string text = Answer(TimelineHorizon.Hours24,
            ("Personligt", TimelineViews.WeeklyRunsOut(12 * H, TimeSpan.FromDays(2))),
            ("Team", TimelineViews.Open()),
            ("Enterprise", TimelineViews.SpentWeekly(20 * H)));

        Assert.Equal("Ingen lucka – Team täcker 22:00–06:00", text);
    }

    [Fact]
    public void OneAccountCoveringTheWholeHorizon_SaysSo()
    {
        string text = Answer(TimelineHorizon.Hours24,
            ("Team", TimelineViews.Open()),
            ("Enterprise", TimelineViews.SpentWeekly(TimeSpan.FromDays(2))));

        Assert.Equal("Ingen lucka – Team täcker hela 24 h", text);
    }

    [Fact]
    public void MoreThanOneGap_SaysHowManyMore()
    {
        // both accounts run out of their session at +2 h until +3 h and of the week at +10 h until +12 h: two gaps
        QuotaView twoBlocks = TimelineViews.View(
            TimelineViews.RunsOut(WindowKind.Session, 2 * H, 3 * H),
            TimelineViews.RunsOut(WindowKind.Weekly, 10 * H, 12 * H));

        string text = Answer(TimelineHorizon.Hours24, ("A", twoBlocks), ("B", twoBlocks));

        Assert.Equal("Lucka 12:00–13:00 (1 h) – inget konto tillgängligt (+1 till)", text);
    }

    [Fact]
    public void UnknownAccounts_AreMentioned_AndAllUnknownSaysSo()
    {
        QuotaView unknown = TimelineViews.Open() with { Freshness = Freshness.Unknown, NeedsLogin = true };

        Assert.Equal("Ingen lucka – A täcker hela 24 h · 1 konto är okänt", Answer(TimelineHorizon.Hours24, ("A", TimelineViews.Open()), ("B", unknown)));
        Assert.Equal("Ingen lucka de närmaste 24 h · 2 konton är okända",
            Answer(TimelineHorizon.Hours24, ("A", unknown), ("B", unknown), ("C", TimelineViews.Open()), ("D", TimelineViews.Open())));
        Assert.Equal("Tillgängligheten är okänd", Answer(TimelineHorizon.Hours24, ("A", unknown), ("B", unknown)));
    }
    [Fact]
    public void NoAccounts_NoAnswer()
    {
        Assert.Equal("", TimelineText.Answer(AvailabilityTimeline.Build(Array.Empty<(string, QuotaView)>(), Now, TimelineHorizon.Hours24), TimelineHorizon.Hours24, Utc));
    }

    [Fact]
    public void TheGapDuration_IsRealTime_AcrossADaylightSavingChange()
    {
        // Europe/Stockholm, the night the clocks go back (2026-10-25 03:00 CEST -> 02:00 CET): 23:30 Saturday to 05:00 Sunday is
        // 5 h 30 min on the wall clock but 6 h 30 min for real.
        TimeZoneInfo stockholm = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var now = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);    // 22:00 CEST
        WindowView Weekly(QuotaState s, double resetsInH, double? depletesInH = null) => new(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, s == QuotaState.Spent ? 100 : 60,
            now.AddHours(resetsInH), s, 0.1, null, 120, depletesInH is { } d ? now.AddHours(d) : null, TimeSpan.Zero, null);
        QuotaView View(WindowView w) => new(
            new WindowView(WindowKind.Session, 300, 10, now.AddHours(3), QuotaState.Safe, 0.1, null, 30, null, TimeSpan.Zero, null), w, Freshness.Live,
            now, now, TimeSpan.FromSeconds(150), null, w.State, w.State == QuotaState.Spent ? w.ResetsAt : null);

        var t = AvailabilityTimeline.Build(new[]
        {
            ("A", View(Weekly(QuotaState.DryEarly, 70, depletesInH: 1.5))),       // out from 21:30 UTC = 23:30 CEST
            ("B", View(Weekly(QuotaState.Spent, 8))),                              // back at 04:00 UTC = 05:00 CET
        }, now, TimelineHorizon.Hours24);

        Assert.Equal("Lucka 23:30–05:00 (6 h 30 min) – inget konto tillgängligt", TimelineText.Answer(t, TimelineHorizon.Hours24, stockholm));
    }

    // ---- the axis ----

    [Fact]
    public void Ticks24h_AreEveryThirdLocalHour_LabelledWithTheHour_AndTheDayAtMidnight()
    {
        var ticks = TimelineText.Ticks(Now, Now.AddHours(24), Utc);

        Assert.Equal(new[] { "12", "15", "18", "21", "i morgon", "03", "06", "09" }, ticks.Select(t => t.Label).ToArray());
        Assert.Equal(new[] { false, false, false, false, true, false, false, false }, ticks.Select(t => t.Major).ToArray());
        Assert.All(ticks, t => Assert.True(t.At > Now && t.At < Now.AddHours(24)));
    }

    [Fact]
    public void Ticks3d_AndSevenDays_AreOnePerLocalMidnight_WithDayAwareLabels()
    {
        var three = TimelineText.Ticks(Now, Now.AddDays(3), Utc);
        var seven = TimelineText.Ticks(Now, Now.AddDays(7), Utc);

        Assert.Equal(new[] { "i morgon", "tor", "fre" }, three.Select(t => t.Label).ToArray());      // Tuesday now: tomorrow, then Thursday, Friday
        Assert.Equal(new[] { "i morgon", "tor", "fre", "lör", "sön", "mån", "20 okt" }, seven.Select(t => t.Label).ToArray());   // a week away the weekday would be ambiguous: the date
        Assert.All(seven, t => Assert.True(t.Major));
        Assert.All(seven, t => Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(t.At, Utc).TimeOfDay));
    }

    [Fact]
    public void Ticks_StayOnLocalHoursAndMidnights_AcrossADaylightSavingChange()
    {
        TimeZoneInfo stockholm = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var now = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);

        var hourly = TimelineText.Ticks(now, now.AddHours(24), stockholm);
        foreach (var tick in hourly)
        {
            DateTimeOffset local = TimeZoneInfo.ConvertTime(tick.At, stockholm);
            Assert.Equal(0, local.Minute);
            Assert.Equal(0, local.Hour % 3);
        }
        Assert.Equal(hourly.Select(t => t.At).OrderBy(t => t).ToArray(), hourly.Select(t => t.At).ToArray());
        Assert.Equal(hourly.Count, hourly.Select(t => t.At).Distinct().Count());

        var daily = TimelineText.Ticks(now, now.AddDays(3), stockholm);
        Assert.All(daily, t => Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(t.At, stockholm).TimeOfDay));
        Assert.Equal(3, daily.Count);                                    // the 25 h day does not add or drop a midnight
    }

    // ---- the hover labels ----

    [Fact]
    public void SegmentLabels_SayFromTo_AndWhy()
    {
        var t = AvailabilityTimeline.Build(new[]
        {
            ("Personligt", TimelineViews.WeeklyRunsOut(4 * H, 20 * H)),
            ("Enterprise", TimelineViews.SpentWeekly(14 * H)),
        }, Now, TimelineHorizon.Hours24);

        AvailabilitySegment available = t.Accounts[0].Segments[0], blockedByForecast = t.Accounts[0].Segments[1], after = t.Accounts[0].Segments[2];
        AvailabilitySegment spent = t.Accounts[1].Segments[0];

        Assert.Equal("Personligt · nu–14:00", TimelineText.SegmentTitle("Personligt", available, Now, Utc));
        Assert.Equal("veckokvoten tar slut ~14:00 (prognos)", TimelineText.SegmentReason(available, Now, Utc));
        Assert.Equal("Personligt · 14:00–06:00", TimelineText.SegmentTitle("Personligt", blockedByForecast, Now, Utc));
        Assert.Equal("veckokvoten slut (prognos) · öppnar i morgon 06:00", TimelineText.SegmentReason(blockedByForecast, Now, Utc));
        Assert.Equal("ingen prognos efter reset", TimelineText.SegmentReason(after, Now, Utc));
        Assert.Equal("veckokvoten slut · öppnar i morgon 00:00", TimelineText.SegmentReason(spent, Now, Utc));
        Assert.Equal("kvot kvar", TimelineText.SegmentReason(new AvailabilitySegment(Now, Now.AddHours(24), AvailabilityKind.Available, AvailabilityReason.Open, null, false), Now, Utc));
        Assert.Contains("okänt", TimelineText.SegmentReason(new AvailabilitySegment(Now, Now.AddHours(24), AvailabilityKind.Unknown, AvailabilityReason.UnknownAccount, null, false), Now, Utc));
    }

    [Fact]
    public void ABlockedSegmentCutByTheHorizon_StillSaysWhenItOpens()
    {
        var t = AvailabilityTimeline.Build(new[] { ("A", TimelineViews.SpentWeekly(TimeSpan.FromDays(2))) }, Now, TimelineHorizon.Hours24);
        AvailabilitySegment cut = t.Accounts[0].Segments[0];

        Assert.Equal("veckokvoten slut · öppnar tor 10:00", TimelineText.SegmentReason(cut, Now, Utc));
        Assert.Equal("A · nu–tor 10:00", TimelineText.SegmentTitle("A", cut, Now, Utc));
    }

    [Fact]
    public void CoverageLabels_NameTheGap_OrWhoCovers()
    {
        var t = AvailabilityTimeline.Build(new[]
        {
            ("A", TimelineViews.WeeklyRunsOut(4 * H, TimeSpan.FromDays(3))),
            ("B", TimelineViews.SpentWeekly(10 * H)),
        }, Now, TimelineHorizon.Hours24);

        Assert.Equal("tillgängligt: A", TimelineText.CoverageReason(t.Coverage[0], t));
        Assert.Equal("Täckning · nu–14:00", TimelineText.CoverageTitle(t.Coverage[0], t, Utc));
        Assert.Equal("lucka – inget konto tillgängligt", TimelineText.CoverageReason(t.Gaps.Single(), t));

        var both = AvailabilityTimeline.Build(new[] { ("A", TimelineViews.Open()), ("B", TimelineViews.Open()) }, Now, TimelineHorizon.Hours24);
        Assert.Equal("alla konton tillgängliga", TimelineText.CoverageReason(both.Coverage[0], both));
    }
}