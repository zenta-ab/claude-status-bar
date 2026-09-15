namespace ClaudeStatusBar.Model;

/// <summary>
/// Synthetic QuotaViews for `--demo` (see the task's "Demo mode" section). Feeds
/// the exact same render path (icon + panel) as live data, so nothing here may be
/// hand-picked in a way the real model couldn't produce -- Safe/Tight/DryEarly/
/// Spent windows are built by running the real ForecastMath + RawStateClassifier
/// over crafted inputs, not by asserting a state directly.
/// </summary>
public static class DemoQuotaSource
{
    public readonly record struct DemoState(string Key, QuotaView View);

    /// <summary>
    /// The 10 states cycled every 4s in demo mode, in the task's required order.
    /// utcNow is applied fresh each call so all the relative displays (countdowns,
    /// "senast ändrad för X sedan") stay sane no matter when demo mode started.
    /// </summary>
    public static IReadOnlyList<DemoState> Build(DateTimeOffset utcNow)
    {
        WindowView measuringSession = MeasuringWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 2.0, minutesToReset: 298, utcNow, MeasuringReasonCode.TooEarly);
        WindowView measuringWeekly = MeasuringWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 1.5, minutesToReset: 10_075, utcNow, MeasuringReasonCode.TooEarlyInWeek);

        // Awaiting reset (item 2, docs/panel-v2.md): the session's own deadline is already
        // 3 minutes behind us and no later window has been observed yet -- usedPct=59 stands
        // in for the OLD window's last known reading, which the Kvot bar must NOT draw as if
        // still current. Paired with a normal Safe weekly window (the common real shape: only
        // the session resets on this short a cycle).
        WindowView awaitingResetSession = MeasuringWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 59.0, minutesToReset: -3, utcNow, MeasuringReasonCode.AwaitingReset);

        WindowView safeSession = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 20.0, ratePctPerMin: 0.15, minutesToReset: 250, utcNow);
        // Decision 9 (round 2): E = 10080 - 8500 = 1580 min, >= the weekly 24h (1440 min)
        // minimum -- the live model would refuse anything short of that as TooEarlyInWeek, so
        // a demo "Safe weekly" window closer to reset than a full day/night cycle is a shape
        // the real model could never produce.
        WindowView safeWeekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 15.0, ratePctPerMin: 0.005, minutesToReset: 8500, utcNow);

        WindowView tightSession = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 78.0, ratePctPerMin: 0.30, minutesToReset: 60, utcNow);
        WindowView tightWeekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 60.0, ratePctPerMin: 0.025, minutesToReset: 1200, utcNow);

        // The doc's worked example: P=56, E=132, t_reset=168, r=0.424 -> shortfall 64 min.
        WindowView dryEarlySession = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168, utcNow);

        // Panel-v2's other DryEarly shape: the WEEKLY window drives, not the session one.
        // usedPct/rate are picked so depletion lands ~46-47h out (docs/panel-v2.md's
        // "(om 1 d 22 h)" example) -- TimeText's day/hour band for that range is
        // [46h, 47h), and minutesToReset leaves a multi-day shortfall well before the
        // weekly reset, so the depletion point falls on a distinct weekday, not
        // today/tomorrow (exercises panel-v2.md's "Veckokvoten tar slut {weekday}
        // (om ...)" / "{shortfall} före reset {weekday}..." composition). Paired with
        // the Safe session window so weekly (the more severe state) drives the box.
        WindowView dryEarlyWeekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 55.0, ratePctPerMin: 0.016, minutesToReset: 7000, utcNow);

        // Decision 15: Spent is now P >= 100 (not 99.5) -- confirmed exhaustion, not "almost".
        WindowView spentSessionWin = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 45, utcNow);
        WindowView spentWeeklyWin = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 100.0, ratePctPerMin: 0.05, minutesToReset: 300, utcNow);

        var list = new List<DemoState>
        {
            new("measuring", View(measuringSession, measuringWeekly, Freshness.Live, utcNow, TimeSpan.FromSeconds(150), utcNow.AddSeconds(-5))),
            // Decision 9 (round 2): the session's own deadline has already passed with no
            // later window observed yet -- the live FreshnessOracle would mark this Stale
            // (the resets_at + 60s rule) regardless of how recently the cached reply arrived.
            new("awaiting_reset", View(awaitingResetSession, safeWeekly, Freshness.Stale, utcNow, TimeSpan.FromSeconds(30), utcNow.AddSeconds(-10))),
            new("safe", View(safeSession, safeWeekly, Freshness.Live, utcNow, TimeSpan.FromSeconds(150), utcNow.AddSeconds(-20))),
            new("tight", View(tightSession, tightWeekly, Freshness.Live, utcNow, TimeSpan.FromSeconds(30), utcNow.AddSeconds(-15))),
            new("dry_early", View(dryEarlySession, safeWeekly, Freshness.Live, utcNow, TimeSpan.FromSeconds(30), utcNow.AddSeconds(-10))),
            new("dry_early_weekly", View(safeSession, dryEarlyWeekly, Freshness.Live, utcNow, TimeSpan.FromSeconds(30), utcNow.AddSeconds(-10))),
            new("spent_session", SpentView(spentSessionWin, safeWeekly, utcNow)),
            new("spent_weekly", SpentView(safeSession, spentWeeklyWin, utcNow)),
            // Reuses the Safe windows: the point of this state is to demo the Stale
            // freshness banner/header in isolation, not to also trip a Tight/DryEarly
            // verdict, which would outrank it in the banner's priority order and hide it.
            new("stale", View(safeSession, safeWeekly, Freshness.Stale, utcNow, TimeSpan.FromSeconds(150), utcNow.AddMinutes(-23))),
            new("unknown", UnknownView(utcNow)),
        };
        return list;
    }

    static QuotaView View(WindowView session, WindowView weekly, Freshness freshness, DateTimeOffset utcNow, TimeSpan pollInterval, DateTimeOffset lastChangedAt) =>
        new(session, weekly, freshness,
            LastChangedAt: lastChangedAt,
            LastPollAt: utcNow.AddSeconds(-2),
            PollInterval: pollInterval,
            Error: null,
            IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State),
            BlockedUntil: null);

    static QuotaView SpentView(WindowView session, WindowView weekly, DateTimeOffset utcNow)
    {
        DateTimeOffset? blockedUntil = null;
        if (session.State == QuotaState.Spent && session.ResetsAt is { } sr) blockedUntil = sr;
        if (weekly.State == QuotaState.Spent && weekly.ResetsAt is { } wr)
            blockedUntil = blockedUntil is null || wr > blockedUntil ? wr : blockedUntil;

        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-30),
            LastPollAt: utcNow.AddSeconds(-2),
            PollInterval: TimeSpan.FromSeconds(300),
            Error: null,
            IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State),
            BlockedUntil: blockedUntil);
    }

    static QuotaView UnknownView(DateTimeOffset utcNow) =>
        new(WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
            WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
            Freshness.Unknown,
            LastChangedAt: null,
            LastPollAt: utcNow.AddMinutes(-40),
            PollInterval: TimeSpan.FromSeconds(600),
            Error: "get_usage svarar inte",
            IconSeverity: QuotaState.Measuring,
            BlockedUntil: null);

    /// <summary>Hand-built Measuring window: no forecast field is ever non-null here, so nothing downstream needs to trust the State gate alone.</summary>
    static WindowView MeasuringWindow(WindowKind kind, double windowMinutes, double usedPct, double minutesToReset, DateTimeOffset utcNow, MeasuringReasonCode reason) =>
        new(kind, windowMinutes, usedPct, utcNow.AddMinutes(minutesToReset), QuotaState.Measuring,
            RatePctPerMin: null, PaceMultiple: null, ProjectedPctAtReset: null, DepletesAt: null,
            Shortfall: TimeSpan.Zero, MeasuringReason: ReasonText(reason));

    /// <summary>Runs the real forecast + classifier so Safe/Tight/DryEarly/Spent demo windows are honest, not asserted.</summary>
    static WindowView VerdictWindow(WindowKind kind, double windowMinutes, double usedPct, double ratePctPerMin, double minutesToReset, DateTimeOffset utcNow)
    {
        double elapsed = windowMinutes - minutesToReset;
        ForecastResult f = ForecastMath.Evaluate(usedPct, elapsed, ratePctPerMin, windowMinutes, minutesToReset);
        QuotaState state = RawStateClassifier.Classify(usedPct, f, windowMinutes, refused: false);
        DateTimeOffset resetsAt = utcNow.AddMinutes(minutesToReset);
        DateTimeOffset? depletesAt = !double.IsPositiveInfinity(f.MinutesToDeplete) ? utcNow.AddMinutes(f.MinutesToDeplete) : null;

        return new WindowView(kind, windowMinutes, usedPct, resetsAt, state,
            RatePctPerMin: f.RatePctPerMin, PaceMultiple: f.Pace, ProjectedPctAtReset: f.ProjectedPctAtReset,
            DepletesAt: depletesAt, Shortfall: TimeSpan.FromMinutes(f.ShortfallMinutes), MeasuringReason: null);
    }

    static string ReasonText(MeasuringReasonCode reason) => reason switch
    {
        MeasuringReasonCode.Rollover => "Nytt fönster, mäter takt…",
        MeasuringReasonCode.TooEarly => "För tidigt att mäta takt…",
        MeasuringReasonCode.TooEarlyInWeek => "För tidigt i veckan — väntar på ett helt dygn",
        MeasuringReasonCode.TooLittleUsage => "För lite förbrukning ännu…",
        MeasuringReasonCode.NoData => "Hämtar…",
        MeasuringReasonCode.AwaitingReset => "Nytt fönster väntas",
        _ => "Mäter takt…",
    };
}
