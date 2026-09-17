using ClaudeStatusBar.Config;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Model/AccountDisplayPlan.cs -- docs/multi-account.md "Display": binding selection (severity,
/// then shorter depletion, then config order) and which accounts get a tray icon in each mode.
/// Every QuotaView here is built from real WindowView/ForecastMath values, mirroring
/// PanelTextTests, so nothing is hand-asserted into a shape the real model couldn't produce.
/// </summary>
public class AccountDisplayPlanTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

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

    static WindowView Measuring(WindowKind kind, double windowMinutes, double usedPct, double minutesToReset) =>
        new(kind, windowMinutes, usedPct, Now.AddMinutes(minutesToReset), QuotaState.Measuring,
            null, null, null, null, TimeSpan.Zero, "Mäter takt…");

    static QuotaView View(WindowView session, WindowView weekly, Freshness freshness = Freshness.Live, DateTimeOffset? blockedUntil = null) =>
        new(session, weekly, freshness, Now.AddSeconds(-10), Now.AddSeconds(-2),
            TimeSpan.FromSeconds(30), Error: null, (QuotaState)Math.Max((int)session.State, (int)weekly.State), blockedUntil);

    static QuotaView SafeView() => View(
        Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 10.0, ratePctPerMin: 0.05, minutesToReset: 250),
        Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 10.0, ratePctPerMin: 0.002, minutesToReset: 9000));

    static QuotaView TightView() => View(
        Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 78.0, ratePctPerMin: 0.30, minutesToReset: 60),
        Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 60.0, ratePctPerMin: 0.025, minutesToReset: 1200));

    /// <summary>DryEarly with a specific depletion delay, for the shorter-depletion tie-break.</summary>
    static QuotaView DryEarlyView(double minutesToDepleteApprox)
    {
        // ForecastMath: t_dep = (100 - usedPct) / ratePctPerMin. Pick usedPct/rate to land near the requested minutes.
        const double usedPct = 50.0;
        double rate = (100.0 - usedPct) / minutesToDepleteApprox;
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct, rate, minutesToReset: minutesToDepleteApprox + 60);
        Assert.Equal(QuotaState.DryEarly, session.State);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 10.0, ratePctPerMin: 0.002, minutesToReset: 9000);
        return View(session, weekly);
    }

    static QuotaView SpentView()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.4, minutesToReset: 45);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 20.0, ratePctPerMin: 0.005, minutesToReset: 5000);
        return View(session, weekly, blockedUntil: session.ResetsAt);
    }

    static QuotaView MeasuringView() => View(
        Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 2.0, minutesToReset: 298),
        Measuring(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 1.0, minutesToReset: 10_075));

    static QuotaView UnknownView() => new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, null, Now.AddMinutes(-40), TimeSpan.FromMinutes(10), "get_usage svarar inte", QuotaState.Measuring, null);

    // ---- binding selection ----

    [Fact]
    public void SelectBinding_HighestSeverityWins()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, SafeView()),
            new AccountDisplayPlan.Candidate(true, SpentView()),
            new AccountDisplayPlan.Candidate(true, TightView()),
        };

        Assert.Equal(1, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_TieOnSeverity_ShorterDepletionWins()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, DryEarlyView(minutesToDepleteApprox: 200)),
            new AccountDisplayPlan.Candidate(true, DryEarlyView(minutesToDepleteApprox: 50)), // closer to running out
            new AccountDisplayPlan.Candidate(true, DryEarlyView(minutesToDepleteApprox: 150)),
        };

        Assert.Equal(1, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_TieOnEverything_FallsBackToConfigOrder()
    {
        QuotaView same = SafeView();
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, same),
            new AccountDisplayPlan.Candidate(true, same),
            new AccountDisplayPlan.Candidate(true, same),
        };

        Assert.Equal(0, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_MeasuringAndUnknown_NeverBeatARealVerdict()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, MeasuringView()),
            new AccountDisplayPlan.Candidate(true, UnknownView()),
            new AccountDisplayPlan.Candidate(true, SafeView()), // the only real verdict -- must win regardless of severity numbers
        };

        Assert.Equal(2, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_OnlyMeasuringAndUnknownExist_StillPicksOneByTheSameTieBreak()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, UnknownView()),
            new AccountDisplayPlan.Candidate(true, MeasuringView()),
        };

        // Neither has a real verdict, so config order decides (index 0 kept, since neither is "better").
        Assert.Equal(0, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_DisabledAccountsAreNeverEligible()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(false, SpentView()), // most severe, but disabled
            new AccountDisplayPlan.Candidate(true, SafeView()),
        };

        Assert.Equal(1, AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    [Fact]
    public void SelectBinding_EveryAccountDisabled_ReturnsNull()
    {
        var accounts = new[] { new AccountDisplayPlan.Candidate(false, SafeView()) };
        Assert.Null(AccountDisplayPlan.SelectBinding(accounts, Now));
    }

    // ---- icon count ----

    [Fact]
    public void SelectIconAccounts_PerAccount_CapsAtMaxIconsInConfigOrder()
    {
        var accounts = Enumerable.Range(0, 5).Select(_ => new AccountDisplayPlan.Candidate(true, SafeView())).ToList();

        IReadOnlyList<int> selected = AccountDisplayPlan.SelectIconAccounts(accounts, AccountDisplayMode.PerAccount, maxIcons: 3, Now);

        Assert.Equal(new[] { 0, 1, 2 }, selected);
    }

    [Fact]
    public void SelectIconAccounts_Binding_AlwaysExactlyOne()
    {
        var accounts = Enumerable.Range(0, 5).Select(_ => new AccountDisplayPlan.Candidate(true, SafeView())).ToList();

        IReadOnlyList<int> selected = AccountDisplayPlan.SelectIconAccounts(accounts, AccountDisplayMode.Binding, maxIcons: 3, Now);

        Assert.Single(selected);
    }

    [Fact]
    public void SelectIconAccounts_PerAccount_DisabledAccountsExcludedFromTheCount()
    {
        var accounts = new[]
        {
            new AccountDisplayPlan.Candidate(true, SafeView()),
            new AccountDisplayPlan.Candidate(false, SafeView()),
            new AccountDisplayPlan.Candidate(true, SafeView()),
            new AccountDisplayPlan.Candidate(false, SafeView()),
            new AccountDisplayPlan.Candidate(true, SafeView()),
        };

        IReadOnlyList<int> selected = AccountDisplayPlan.SelectIconAccounts(accounts, AccountDisplayMode.PerAccount, maxIcons: 3, Now);

        Assert.Equal(new[] { 0, 2, 4 }, selected); // the three enabled ones, disabled indices 1 and 3 skipped
    }
}
