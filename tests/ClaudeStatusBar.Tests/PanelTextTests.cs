using System.Text.RegularExpressions;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Locks PanelText.Compose's exact wording per state, per docs/panel-v2.md's status-box
/// table. Every QuotaView here is built from real WindowView values (not asserted text),
/// mirroring how DemoQuotaSource builds its states through ForecastMath/RawStateClassifier.
/// </summary>
public class PanelTextTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test/+1", TimeSpan.FromHours(1), "Test/+1", "Test/+1");
    static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // Friday 11:00 local

    static readonly Regex RawMinutes = new(@"\b([6-9][0-9]|[1-9][0-9]{2,}) min\b");

    static WindowView Verdict(WindowKind kind, double windowMinutes, double usedPct, double ratePctPerMin, double minutesToReset)
    {
        double elapsed = windowMinutes - minutesToReset;
        ForecastResult f = ForecastMath.Evaluate(usedPct, elapsed, ratePctPerMin, windowMinutes, minutesToReset);
        QuotaState state = RawStateClassifier.Classify(usedPct, f, windowMinutes, refused: false);
        DateTimeOffset resetsAt = Now.AddMinutes(minutesToReset);
        DateTimeOffset? depletesAt = !double.IsPositiveInfinity(f.MinutesToDeplete) ? Now.AddMinutes(f.MinutesToDeplete) : null;
        return new WindowView(kind, windowMinutes, usedPct, resetsAt, state,
            f.RatePctPerMin, f.Pace, f.ProjectedPctAtReset, depletesAt, TimeSpan.FromMinutes(f.ShortfallMinutes), MeasuringReason: null);
    }

    static WindowView Measuring(WindowKind kind, double windowMinutes, double usedPct, double minutesToReset, string reason) =>
        new(kind, windowMinutes, usedPct, Now.AddMinutes(minutesToReset), QuotaState.Measuring,
            null, null, null, null, TimeSpan.Zero, reason);

    static QuotaView View(WindowView session, WindowView weekly, DateTimeOffset? blockedUntil = null, Freshness freshness = Freshness.Live, DateTimeOffset? lastChangedAt = null, DateTimeOffset? lastPollAt = null) =>
        new(session, weekly, freshness, lastChangedAt ?? Now.AddSeconds(-10), lastPollAt ?? Now.AddSeconds(-2),
            TimeSpan.FromSeconds(30), Error: null, (QuotaState)Math.Max((int)session.State, (int)weekly.State), blockedUntil);

    // ---- Safe ----

    [Fact]
    public void Safe_SessionDriven()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 20.0, ratePctPerMin: 0.15, minutesToReset: 61);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);
        Assert.Equal(QuotaState.Safe, session.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.Equal("✓ Kvoten räcker till reset", box.Line1);
        Assert.StartsWith("Sessionen landar på ~", box.Line2);
        Assert.Contains("kl 12:01 (om 1 h 1 min)", box.Line2);
        Assert.Equal(PanelColorRole.Safe, box.Role);
        Assert.Null(box.Line3);
        AssertNoRawMinutes(box);
    }

    // ---- Tight ----

    [Fact]
    public void Tight_SessionDriven()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 78.0, ratePctPerMin: 0.30, minutesToReset: 60);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 60.0, ratePctPerMin: 0.025, minutesToReset: 1200);
        Assert.Equal(QuotaState.Tight, session.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.Equal("Tajt — kvoten räcker precis", box.Line1);
        Assert.StartsWith("Sessionen landar på ~", box.Line2);
        Assert.Contains("kl 12:00 (om 1 h)", box.Line2); // reset in exactly 60 min -> "1 h" (drops "0 min")
        Assert.Equal(PanelColorRole.Tight, box.Role);
        AssertNoRawMinutes(box);
    }

    // ---- DryEarly, session-driven (docs/panel-v2.md's worked example: P=56, E=132, t_reset=168, r=0.424) ----

    [Fact]
    public void DryEarly_SessionDriven_TodayStillStatesHowLongUntil()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);
        Assert.Equal(QuotaState.DryEarly, session.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        // "Running out always states both when and how long until", today included
        // (docs/panel-v2.md, "Time text") -- no weekday today, but the "(om ...)"
        // duration is present regardless of day.
        Assert.StartsWith("⚠ Kvoten tar slut kl ", box.Line1);
        Assert.Matches(@"^⚠ Kvoten tar slut kl \d{2}:\d{2} \(om .+\)$", box.Line1);
        Assert.Contains("före reset kl ", box.Line2);
        Assert.Contains(", om du fortsätter i samma takt", box.Line2);
        Assert.Equal(PanelColorRole.Crit, box.Role);
        AssertNoRawMinutes(box);
    }

    [Fact]
    public void DryEarly_Today_KvotBarLabelAlsoStatesHowLongUntil()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);
        Assert.Equal(QuotaState.DryEarly, session.State);

        WindowSectionText section = PanelText.Compose(View(session, weekly), Now, Tz).Session;

        Assert.Matches(@"^56 % använt → slut kl \d{2}:\d{2} \(om .+\)$", section.KvotLabel);
    }

    [Fact]
    public void DryEarly_Today_SecondaryLineAlsoStatesHowLongUntil()
    {
        // Session Spent (drives, BlockedUntil path) with weekly DryEarly, depleting later
        // today -- exercises the secondary "Dessutom: ... tar slut ..." line's own today case.
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 101);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 90.0, ratePctPerMin: 0.02, minutesToReset: 5000);
        Assert.Equal(QuotaState.Spent, session.State);
        Assert.Equal(QuotaState.DryEarly, weekly.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly, blockedUntil: session.ResetsAt), Now, Tz).StatusBox;

        Assert.NotNull(box.SecondaryLine);
        Assert.Matches(@"^Dessutom: Veckokvoten tar slut kl \d{2}:\d{2} \(om .+\)$", box.SecondaryLine);
    }

    // ---- DryEarly, weekly-driven: depletion ~1 d 22 h out, well before a multi-day-away reset ----

    [Fact]
    public void DryEarly_WeeklyDriven_NotTodayHasWeekdayAndDuration()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 20.0, ratePctPerMin: 0.15, minutesToReset: 250);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 55.0, ratePctPerMin: 0.016, minutesToReset: 7000);
        Assert.Equal(QuotaState.DryEarly, weekly.State);
        Assert.True((int)weekly.State > (int)session.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.StartsWith("⚠ Veckokvoten tar slut ", box.Line1);
        Assert.Matches(@"(sön|mån|tis|ons|tor|fre|lör) kl \d{2}:\d{2} \(om .+\)$", box.Line1);
        Assert.Contains("före reset ", box.Line2);
        Assert.Matches(@"(sön|mån|tis|ons|tor|fre|lör) kl \d{2}:\d{2}, om du fortsätter i samma takt$", box.Line2);
        Assert.Equal(PanelColorRole.Crit, box.Role);
        AssertNoRawMinutes(box);
    }

    // ---- Spent ----

    [Fact]
    public void Spent_SessionDriven()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 101);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);
        Assert.Equal(QuotaState.Spent, session.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly, blockedUntil: session.ResetsAt), Now, Tz).StatusBox;

        Assert.Equal("Kvoten är slut", box.Line1);
        Assert.Equal("Öppnar igen kl 12:41 (om 1 h 41 min)", box.Line2);
        Assert.Equal(PanelColorRole.Dead, box.Role);
        AssertNoRawMinutes(box);
    }

    // ---- Measuring (generic) ----

    [Fact]
    public void Measuring_Generic()
    {
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 3.0, minutesToReset: 292, "För tidigt att mäta takt…");
        WindowView weekly = Measuring(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 1.5, minutesToReset: 10_075, "För tidigt att mäta takt…");

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.Equal("Mäter takt…", box.Line1);
        Assert.Equal("3 % använt · återställs kl 15:52 (om 4 h 52 min)", box.Line2);
        Assert.Equal(PanelColorRole.Measuring, box.Role);
        AssertNoRawMinutes(box);
    }

    // ---- Measuring, "För tidigt i veckan" (weekly reason surfaced by name) ----

    [Fact]
    public void Measuring_TooEarlyInWeek_SurfacesWeeklyReasonByName()
    {
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 2.0, minutesToReset: 298, "För tidigt att mäta takt…");
        WindowView weekly = Measuring(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 1.5, minutesToReset: 10_075, "För tidigt i veckan — väntar på ett helt dygn");

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.Equal("Mäter takt…", box.Line1);
        Assert.Contains("För tidigt i veckan — väntar på ett helt dygn", box.Line2);
        Assert.Contains("återställs ", box.Line2);
        Assert.NotEqual("3 % använt · återställs kl 15:52 (om 4 h 52 min)", box.Line2); // distinct from the generic Measuring row
        AssertNoRawMinutes(box);
    }

    // ---- Measuring, awaiting reset (window boundary already passed; nothing sane to count down to) ----

    [Fact]
    public void Measuring_AwaitingReset_ReasonStandsAloneWithoutAStaleResetClause()
    {
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 95.0, minutesToReset: -2, "Nytt fönster väntas");
        WindowView weekly = Measuring(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 40.0, minutesToReset: 5000, "För tidigt att mäta takt…");

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        // The session window carries the "special" reason here (AwaitingReset), so it --
        // not weekly -- drives the row, and its reset (already in the past) is dropped
        // rather than shown as a nonsensical negative countdown.
        Assert.Equal("Mäter takt…", box.Line1);
        Assert.Equal("Nytt fönster väntas", box.Line2);
        AssertNoRawMinutes(box);
    }

    // ---- Stale: verdict unchanged, Line3 added ----

    [Fact]
    public void Stale_AddsLine3_KeepsUnderlyingVerdict()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 20.0, ratePctPerMin: 0.15, minutesToReset: 250);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        StatusBoxText box = PanelText.Compose(
            View(session, weekly, freshness: Freshness.Stale, lastChangedAt: Now.AddMinutes(-23)), Now, Tz).StatusBox;

        Assert.Equal("✓ Kvoten räcker till reset", box.Line1);
        Assert.Equal("Datan kan vara inaktuell — senast ändrad för 23 min sedan", box.Line3);
        AssertNoRawMinutes(box);
    }

    // ---- Unknown ----

    [Fact]
    public void Unknown_NoReset_ShowsLastPollInstead()
    {
        QuotaView view = new(
            WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
            WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
            Freshness.Unknown, LastChangedAt: null, LastPollAt: new DateTimeOffset(2026, 9, 11, 11, 10, 0, TimeSpan.Zero),
            PollInterval: TimeSpan.FromSeconds(600), Error: "get_usage svarar inte", IconSeverity: QuotaState.Measuring, BlockedUntil: null);

        StatusBoxText box = PanelText.Compose(view, Now, Tz).StatusBox;

        Assert.Equal("Kan inte läsa kvoten", box.Line1);
        Assert.Equal("Senast avläst kl 12:10 · försöker igen", box.Line2);
        Assert.Equal(PanelColorRole.Unknown, box.Role);
        AssertNoRawMinutes(box);
    }

    // ---- Secondary line ----

    [Fact]
    public void SecondaryLine_AddedWhenNonDrivingWindowIsTight()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168); // DryEarly -- drives
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 80.0, ratePctPerMin: 0.05, minutesToReset: 400); // Tight, less severe -- secondary
        Assert.True((int)session.State > (int)weekly.State);
        Assert.Equal(QuotaState.Tight, weekly.State);

        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;

        Assert.NotNull(box.SecondaryLine);
        Assert.Contains("Veckokvoten", box.SecondaryLine);
    }

    [Fact]
    public void SecondaryLine_AbsentWhenNonDrivingWindowIsSafe()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 78.0, ratePctPerMin: 0.30, minutesToReset: 60); // Tight
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500); // Safe
        StatusBoxText box = PanelText.Compose(View(session, weekly), Now, Tz).StatusBox;
        Assert.Null(box.SecondaryLine);
    }

    // ---- Window sections ----

    [Fact]
    public void WindowSection_TidAndKvotLabels_SafeSession()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 45.0, ratePctPerMin: 0.02, minutesToReset: 61); // 80% elapsed
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        WindowSectionText section = PanelText.Compose(View(session, weekly), Now, Tz).Session;

        Assert.Equal("återställs om 1 h 1 min · kl 12:01", section.ResetHeader);
        Assert.Equal("80 % av 5 h har gått", section.TidLabel);
        Assert.StartsWith("45 % använt → ~", section.KvotLabel);
        Assert.Contains("vid reset", section.KvotLabel);
    }

    [Fact]
    public void WindowSection_Weekly_UsesVeckanWordAndWeekdayHeader()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 20.0, ratePctPerMin: 0.05, minutesToReset: 250);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        WindowSectionText section = PanelText.Compose(View(session, weekly), Now, Tz).Weekly;

        Assert.Contains("av veckan har gått", section.TidLabel);
        Assert.Matches(@"^återställs om .+ · (sön|mån|tis|ons|tor|fre|lör) \d{2}:\d{2}$", section.ResetHeader);
    }

    [Fact]
    public void WindowSection_Spent_KvotLabelIsFixed()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 45);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        WindowSectionText section = PanelText.Compose(View(session, weekly, blockedUntil: session.ResetsAt), Now, Tz).Session;

        Assert.Equal("100 % · slut", section.KvotLabel);
    }

    [Fact]
    public void WindowSection_AwaitingReset_KvotLabelIsReasonAlone()
    {
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 95.0, minutesToReset: -2, "Nytt fönster väntas");
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        WindowSectionText section = PanelText.Compose(View(session, weekly), Now, Tz).Session;

        Assert.Equal("Nytt fönster väntas", section.KvotLabel);
    }

    // ---- Window section, awaiting reset: header/Tid/Kvot all replaced, never a 0/negative countdown (docs/panel-v2.md, item 2) ----

    [Fact]
    public void WindowSection_AwaitingReset_HeaderAndTidLabelReplaced_NeverAZeroCountdown()
    {
        // usedPct: 59 stands in for the OLD window's last known reading -- the window
        // section text must not lean on it at all once the reset has passed.
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 59.0, minutesToReset: -3, "Nytt fönster väntas");
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 9500);

        WindowSectionText section = PanelText.Compose(View(session, weekly), Now, Tz).Session;

        Assert.Equal("väntar på nytt fönster", section.ResetHeader);
        Assert.Equal("fönstret är slut", section.TidLabel);
        Assert.Equal("Nytt fönster väntas", section.KvotLabel);

        foreach (string text in new[] { section.Title, section.ResetHeader, section.TidLabel, section.KvotLabel })
            Assert.DoesNotContain("om 0", text);
    }

    // ---- TimeText countdown at/before now: never "om 0 s" or negative, always "nu" ----

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Reset_AtOrBeforeNow_RendersAsNu(int secondsFromNow)
    {
        DateTimeOffset resetsAt = Now.AddSeconds(secondsFromNow);
        Assert.Equal("nu", TimeText.Reset(resetsAt, Now, Tz, forceWeekday: false));
        Assert.Equal("nu", TimeText.Reset(resetsAt, Now, Tz, forceWeekday: true));
    }

    static void AssertNoRawMinutes(StatusBoxText box)
    {
        foreach (string? s in new[] { box.Line1, box.Line2, box.Line3, box.SecondaryLine })
            if (s != null) Assert.False(RawMinutes.IsMatch(s), $"raw minute count in: {s}");
    }
}
