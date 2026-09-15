using System.Linq;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Decision 9 (round 2): demo states must never show a combination the live model itself
/// could not produce -- otherwise the demo mode weakens visual review of the real rules
/// instead of exercising them honestly.
/// </summary>
public class DemoQuotaSourceTests
{
    static readonly DateTimeOffset UtcNow = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SafeWeekly_HasAtLeastTwentyFourHoursElapsed_LikeTheLiveModelRequires()
    {
        // The live weekly window refuses (TooEarlyInWeek) until E >= 1440 min (24h). A demo
        // "Safe weekly" window built with less elapsed time than that is a shape the real
        // model could never actually show as a verdict.
        DemoQuotaSource.DemoState safe = DemoQuotaSource.Build(UtcNow).Single(s => s.Key == "safe");
        WindowView weekly = safe.View.Weekly;

        Assert.NotNull(weekly.ResetsAt);
        double elapsedMinutes = QuotaWindows.WeeklyMinutes - (weekly.ResetsAt!.Value - UtcNow).TotalMinutes;
        Assert.True(elapsedMinutes >= 1440.0, $"expected E >= 1440 min, got {elapsedMinutes}");
        Assert.Equal(QuotaState.Safe, weekly.State);
    }

    [Fact]
    public void AwaitingReset_ShowsStaleFreshness_NotLive()
    {
        // The session's own deadline has already passed with no later window observed yet --
        // the live FreshnessOracle would mark this Stale via the resets_at+60s rule regardless
        // of how recently the cached reply arrived.
        DemoQuotaSource.DemoState awaitingReset = DemoQuotaSource.Build(UtcNow).Single(s => s.Key == "awaiting_reset");
        Assert.Equal(Freshness.Stale, awaitingReset.View.Freshness);
    }
}
