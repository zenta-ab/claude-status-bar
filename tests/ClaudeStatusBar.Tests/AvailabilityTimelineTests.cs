using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>Hand-built views for the timeline tests: only the fields the availability model reads, relative to a chosen "now".</summary>
internal sealed class TimelineViewFactory
{
    public TimelineViewFactory(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; }

    public WindowView Window(WindowKind kind, QuotaState state, DateTimeOffset? resetsAt, DateTimeOffset? depletesAt = null) => new(
        kind, kind == WindowKind.Session ? QuotaWindows.SessionMinutes : QuotaWindows.WeeklyMinutes,
        UsedPct: state == QuotaState.Spent ? 100.0 : 50.0, resetsAt, state,
        RatePctPerMin: state == QuotaState.Measuring ? null : 0.1, PaceMultiple: null,
        ProjectedPctAtReset: state == QuotaState.Measuring ? null : 100.0, depletesAt,
        Shortfall: depletesAt is { } d && resetsAt is { } r && r > d ? r - d : TimeSpan.Zero,
        MeasuringReason: state == QuotaState.Measuring ? "Mäter takt…" : null);

    public WindowView Safe(WindowKind kind, TimeSpan resetsIn) => Window(kind, QuotaState.Safe, Now + resetsIn);
    public WindowView Spent(WindowKind kind, TimeSpan resetsIn) => Window(kind, QuotaState.Spent, Now + resetsIn);
    public WindowView RunsOut(WindowKind kind, TimeSpan depletesIn, TimeSpan resetsIn, QuotaState state = QuotaState.DryEarly) =>
        Window(kind, state, Now + resetsIn, Now + depletesIn);

    /// <summary>A Live view; BlockedUntil as the model sets it (the latest reset among spent windows).</summary>
    public QuotaView View(WindowView session, WindowView weekly, Freshness freshness = Freshness.Live, bool needsLogin = false, bool loading = false)
    {
        DateTimeOffset? blocked = null;
        if (session.State == QuotaState.Spent && session.ResetsAt is { } s) blocked = s;
        if (weekly.State == QuotaState.Spent && weekly.ResetsAt is { } w) blocked = blocked is null || w > blocked ? w : blocked;
        return new QuotaView(session, weekly, freshness, Now.AddSeconds(-20), Now.AddSeconds(-2), TimeSpan.FromSeconds(150), null,
            (QuotaState)Math.Max((int)session.State, (int)weekly.State), blocked, needsLogin, loading);
    }

    public QuotaView Open() => View(Safe(WindowKind.Session, TimeSpan.FromHours(3)), Safe(WindowKind.Weekly, TimeSpan.FromDays(4)));
    public QuotaView SpentWeekly(TimeSpan resetsIn) => View(Safe(WindowKind.Session, TimeSpan.FromHours(3)), Spent(WindowKind.Weekly, resetsIn));
    public QuotaView SpentSession(TimeSpan resetsIn) => View(Spent(WindowKind.Session, resetsIn), Safe(WindowKind.Weekly, TimeSpan.FromDays(4)));
    public QuotaView WeeklyRunsOut(TimeSpan depletesIn, TimeSpan resetsIn) =>
        View(Safe(WindowKind.Session, TimeSpan.FromHours(3)), RunsOut(WindowKind.Weekly, depletesIn, resetsIn));
}

/// <summary>The same builders at a fixed instant (Tuesday 2026-10-13 10:00 UTC), for the deterministic model and text tests.</summary>
internal static class TimelineViews
{
    static readonly TimelineViewFactory F = new(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));

    public static DateTimeOffset Now => F.Now;
    public static WindowView Window(WindowKind kind, QuotaState state, DateTimeOffset? resetsAt, DateTimeOffset? depletesAt = null) => F.Window(kind, state, resetsAt, depletesAt);
    public static WindowView Safe(WindowKind kind, TimeSpan resetsIn) => F.Safe(kind, resetsIn);
    public static WindowView Spent(WindowKind kind, TimeSpan resetsIn) => F.Spent(kind, resetsIn);
    public static WindowView RunsOut(WindowKind kind, TimeSpan depletesIn, TimeSpan resetsIn, QuotaState state = QuotaState.DryEarly) => F.RunsOut(kind, depletesIn, resetsIn, state);
    public static QuotaView View(WindowView session, WindowView weekly, Freshness freshness = Freshness.Live, bool needsLogin = false, bool loading = false) => F.View(session, weekly, freshness, needsLogin, loading);
    public static QuotaView Open() => F.Open();
    public static QuotaView SpentWeekly(TimeSpan resetsIn) => F.SpentWeekly(resetsIn);
    public static QuotaView SpentSession(TimeSpan resetsIn) => F.SpentSession(resetsIn);
    public static QuotaView WeeklyRunsOut(TimeSpan depletesIn, TimeSpan resetsIn) => F.WeeklyRunsOut(depletesIn, resetsIn);
}

/// <summary>
/// docs/multi-account.md "Availability timeline": the availability model -- per account the segments from now to
/// the horizon (available, blocked, unknown; certain or forecast), and across the accounts the gaps and who covers.
/// </summary>
public class AvailabilityTimelineTests
{
    static readonly DateTimeOffset Now = TimelineViews.Now;
    static DateTimeOffset At(double hours) => Now.AddHours(hours);
    static readonly DateTimeOffset Horizon24 = Now.AddHours(24);

    static IReadOnlyList<AvailabilitySegment> Segments(QuotaView view, double horizonHours = 24) =>
        AvailabilityTimeline.SegmentsFor(view, Now, Now.AddHours(horizonHours));

    static AvailabilityTimeline Timeline(params (string Label, QuotaView View)[] accounts) =>
        AvailabilityTimeline.Build(accounts, Now, TimelineHorizon.Hours24);

    // ---- one account ----

    [Fact]
    public void AnAccountWithQuotaLeftAndNoRunOut_IsAvailableThroughout_AsOneSegment()
    {
        var segments = Segments(TimelineViews.Open());

        AvailabilitySegment only = Assert.Single(segments);
        Assert.Equal((Now, Horizon24), (only.Start, only.End));
        Assert.Equal(AvailabilityKind.Available, only.Kind);
        Assert.Equal(AvailabilityReason.Open, only.Reason);
        Assert.False(only.IsForecast);
    }

    [Fact]
    public void SpentWeekly_IsBlockedFromNowToTheWeeklyReset_ThenAvailableWithNoForecast()
    {
        var segments = Segments(TimelineViews.SpentWeekly(TimeSpan.FromHours(8)));

        Assert.Equal(2, segments.Count);
        Assert.Equal((Now, At(8), AvailabilityKind.Blocked, AvailabilityReason.Spent, (WindowKind?)WindowKind.Weekly, false), (segments[0].Start, segments[0].End, segments[0].Kind, segments[0].Reason, segments[0].Window, segments[0].IsForecast));
        Assert.Equal((At(8), Horizon24, AvailabilityKind.Available, AvailabilityReason.NoForecastAfterReset), (segments[1].Start, segments[1].End, segments[1].Kind, segments[1].Reason));
        Assert.False(segments[1].IsForecast);
    }

    [Fact]
    public void SpentSessionOnly_IsBlockedToTheSessionReset()
    {
        var segments = Segments(TimelineViews.SpentSession(TimeSpan.FromMinutes(95)));

        Assert.Equal(Now.AddMinutes(95), segments[0].End);
        Assert.Equal(AvailabilityKind.Blocked, segments[0].Kind);
        Assert.Equal(WindowKind.Session, segments[0].Window);
        Assert.Equal(AvailabilityReason.NoForecastAfterReset, segments[1].Reason);
    }

    [Fact]
    public void SpentUntilAfterTheHorizon_IsOneBlockedSegment_ClippedButRemembersItsRealEnd()
    {
        var segments = Segments(TimelineViews.SpentWeekly(TimeSpan.FromDays(2)));

        AvailabilitySegment only = Assert.Single(segments);
        Assert.Equal(AvailabilityKind.Blocked, only.Kind);
        Assert.Equal(Horizon24, only.End);
        Assert.Equal(Now.AddDays(2), only.RealEnd);       // the reset time the hover label still has to say
    }

    [Fact]
    public void AForecastRunOut_IsAvailableUntilIt_BlockedUntilTheCertainReset_ThenNoForecast()
    {
        var segments = Segments(TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(4), TimeSpan.FromHours(10)));

        Assert.Equal(3, segments.Count);
        // available until the forecast run-out: an estimate
        Assert.Equal((Now, At(4), AvailabilityKind.Available, AvailabilityReason.RunsOutForecast, true), (segments[0].Start, segments[0].End, segments[0].Kind, segments[0].Reason, segments[0].IsForecast));
        Assert.Equal(WindowKind.Weekly, segments[0].Window);
        // blocked from it until the reset: the start is an estimate, the reset is not
        Assert.Equal((At(4), At(10), AvailabilityKind.Blocked, AvailabilityReason.BlockedByForecast, true), (segments[1].Start, segments[1].End, segments[1].Kind, segments[1].Reason, segments[1].IsForecast));
        // after the reset nothing is modelled
        Assert.Equal((At(10), Horizon24, AvailabilityKind.Available, AvailabilityReason.NoForecastAfterReset, false), (segments[2].Start, segments[2].End, segments[2].Kind, segments[2].Reason, segments[2].IsForecast));
    }

    [Fact]
    public void ASessionForecastRunOut_IsTheSameShape_WithTheSessionReset()
    {
        QuotaView view = TimelineViews.View(
            TimelineViews.RunsOut(WindowKind.Session, TimeSpan.FromMinutes(70), TimeSpan.FromMinutes(200)),
            TimelineViews.Safe(WindowKind.Weekly, TimeSpan.FromDays(4)));

        var segments = Segments(view);

        Assert.Equal(Now.AddMinutes(70), segments[0].End);
        Assert.Equal(AvailabilityReason.RunsOutForecast, segments[0].Reason);
        Assert.Equal(WindowKind.Session, segments[0].Window);
        Assert.Equal((Now.AddMinutes(70), Now.AddMinutes(200)), (segments[1].Start, segments[1].End));
        Assert.Equal(AvailabilityReason.NoForecastAfterReset, segments[2].Reason);
    }

    [Fact]
    public void ATightWindowThatLastsToItsReset_HasNoRunOut_ButOneForecastToRunOutBeforeItsResetDoes()
    {
        QuotaView lasts = TimelineViews.View(
            TimelineViews.Window(WindowKind.Session, QuotaState.Tight, Now.AddHours(2), depletesAt: Now.AddHours(3)),   // would run out AFTER its reset
            TimelineViews.Safe(WindowKind.Weekly, TimeSpan.FromDays(4)));
        QuotaView runsOut = TimelineViews.View(
            TimelineViews.Window(WindowKind.Session, QuotaState.Tight, Now.AddHours(2), depletesAt: Now.AddMinutes(100)),
            TimelineViews.Safe(WindowKind.Weekly, TimeSpan.FromDays(4)));

        Assert.Single(Segments(lasts));
        Assert.Equal(3, Segments(runsOut).Count);
    }

    [Fact]
    public void AMeasuringWindow_HasNoForecast_SoNoForecastRunOut()
    {
        // including the weekly window's first 24 h ("För tidigt i veckan"): no forecast at all, so nothing to run out
        QuotaView view = TimelineViews.View(
            TimelineViews.Safe(WindowKind.Session, TimeSpan.FromHours(3)),
            TimelineViews.Window(WindowKind.Weekly, QuotaState.Measuring, Now.AddDays(6), depletesAt: Now.AddHours(2)));   // even a stray DepletesAt is not trusted

        AvailabilitySegment only = Assert.Single(Segments(view));
        Assert.Equal(AvailabilityKind.Available, only.Kind);
    }

    [Fact]
    public void ARefusedWeeklyForecast_ThroughTheRealModel_GivesNoForecastRunOut()
    {
        // The real model in its first day of a week (docs/forecast-and-states.md "weekly refusal"): the weekly window
        // is Measuring, carries no DepletesAt, and the timeline shows the account as available.
        var model = new QuotaModel();
        var t0 = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        string Raw(DateTimeOffset i) => i.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < 6; i++)
        {
            DateTimeOffset now = t0.AddMinutes(i * 3);
            model.Ingest(new ClaudeStatusBar.Data.UsageSnapshot(10 + i * 8, Raw(t0.AddHours(4)), 40 + i * 6, Raw(t0.AddDays(6).AddHours(2)), now, "max"), now, i * 180_000L);
        }
        QuotaView view = model.Evaluate(t0.AddMinutes(18), 6 * 180_000L);
        Assert.Equal(QuotaState.Measuring, view.Weekly.State);

        var segments = AvailabilityTimeline.SegmentsFor(view, t0.AddMinutes(18), t0.AddHours(30));
        Assert.DoesNotContain(segments, s => s.Window == WindowKind.Weekly && s.IsForecast);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("needslogin")]
    [InlineData("loading")]
    public void AnUnreadableAccount_IsOneNeutralUnknownSegment(string kind)
    {
        QuotaView open = TimelineViews.Open();
        QuotaView view = kind switch
        {
            "unknown" => open with { Freshness = Freshness.Unknown },
            "needslogin" => open with { Freshness = Freshness.Unknown, NeedsLogin = true },
            _ => open with { Freshness = Freshness.Unknown, Loading = true },
        };

        AvailabilitySegment only = Assert.Single(Segments(view));
        Assert.Equal((AvailabilityKind.Unknown, AvailabilityReason.UnknownAccount), (only.Kind, only.Reason));
        Assert.Equal((Now, Horizon24), (only.Start, only.End));
    }

    [Fact]
    public void ASpentWindowWhoseResetHasPassed_BlocksNothing_AndOneWithNoResetBlocksTheWholeHorizon()
    {
        Assert.Single(Segments(TimelineViews.SpentWeekly(TimeSpan.FromMinutes(-5))));

        QuotaView noReset = TimelineViews.View(TimelineViews.Safe(WindowKind.Session, TimeSpan.FromHours(3)), TimelineViews.Window(WindowKind.Weekly, QuotaState.Spent, null));
        AvailabilitySegment only = Assert.Single(Segments(noReset));
        Assert.Equal((AvailabilityKind.Blocked, AvailabilityReason.SpentResetUnknown), (only.Kind, only.Reason));
    }

    [Fact]
    public void BothWindowsSpent_BlockedUntilTheLaterReset_WhichBinds()
    {
        QuotaView view = TimelineViews.View(TimelineViews.Spent(WindowKind.Session, TimeSpan.FromHours(2)), TimelineViews.Spent(WindowKind.Weekly, TimeSpan.FromHours(9)));

        var segments = Segments(view);

        Assert.Equal(At(9), segments[0].End);
        Assert.Equal(WindowKind.Weekly, segments[0].Window);
        Assert.Equal(AvailabilityReason.NoForecastAfterReset, segments[1].Reason);
    }

    [Fact]
    public void ASpentSession_FollowedByAWeeklyRunOut_GivesBlocked_Available_Blocked_Available()
    {
        QuotaView view = TimelineViews.View(
            TimelineViews.Spent(WindowKind.Session, TimeSpan.FromHours(2)),
            TimelineViews.RunsOut(WindowKind.Weekly, TimeSpan.FromHours(6), TimeSpan.FromHours(15)));

        var segments = Segments(view);

        Assert.Equal(new[] { AvailabilityKind.Blocked, AvailabilityKind.Available, AvailabilityKind.Blocked, AvailabilityKind.Available }, segments.Select(s => s.Kind).ToArray());
        Assert.Equal(new[] { At(0), At(2), At(6), At(15) }, segments.Select(s => s.Start).ToArray());
        Assert.False(segments[0].IsForecast);          // the spent session: certain
        Assert.True(segments[1].IsForecast);           // available 02:00-06:00: ends at the forecast run-out
        Assert.Equal(AvailabilityReason.RunsOutForecast, segments[1].Reason);
        Assert.True(segments[2].IsForecast);
    }

    [Fact]
    public void OverlappingBlocks_AreMergedIntoOne_ToTheLaterReset()
    {
        // the session is spent until +3 h, the weekly is forecast to run out at +2 h (before the session reopens)
        QuotaView view = TimelineViews.View(
            TimelineViews.Spent(WindowKind.Session, TimeSpan.FromHours(3)),
            TimelineViews.RunsOut(WindowKind.Weekly, TimeSpan.FromHours(2), TimeSpan.FromHours(12)));

        var segments = Segments(view);

        Assert.Equal(2, segments.Count);
        Assert.Equal((Now, At(12), AvailabilityKind.Blocked), (segments[0].Start, segments[0].End, segments[0].Kind));
        Assert.False(segments[0].IsForecast);            // it starts spent, now
    }

    [Fact]
    public void EverySegmentList_CoversTheHorizonExactly_WithoutHolesOrOverlaps()
    {
        var views = new[]
        {
            TimelineViews.Open(), TimelineViews.SpentWeekly(TimeSpan.FromHours(5)), TimelineViews.SpentSession(TimeSpan.FromHours(1)),
            TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(3), TimeSpan.FromHours(30)),
            TimelineViews.View(TimelineViews.Spent(WindowKind.Session, TimeSpan.FromHours(2)), TimelineViews.RunsOut(WindowKind.Weekly, TimeSpan.FromHours(6), TimeSpan.FromHours(15))),
            TimelineViews.Open() with { Freshness = Freshness.Unknown },
        };
        foreach (QuotaView view in views)
            foreach (double horizon in new[] { 24.0, 72.0, 168.0 })
            {
                var segments = Segments(view, horizon);
                Assert.Equal(Now, segments[0].Start);
                Assert.Equal(Now.AddHours(horizon), segments[^1].End);
                for (int i = 1; i < segments.Count; i++) Assert.Equal(segments[i - 1].End, segments[i].Start);
                Assert.All(segments, s => Assert.True(s.Length > TimeSpan.Zero));
            }
    }

    [Fact]
    public void TheHorizon_ClipsTheSegments_AndALongerOneShowsTheResetAndWhatComesAfter()
    {
        QuotaView view = TimelineViews.SpentWeekly(TimeSpan.FromHours(40));

        Assert.Single(Segments(view, 24));
        var seven = Segments(view, 168);
        Assert.Equal(2, seven.Count);
        Assert.Equal(At(40), seven[0].End);
        Assert.Null(seven[0].FullEnd);                   // not cut by the horizon any more
        Assert.Equal(TimeSpan.FromHours(24), TimelineHorizon.Hours24.Span());
        Assert.Equal(TimeSpan.FromDays(3), TimelineHorizon.Days3.Span());
        Assert.Equal(TimeSpan.FromDays(7), TimelineHorizon.Days7.Span());
        Assert.Equal(new[] { "24 h", "3 d", "7 d" }, new[] { TimelineHorizon.Hours24, TimelineHorizon.Days3, TimelineHorizon.Days7 }.Select(h => h.Label()).ToArray());
    }

    [Fact]
    public void ANotPositiveHorizon_HasNoSegments()
    {
        Assert.Empty(AvailabilityTimeline.SegmentsFor(TimelineViews.Open(), Now, Now));
        Assert.Empty(AvailabilityTimeline.SegmentsFor(TimelineViews.Open(), Now, Now.AddHours(-1)));
    }

    // ---- across accounts ----

    [Fact]
    public void TheRealCase_OneRunsOut_OneIsSpent_OneIsAvailableThroughout_HasNoGap_AndTheSmallerPlanCoversTheStretch()
    {
        // A: weekly forecast to run out in 13 h (until its reset in 2 d); C: spent for 20 h; B: available throughout
        var t = Timeline(
            ("A", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(13), TimeSpan.FromDays(2))),
            ("B", TimelineViews.Open()),
            ("C", TimelineViews.SpentWeekly(TimeSpan.FromHours(20))));

        Assert.Empty(t.Gaps);
        CoverageInterval sole = Assert.Single(t.Coverage, c => c.Available.Count == 1);
        Assert.Equal((At(13), At(20)), (sole.Start, sole.End));
        Assert.Equal(new[] { 1 }, sole.Available);                  // B
        Assert.Equal(new[] { 0, 1 }, t.Coverage[0].Available);      // A and B at first (C is spent)
    }

    [Fact]
    public void AGap_IsWhereNoAccountIsAvailable()
    {
        // A runs out at +4 h (blocked to +3 d); B is available until it runs out at +6 h (its reset is at +10 h); C is spent for 30 h
        var t = Timeline(
            ("A", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(4), TimeSpan.FromDays(3))),
            ("B", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(6), TimeSpan.FromHours(10))),
            ("C", TimelineViews.SpentWeekly(TimeSpan.FromHours(30))));

        CoverageInterval gap = Assert.Single(t.Gaps);
        Assert.Equal((At(6), At(10)), (gap.Start, gap.End));
        Assert.Equal(TimeSpan.FromHours(4), gap.Length);
    }

    [Fact]
    public void TouchingIntervals_LeaveNoGap_WhenOneAccountReopensExactlyWhenAnotherRunsOut()
    {
        // A is available until +5 h; B is spent until exactly +5 h
        var t = Timeline(
            ("A", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(5), TimeSpan.FromDays(3))),
            ("B", TimelineViews.SpentWeekly(TimeSpan.FromHours(5))));

        Assert.Empty(t.Gaps);
        Assert.Equal(2, t.Coverage.Count);
        Assert.Equal(new[] { 0 }, t.Coverage[0].Available);        // A until it runs out ...
        Assert.Equal(new[] { 1 }, t.Coverage[1].Available);        // ... B from the same instant
        Assert.Equal((At(5), At(5)), (t.Coverage[0].End, t.Coverage[1].Start));
    }

    [Fact]
    public void OverlappingAvailability_ListsBothAccounts_AndAdjacentEqualPiecesAreMerged()
    {
        var t = Timeline(
            ("A", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(8), TimeSpan.FromDays(3))),
            ("B", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(12), TimeSpan.FromDays(3))));

        Assert.Equal(new[] { 0, 1 }, t.Coverage[0].Available);
        Assert.Equal((Now, At(8)), (t.Coverage[0].Start, t.Coverage[0].End));
        Assert.Equal(new[] { 1 }, t.Coverage[1].Available);
        // from +12 h both are blocked: a gap to the horizon
        CoverageInterval gap = Assert.Single(t.Gaps);
        Assert.Equal((At(12), Horizon24), (gap.Start, gap.End));
        // exactly three pieces, no needless splits
        Assert.Equal(3, t.Coverage.Count);
    }

    [Fact]
    public void AllAccountsSpentNow_IsAGapFromNow_UntilTheFirstReopens()
    {
        var t = Timeline(
            ("A", TimelineViews.SpentWeekly(TimeSpan.FromHours(20))),
            ("B", TimelineViews.SpentSession(TimeSpan.FromHours(2))));

        CoverageInterval gap = Assert.Single(t.Gaps);
        Assert.Equal((Now, At(2)), (gap.Start, gap.End));
    }

    [Fact]
    public void UnknownAccounts_DoNotCover_AndAnAllUnknownTimelineHasNoGaps()
    {
        QuotaView unknown = TimelineViews.Open() with { Freshness = Freshness.Unknown, NeedsLogin = true };

        var mixed = Timeline(("A", TimelineViews.SpentWeekly(TimeSpan.FromHours(6))), ("B", unknown));
        Assert.Equal(1, mixed.UnknownCount);
        Assert.Equal((Now, At(6)), (mixed.Gaps[0].Start, mixed.Gaps[0].End));

        var allUnknown = Timeline(("A", unknown), ("B", unknown));
        Assert.True(allUnknown.AllUnknown);
        Assert.Empty(allUnknown.Gaps);
    }

    [Fact]
    public void Coverage_TilesTheHorizonExactly()
    {
        var t = Timeline(
            ("A", TimelineViews.WeeklyRunsOut(TimeSpan.FromHours(4), TimeSpan.FromDays(3))),
            ("B", TimelineViews.SpentSession(TimeSpan.FromHours(1))),
            ("C", TimelineViews.SpentWeekly(TimeSpan.FromHours(30))));

        Assert.Equal(Now, t.Coverage[0].Start);
        Assert.Equal(Horizon24, t.Coverage[^1].End);
        for (int i = 1; i < t.Coverage.Count; i++) Assert.Equal(t.Coverage[i - 1].End, t.Coverage[i].Start);
    }

    [Fact]
    public void AcrossADaylightSavingChange_EverythingIsMeasuredInRealTime()
    {
        // Europe/Stockholm falls back on 2026-10-25 at 01:00 UTC: a 24 h horizon from 20:00 UTC covers it. Instants are UTC,
        // so the 4 h gap between +6 h and +10 h is 4 real hours whatever the clocks do.
        var now = new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero);
        WindowView Weekly(QuotaState s, double resetsInH, double? depletesInH = null) => new(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, s == QuotaState.Spent ? 100 : 60,
            now.AddHours(resetsInH), s, 0.1, null, 120, depletesInH is { } d ? now.AddHours(d) : null, TimeSpan.Zero, null);
        QuotaView View(WindowView w) => new(
            new WindowView(WindowKind.Session, 300, 10, now.AddHours(3), QuotaState.Safe, 0.1, null, 30, null, TimeSpan.Zero, null), w, Freshness.Live,
            now, now, TimeSpan.FromSeconds(150), null, w.State, w.State == QuotaState.Spent ? w.ResetsAt : null);

        var t = AvailabilityTimeline.Build(new[]
        {
            ("A", View(Weekly(QuotaState.DryEarly, 70, depletesInH: 6))),
            ("B", View(Weekly(QuotaState.Spent, 10))),
        }, now, TimelineHorizon.Hours24);

        CoverageInterval gap = Assert.Single(t.Gaps);
        Assert.Equal(TimeSpan.FromHours(4), gap.Length);
        Assert.Equal((now.AddHours(6), now.AddHours(10)), (gap.Start, gap.End));
    }
}
