using ClaudeStatusBar.Config;

namespace ClaudeStatusBar.Model;

/// <summary>
/// Synthetic multi-account frames for `--demo`/`--capture-states` (the multi-account UI task's
/// "Demo mode" section): a three-account scenario that demonstrates both display modes
/// (docs/multi-account.md "Display"). Every window here is built by running the real
/// ForecastMath + RawStateClassifier over crafted inputs -- same discipline as
/// Model/DemoQuotaSource.cs -- so nothing is hand-asserted into a state the real model
/// couldn't produce.
/// </summary>
public static class MultiAccountDemoSource
{
    public readonly record struct DemoAccount(string Label, QuotaView View, string? Subtitle = null);
    public readonly record struct MultiState(string Key, AccountDisplayMode Mode, IReadOnlyList<DemoAccount> Accounts, int FocusIndex);

    /// <summary>
    /// Three accounts -- "Personligt" (Safe), "Team" (DryEarly, close to running out) and
    /// "Enterprise" (Spent, already blocked) -- shown once in perAccount mode (three icons,
    /// panel focused on "Team", one account the user picked) and once in binding mode (one icon:
    /// the Spent account, the closest to being blocked -- panel focused on THAT account,
    /// "Enterprise", the same default docs/multi-account.md gives clicking the single binding
    /// icon). Both frames' "other accounts" list still shows all three states (Safe/DryEarly/
    /// Spent rows) either way.
    /// </summary>
    public static IReadOnlyList<MultiState> Build(DateTimeOffset utcNow)
    {
        DemoAccount personal = new("Personligt", SafeView(utcNow), "Max · personlig organisation");
        DemoAccount team = new("Team", DryEarlyView(utcNow), "Acme AB");
        DemoAccount enterprise = new("Enterprise", SpentView(utcNow), "Globex AB");
        var accounts = new List<DemoAccount> { personal, team, enterprise };

        return new List<MultiState>
        {
            new("multi_per_account", AccountDisplayMode.PerAccount, accounts, FocusIndex: 1),
            new("multi_binding", AccountDisplayMode.Binding, accounts, FocusIndex: 2),
            new("multi_needs_login", AccountDisplayMode.PerAccount, new List<DemoAccount> { personal, new("Team", NeedsLoginView(utcNow), "Acme AB") }, FocusIndex: 1),
            new("multi_loading", AccountDisplayMode.PerAccount, new List<DemoAccount> { personal, new("Team", LoadingView(utcNow), "Acme AB") }, FocusIndex: 1),
        };
    }

    // ---- availability timeline scenarios (docs/multi-account.md "Availability timeline") ----

    /// <summary>A named set of accounts to show the timeline strip on, with the horizon it is shown at.</summary>
    public readonly record struct TimelineScenario(string Key, IReadOnlyList<DemoAccount> Accounts, TimelineHorizon Horizon, int FocusIndex = 1);

    /// <summary>
    /// Five scenarios for the strip, built (like everything here) with the real forecast and classifier:
    ///   timeline_gap       -- a gap: the third account is spent until tomorrow, the other two run out and are blocked together for a while;
    ///   timeline_coverage  -- no gap: one account runs out, one is spent, an idle one (no open session) covers the whole time;
    ///   timeline_all_spent -- every account is spent: no one is available now, the first reopens in 2 h;
    ///   timeline_unknown   -- one account is not logged in: unknown, and the answer says so;
    ///   timeline_3d        -- the 3 d horizon, with a gap across the night.
    /// </summary>
    public static IReadOnlyList<TimelineScenario> BuildTimelineScenarios(DateTimeOffset now)
    {
        const double Week = QuotaWindows.WeeklyMinutes, Session = QuotaWindows.SessionMinutes;
        WindowView SessionOk() => VerdictWindow(WindowKind.Session, Session, usedPct: 18.0, ratePctPerMin: 0.1, minutesToReset: 200, now);
        WindowView WeekOk() => VerdictWindow(WindowKind.Weekly, Week, usedPct: 12.0, ratePctPerMin: 0.004, minutesToReset: 8000, now);

        QuotaView personalRunsOut = Live(SessionOk(), RunsOut(Week, WindowKind.Weekly, depletesInMin: 4 * 60, resetInMin: 3 * 24 * 60, now), now);
        QuotaView teamRunsOut = Live(SessionOk(), RunsOut(Week, WindowKind.Weekly, depletesInMin: 6 * 60, resetInMin: 10 * 60, now), now);
        QuotaView enterpriseSpentWeek = Live(SessionOk(), SpentWindow(Week, WindowKind.Weekly, resetInMin: 30 * 60, now), now);

        QuotaView personalRunsOutEarly = Live(SessionOk(), RunsOut(Week, WindowKind.Weekly, depletesInMin: 4 * 60, resetInMin: 20 * 60, now), now);
        QuotaView enterpriseSpentTomorrow = Live(SessionOk(), SpentWindow(Week, WindowKind.Weekly, resetInMin: 14 * 60, now), now);
        QuotaView teamIdle = Idle(now);

        QuotaView personalSpent = Live(SessionOk(), SpentWindow(Week, WindowKind.Weekly, resetInMin: 20 * 60, now), now);
        QuotaView teamSessionSpent = Live(SpentWindow(Session, WindowKind.Session, resetInMin: 120, now), WeekOk(), now);

        QuotaView personalSpent3d = Live(SessionOk(), SpentWindow(Week, WindowKind.Weekly, resetInMin: 55 * 60, now), now);
        QuotaView teamRunsOut3d = Live(SessionOk(), RunsOut(Week, WindowKind.Weekly, depletesInMin: 30 * 60, resetInMin: 60 * 60, now), now);
        QuotaView enterpriseSpent3d = Live(SessionOk(), SpentWindow(Week, WindowKind.Weekly, resetInMin: 40 * 60, now), now);

        DemoAccount Personal(QuotaView v) => new("Personligt", v, "Max · personlig organisation");
        DemoAccount Team(QuotaView v) => new("Team", v, "Acme AB");
        DemoAccount Enterprise(QuotaView v) => new("Enterprise", v, "Globex AB");

        return new List<TimelineScenario>
        {
            new("timeline_gap", new[] { Personal(personalRunsOut), Team(teamRunsOut), Enterprise(enterpriseSpentWeek) }, TimelineHorizon.Hours24),
            new("timeline_coverage", new[] { Personal(personalRunsOutEarly), Team(teamIdle), Enterprise(enterpriseSpentTomorrow) }, TimelineHorizon.Hours24),
            new("timeline_all_spent", new[] { Personal(personalSpent), Team(teamSessionSpent), Enterprise(enterpriseSpentWeek) }, TimelineHorizon.Hours24),
            new("timeline_unknown", new[] { Personal(personalRunsOutEarly), Team(SafeView(now)), new DemoAccount("Konto utan inloggning", NeedsLoginView(now), "Acme AB") }, TimelineHorizon.Hours24),
            new("timeline_3d", new[] { Personal(personalSpent3d), Team(teamRunsOut3d), Enterprise(enterpriseSpent3d) }, TimelineHorizon.Days3),
            // long labels that share a long prefix (placeholders, never real addresses): the row labels must stay tellable apart
            new("timeline_labels", new[]
            {
                new DemoAccount("Max 20x (alex@example.com)", personalRunsOut, "Max · personlig organisation"),
                new DemoAccount("Max 20x (alex@example.org)", teamRunsOut, "Max · personlig organisation"),
                new DemoAccount("Team (sam@example.net)", enterpriseSpentWeek, "Acme AB"),
            }, TimelineHorizon.Hours24),
        };
    }

    /// <summary>A Live view of two windows, with BlockedUntil as the real model sets it (the latest reset among the spent windows).</summary>
    static QuotaView Live(WindowView session, WindowView weekly, DateTimeOffset now)
    {
        DateTimeOffset? blocked = null;
        if (session.State == QuotaState.Spent && session.ResetsAt is { } sr) blocked = sr;
        if (weekly.State == QuotaState.Spent && weekly.ResetsAt is { } wr) blocked = blocked is null || wr > blocked ? wr : blocked;
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: now.AddSeconds(-20), LastSuccessAt: now.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(150),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: blocked);
    }

    /// <summary>A window forecast to run out in `depletesInMin`, before its reset `resetInMin` away.</summary>
    static WindowView RunsOut(double windowMinutes, WindowKind kind, double depletesInMin, double resetInMin, DateTimeOffset now) =>
        VerdictWindow(kind, windowMinutes, usedPct: 60.0, ratePctPerMin: 40.0 / depletesInMin, minutesToReset: resetInMin, now);

    /// <summary>A window that is spent (100 %) until its reset.</summary>
    static WindowView SpentWindow(double windowMinutes, WindowKind kind, double resetInMin, DateTimeOffset now) =>
        VerdictWindow(kind, windowMinutes, usedPct: 100.0, ratePctPerMin: 0.2, minutesToReset: resetInMin, now);

    /// <summary>
    /// An idle account: polls succeed, but nothing is used, so the session window is closed (no deadline, no
    /// percentage) and nothing has changed for hours -- the case the panel must not show as an error.
    /// </summary>
    static QuotaView Idle(DateTimeOffset now)
    {
        var closedSession = new WindowView(WindowKind.Session, QuotaWindows.SessionMinutes, UsedPct: null, ResetsAt: null, QuotaState.Measuring,
            RatePctPerMin: null, PaceMultiple: null, ProjectedPctAtReset: null, DepletesAt: null, Shortfall: TimeSpan.Zero, MeasuringReason: "Inget förbrukat ännu");
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 4.0, ratePctPerMin: 0.002, minutesToReset: 6200, now);
        return new QuotaView(closedSession, weekly, Freshness.Live,
            LastChangedAt: now.AddHours(-10).AddMinutes(-39), LastSuccessAt: now.AddSeconds(-2), PollInterval: TimeSpan.FromMinutes(10),
            Error: null, IconSeverity: weekly.State, BlockedUntil: null);
    }

    /// <summary>docs/multi-account.md "Loading": the account has not answered yet.</summary>
    static QuotaView LoadingView(DateTimeOffset utcNow) => new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, LastChangedAt: null, LastSuccessAt: null, PollInterval: TimeSpan.FromSeconds(30),
        Error: null, IconSeverity: QuotaState.Measuring, BlockedUntil: null, Loading: true);

    /// <summary>docs/multi-account.md "NeedsLogin": a fresh child confirmed there is no usable login. The grey ring, the tooltip and the panel's re-login button all key on QuotaView.NeedsLogin.</summary>
    static QuotaView NeedsLoginView(DateTimeOffset utcNow) => new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, LastChangedAt: null, LastSuccessAt: utcNow.AddHours(-3), PollInterval: TimeSpan.FromMinutes(2),
        Error: "not logged in", IconSeverity: QuotaState.Measuring, BlockedUntil: null, NeedsLogin: true);

    static QuotaView SafeView(DateTimeOffset utcNow)
    {
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 18.0, ratePctPerMin: 0.12, minutesToReset: 240, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 12.0, ratePctPerMin: 0.004, minutesToReset: 8000, utcNow);
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-15), LastSuccessAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(150),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: null);
    }

    static QuotaView DryEarlyView(DateTimeOffset utcNow)
    {
        // Same shape as DemoQuotaSource's worked-example DryEarly session window.
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 40.0, ratePctPerMin: 0.01, minutesToReset: 6000, utcNow);
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-10), LastSuccessAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(30),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: null);
    }

    static QuotaView SpentView(DateTimeOffset utcNow)
    {
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 45, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 70.0, ratePctPerMin: 0.02, minutesToReset: 5000, utcNow);
        DateTimeOffset? blockedUntil = session.State == QuotaState.Spent ? session.ResetsAt : null;
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-30), LastSuccessAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(300),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: blockedUntil);
    }

    /// <summary>Runs the real forecast + classifier, exactly like Model/DemoQuotaSource.cs's own VerdictWindow.</summary>
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
}
