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
    public readonly record struct DemoAccount(string Label, QuotaView View);
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
        DemoAccount personal = new("Personligt", SafeView(utcNow));
        DemoAccount team = new("Team", DryEarlyView(utcNow));
        DemoAccount enterprise = new("Enterprise", SpentView(utcNow));
        var accounts = new List<DemoAccount> { personal, team, enterprise };

        return new List<MultiState>
        {
            new("multi_per_account", AccountDisplayMode.PerAccount, accounts, FocusIndex: 1),
            new("multi_binding", AccountDisplayMode.Binding, accounts, FocusIndex: 2),
        };
    }

    static QuotaView SafeView(DateTimeOffset utcNow)
    {
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 18.0, ratePctPerMin: 0.12, minutesToReset: 240, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 12.0, ratePctPerMin: 0.004, minutesToReset: 8000, utcNow);
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-15), LastPollAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(150),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: null);
    }

    static QuotaView DryEarlyView(DateTimeOffset utcNow)
    {
        // Same shape as DemoQuotaSource's worked-example DryEarly session window.
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 40.0, ratePctPerMin: 0.01, minutesToReset: 6000, utcNow);
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-10), LastPollAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(30),
            Error: null, IconSeverity: (QuotaState)Math.Max((int)session.State, (int)weekly.State), BlockedUntil: null);
    }

    static QuotaView SpentView(DateTimeOffset utcNow)
    {
        WindowView session = VerdictWindow(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.40, minutesToReset: 45, utcNow);
        WindowView weekly = VerdictWindow(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 70.0, ratePctPerMin: 0.02, minutesToReset: 5000, utcNow);
        DateTimeOffset? blockedUntil = session.State == QuotaState.Spent ? session.ResetsAt : null;
        return new QuotaView(session, weekly, Freshness.Live,
            LastChangedAt: utcNow.AddSeconds(-30), LastPollAt: utcNow.AddSeconds(-2), PollInterval: TimeSpan.FromSeconds(300),
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
