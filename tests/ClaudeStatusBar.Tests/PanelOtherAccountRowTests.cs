using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Ui/PanelText.cs's ComposeOtherAccountRow: the compact "other accounts" list at the bottom of
/// the panel (docs/multi-account.md "Display", the multi-account UI task's "Panel" item) --
/// label, verdict colour, one short line, one per QuotaState/freshness shape.
/// </summary>
public class PanelOtherAccountRowTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test/+1", TimeSpan.FromHours(1), "Test/+1", "Test/+1");
    static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void Safe_RäckerTillReset()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 10.0, ratePctPerMin: 0.05, minutesToReset: 250);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 10.0, ratePctPerMin: 0.002, minutesToReset: 9000);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(3, "Personligt", View(session, weekly), Now, Tz);

        Assert.Equal(3, row.AccountIndex);
        Assert.Equal("Personligt", row.Label);
        Assert.Equal("räcker till reset", row.Line);
        Assert.Equal(PanelColorRole.Safe, row.Role);
    }

    [Fact]
    public void Tight_TajtRackerPrecis()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 78.0, ratePctPerMin: 0.30, minutesToReset: 60);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 60.0, ratePctPerMin: 0.025, minutesToReset: 1200);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(0, "Team", View(session, weekly), Now, Tz);

        Assert.Equal("tajt — räcker precis", row.Line);
        Assert.Equal(PanelColorRole.Tight, row.Role);
    }

    [Fact]
    public void DryEarly_SlutAtClockTime()
    {
        // docs/panel-v2.md's worked example: P=56, E=132, t_reset=168, r=0.424 -> depletes before reset.
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 56.0, ratePctPerMin: 0.424, minutesToReset: 168);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 10.0, ratePctPerMin: 0.002, minutesToReset: 9000);
        Assert.Equal(QuotaState.DryEarly, session.State);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(1, "Enterprise", View(session, weekly), Now, Tz);

        Assert.StartsWith("slut ", row.Line);
        Assert.Equal(PanelColorRole.Crit, row.Role);
    }

    [Fact]
    public void Spent_SlutOppnarAtResetTime()
    {
        WindowView session = Verdict(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 100.0, ratePctPerMin: 0.4, minutesToReset: 45);
        WindowView weekly = Verdict(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 20.0, ratePctPerMin: 0.005, minutesToReset: 5000);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(2, "Max", View(session, weekly, blockedUntil: session.ResetsAt), Now, Tz);

        Assert.StartsWith("slut · öppnar ", row.Line);
        Assert.Equal(PanelColorRole.Dead, row.Role);
    }

    [Fact]
    public void Measuring_MäterTakt()
    {
        WindowView session = Measuring(WindowKind.Session, QuotaWindows.SessionMinutes, usedPct: 2.0, minutesToReset: 298);
        WindowView weekly = Measuring(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, usedPct: 1.0, minutesToReset: 10_075);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(4, "Personligt", View(session, weekly), Now, Tz);

        Assert.Equal("mäter takt", row.Line);
        Assert.Equal(PanelColorRole.Measuring, row.Role);
    }

    [Fact]
    public void Unknown_GårInteAttLäsa()
    {
        var view = new QuotaView(
            WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
            WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
            Freshness.Unknown, null, Now.AddMinutes(-40), TimeSpan.FromMinutes(10), "get_usage svarar inte", QuotaState.Measuring, null);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(0, "Team", view, Now, Tz);

        Assert.Equal(PanelColorRole.Unknown, row.Role);
    }

    // ---- ComposeDuplicateAccountRow (docs/multi-account.md "Duplicate accounts") ----

    [Fact]
    public void Duplicate_ScriptedSlotConfigDir_NamesTheRemoveCommandForThatSlot()
    {
        string configDir = @"C:\Users\test\AppData\Local\ClaudeStatusBar\accounts\2\config";

        OtherAccountRow row = PanelText.ComposeDuplicateAccountRow(1, 0, "Max", configDir);

        Assert.Equal(1, row.AccountIndex);
        Assert.Equal("Konto 2", row.Label);
        Assert.Equal(PanelColorRole.Tight, row.Role);
        Assert.Contains("är samma inloggning som konto 1 (Max)", row.Line);
        Assert.Contains("add-account.ps1 -Remove 2", row.Line);
    }

    /// <summary>The default entry (configDir null, "whatever you're logged into") has no scripted slot to remove -- the row must fall back to login-only instructions instead of naming a nonexistent -Remove target.</summary>
    [Fact]
    public void Duplicate_DefaultConfigDir_FallsBackToLoginInstructions_NoRemoveCommand()
    {
        OtherAccountRow row = PanelText.ComposeDuplicateAccountRow(0, 1, "Team", duplicateConfigDir: null);

        Assert.Equal("Konto 1", row.Label);
        Assert.Contains("är samma inloggning som konto 2 (Team)", row.Line);
        Assert.DoesNotContain("-Remove", row.Line);
        Assert.Contains("claude /login", row.Line);
    }

    /// <summary>A hand-configured directory that doesn't follow the accounts\&lt;n&gt;\config layout has no slot number either -- same fallback as the default entry.</summary>
    [Fact]
    public void Duplicate_NonScriptedConfigDir_FallsBackToLoginInstructions()
    {
        OtherAccountRow row = PanelText.ComposeDuplicateAccountRow(2, 0, "Max", @"C:\Users\test\my-custom-claude-dir");

        Assert.DoesNotContain("-Remove", row.Line);
        Assert.Contains("accounts.json", row.Line);
    }
}
