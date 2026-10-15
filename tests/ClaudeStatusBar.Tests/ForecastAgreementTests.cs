using ClaudeStatusBar.Data;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// One source of truth for "this window runs out before its reset" (WindowView.ForecastRunOutAt): the status box, the
/// other-accounts row, the tooltip and the availability timeline must agree. The case that prompted it: a TIGHT
/// weekly window projecting ~127 % -- which the real model can produce, because the state table's Tight row includes
/// a small shortfall (docs/forecast-and-states.md, row 4: `shortfall &gt; 0` below S_lo) -- read "Tajt — veckokvoten
/// räcker precis" in the status box while the timeline showed it blocked from a forecast run-out.
/// </summary>
public class ForecastAgreementTests
{
    static readonly DateTimeOffset Now = TimelineViews.Now;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly TimeSpan H = TimeSpan.FromHours(1);

    // ---- is it only demo data? No: the real classifier and the real model produce it ----

    [Fact]
    public void RealClassifier_CallsAWeeklyShortfallBelowSLoTight_EvenThoughItProjectsPastOneHundredPercent()
    {
        // 60 % used, 0.111 %/min, 600 min to the reset: depletion in 360 min, a 240 min shortfall -- below S_lo = 504 min for the week
        ForecastResult f = ForecastMath.Evaluate(usedPct: 60.0, elapsedMinutes: QuotaWindows.WeeklyMinutes - 600, ratePctPerMin: 40.0 / 360.0, QuotaWindows.WeeklyMinutes, minutesToReset: 600);

        Assert.True(f.ProjectedPctAtReset > 120);
        Assert.Equal(240.0, f.ShortfallMinutes, 3);
        Assert.Equal(QuotaState.Tight, RawStateClassifier.Classify(60.0, f, QuotaWindows.WeeklyMinutes, refused: false));
    }

    [Fact]
    public void RealModel_CanBeTightWhileProjectingARunOut_SoTheWordingHasToHandleIt()
    {
        // The full QuotaModel (tracker, forecast, hysteresis) on a steady climb whose forecast runs out ~10 min before the
        // 5 h window resets: a shortfall under S_lo (15 min for a session) is Tight, not DryEarly.
        var model = new QuotaModel();
        var t0 = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset reset = t0.AddMinutes(150);
        string Raw(DateTimeOffset i, int jitter) => i.AddTicks(jitter * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);   // the server's resets_at jitters a little on every reply
        QuotaView? view = null;
        for (int i = 0; i <= 60; i++)
        {
            DateTimeOffset now = t0.AddMinutes(i);
            double used = 30 + i * 0.5;                                       // 0.5 %/min: from 60 % at +60 min, 40 % left = 80 min; the reset is 90 min away -> 10 min short
            model.Ingest(new UsageSnapshot(used, Raw(reset, i), 10, Raw(t0.AddDays(5), i), now, "max"), now, i * 60_000L);
            view = model.Evaluate(now, i * 60_000L);
        }

        WindowView session = view!.Session;
        Assert.True(session.State == QuotaState.Tight, $"{session.State} proj={session.ProjectedPctAtReset} dep={session.DepletesAt} reset={session.ResetsAt} rate={session.RatePctPerMin} reason={session.MeasuringReason} used={session.UsedPct} short={session.Shortfall}");
        Assert.True(session.ProjectedPctAtReset > 100, $"projected {session.ProjectedPctAtReset}");
        Assert.NotNull(session.ForecastRunOutAt);

        PanelTextResult text = PanelText.Compose(view, t0.AddMinutes(60), Utc);
        Assert.DoesNotContain("räcker precis", text.StatusBox.Line1);
        Assert.Contains("tar slut", text.StatusBox.Line1);

        var t = AvailabilityTimeline.Build(new[] { ("A", view) }, t0.AddMinutes(60), TimelineHorizon.Hours24);
        Assert.Contains(t.Accounts[0].Segments, s => s.Kind == AvailabilityKind.Blocked && s.IsForecast);
    }

    // ---- ForecastRunOutAt ----

    [Fact]
    public void ForecastRunOutAt_IsTheDepletionInstant_OnlyWhenATightOrDryEarlyWindowRunsOutAtLeastAMinuteBeforeItsReset()
    {
        DateTimeOffset reset = Now.AddHours(10);

        Assert.Equal(Now.AddHours(6), TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, reset, Now.AddHours(6)).ForecastRunOutAt);
        Assert.Equal(Now.AddHours(6), TimelineViews.Window(WindowKind.Weekly, QuotaState.DryEarly, reset, Now.AddHours(6)).ForecastRunOutAt);
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, reset, reset.AddHours(2)).ForecastRunOutAt);                 // lasts: depletion after the reset
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, reset, reset.AddSeconds(-30)).ForecastRunOutAt);              // under a minute short: reaches the reset
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Safe, reset, Now.AddHours(6)).ForecastRunOutAt);
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Measuring, reset, Now.AddHours(6)).ForecastRunOutAt);
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Spent, reset, Now.AddHours(6)).ForecastRunOutAt);
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, reset, null).ForecastRunOutAt);
        Assert.Null(TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, null, Now.AddHours(6)).ForecastRunOutAt);
    }

    // ---- wording ----

    static QuotaView WeeklyTight(TimeSpan? depletesIn, TimeSpan resetsIn) => TimelineViews.View(
        TimelineViews.Safe(WindowKind.Session, 3 * H),
        TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, Now + resetsIn, depletesIn is { } d ? Now + d : null) with { ProjectedPctAtReset = 127 });

    [Fact]
    public void ATightWindowThatRunsOut_NeverSaysRaeckerPrecis_InAnySurface()
    {
        QuotaView view = WeeklyTight(6 * H, 10 * H);

        StatusBoxText box = PanelText.Compose(view, Now, Utc).StatusBox;
        Assert.Equal("Tajt — veckokvoten tar slut kl 16:00 (om 6 h)", box.Line1);
        Assert.Equal("4 h före reset tis kl 20:00, om du fortsätter i samma takt", box.Line2);
        Assert.Equal(PanelColorRole.Tight, box.Role);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(0, "A", view, Now, Utc);
        Assert.Equal("tajt — veckan tar slut 16:00", row.Line);
        Assert.Equal(PanelColorRole.Tight, row.Role);

        Assert.Equal("Tajt · slut 16:00 — 4 h före reset", IconSlot.BuildTooltip(view, null, Now, Utc));
    }

    [Fact]
    public void ATightWindowThatRunsOut_AsTheSecondaryLine_SaysWhenToo()
    {
        QuotaView view = TimelineViews.View(
            TimelineViews.RunsOut(WindowKind.Session, 60 * TimeSpan.FromMinutes(1), 3 * H),            // DryEarly session drives the box
            TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, Now + 10 * H, Now + 6 * H) with { ProjectedPctAtReset = 127 });

        string? secondary = PanelText.Compose(view, Now, Utc).StatusBox.SecondaryLine;

        Assert.NotNull(secondary);
        Assert.StartsWith("Dessutom tajt: Veckokvoten tar slut", secondary);
        Assert.DoesNotContain("räcker precis", secondary);
    }

    [Fact]
    public void ATightWindowThatLastsToItsReset_KeepsItsWording()
    {
        QuotaView view = WeeklyTight(depletesIn: 14 * H, resetsIn: 10 * H);   // depletion AFTER the reset: it lasts

        StatusBoxText box = PanelText.Compose(view, Now, Utc).StatusBox;
        Assert.Equal("Tajt — veckokvoten räcker precis", box.Line1);
        Assert.Equal("tajt — räcker precis", PanelText.ComposeOtherAccountRow(0, "A", view, Now, Utc).Line);
        Assert.StartsWith("Tajt · ~", IconSlot.BuildTooltip(view, null, Now, Utc));
        Assert.DoesNotContain(AvailabilityTimeline.SegmentsFor(view, Now, Now.AddHours(24)), s => s.IsForecast);
    }

    [Fact]
    public void ATightWindowThatReachesItsResetByUnderAMinute_IsNotARunOutAnywhere()
    {
        QuotaView view = TimelineViews.View(
            TimelineViews.Safe(WindowKind.Session, 3 * H),
            TimelineViews.Window(WindowKind.Weekly, QuotaState.Tight, Now + 10 * H, Now + 10 * H - TimeSpan.FromSeconds(20)));

        Assert.Equal("Tajt — veckokvoten räcker precis", PanelText.Compose(view, Now, Utc).StatusBox.Line1);
        Assert.DoesNotContain(AvailabilityTimeline.SegmentsFor(view, Now, Now.AddHours(24)), s => s.IsForecast);
    }

    // ---- every demo frame: the status box, the row and the timeline say the same thing ----

    public static IEnumerable<object[]> EveryDemoView()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (DemoQuotaSource.DemoState s in DemoQuotaSource.Build(now)) yield return new object[] { $"demo:{s.Key}", s.View, now };
        foreach (MultiAccountDemoSource.MultiState m in MultiAccountDemoSource.Build(now))
            foreach (MultiAccountDemoSource.DemoAccount a in m.Accounts) yield return new object[] { $"multi:{m.Key}:{a.Label}", a.View, now };
        foreach (MultiAccountDemoSource.TimelineScenario s in MultiAccountDemoSource.BuildTimelineScenarios(now))
            foreach (MultiAccountDemoSource.DemoAccount a in s.Accounts) yield return new object[] { $"timeline:{s.Key}:{a.Label}", a.View, now };
    }

    [Theory]
    [MemberData(nameof(EveryDemoView))]
    public void StatusBoxRowAndTimeline_AgreeAboutARunOut_ForEveryDemoView(string name, QuotaView view, DateTimeOffset now)
    {
        if (view.Loading || view.NeedsLogin || view.Freshness == Freshness.Unknown) return;     // no verdict, an "unknown" segment: nothing to agree about

        StatusBoxText box = PanelText.Compose(view, now, Utc).StatusBox;
        OtherAccountRow row = PanelText.ComposeOtherAccountRow(0, "A", view, now, Utc);
        IReadOnlyList<AvailabilitySegment> segments = AvailabilityTimeline.SegmentsFor(view, now, now.AddDays(7));

        DateTimeOffset?[] runOuts = { view.Session.ForecastRunOutAt, view.Weekly.ForecastRunOutAt };
        bool timelineRunsOut = segments.Any(s => s.IsForecast);

        // the timeline has a forecast run-out exactly when one of the windows has one, at that instant
        Assert.Equal(runOuts.Any(r => r is not null), timelineRunsOut);
        if (timelineRunsOut)
        {
            AvailabilitySegment blocked = segments.First(s => s.Kind == AvailabilityKind.Blocked && s.IsForecast);
            DateTimeOffset expected = runOuts.Where(r => r is not null).Select(r => r!.Value).Min();
            Assert.Equal(expected > now ? expected : now, blocked.Start);
        }

        // what the status box says about the DRIVING window, which is the one it is about
        WindowView driver = (int)view.Session.State >= (int)view.Weekly.State ? view.Session : view.Weekly;
        if (view.BlockedUntil is null && driver.State is QuotaState.Safe or QuotaState.Tight or QuotaState.DryEarly)
        {
            bool driverRunsOut = driver.ForecastRunOutAt is not null;
            if (driver.State == QuotaState.DryEarly && !driverRunsOut) return;                  // DryEarly by "almost nothing left", depleting after the reset: the existing wording, no run-out before it
            Assert.Equal(driverRunsOut, box.Line1.Contains("tar slut"));                         // "tar slut" in the box <=> a run-out
            Assert.Equal(driverRunsOut, row.Line.Contains("tar slut"));
            if (!driverRunsOut)
            {
                Assert.Contains("räcker", box.Line1);                                            // "räcker till reset" / "räcker precis"
                // and a "landar på ~N %" never claims a projection past 100 %
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(box.Line2, @"landar på ~(\d+) %");
                if (m.Success) Assert.True(int.Parse(m.Groups[1].Value) <= 100, $"{name}: \"{box.Line2}\" projects past 100 % while the box says it lasts");
            }
        }

        // a spent account: the box says it is out, the timeline starts blocked and certain
        if (view.BlockedUntil is not null)
        {
            Assert.Contains("slut", box.Line1);
            Assert.Equal(AvailabilityKind.Blocked, segments[0].Kind);
            Assert.False(segments[0].IsForecast);
        }
    }
}
