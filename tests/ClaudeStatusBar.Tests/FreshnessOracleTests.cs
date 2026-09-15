using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Decisions 3+5 (round 1) / decision 2 (round 2): StaleAtMono/UnknownAtMono are frozen
/// MONOTONIC deadlines (QuotaModel computes them once, at each ACCEPTED poll, from the POLICY
/// interval only -- no reset cap, no backoff). These tests exercise FreshnessOracle directly
/// against already-frozen deadlines, which is what makes them an oracle for the SPEC rather
/// than for QuotaModel's internal interval math (the review's complaint about the old fixed-`c`
/// tests: "isolates away both main integration bugs" -- QuotaModelTests below covers the
/// integration: computing those deadlines correctly from backoff/reset-cap scenarios, and from
/// realistic consistent monoMs deltas rather than mismatched ones that trip clock-jump
/// detection).
/// </summary>
public class FreshnessOracleTests
{
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    const long NowMono = 10_000_000;

    static FreshnessInputs Baseline() => new(
        HasEverSucceeded: true,
        StaleAtMono: NowMono + 90_000,
        UnknownAtMono: NowMono + 300_000,
        LastAcceptedMono: NowMono,
        SessionResetsAt: Now.AddHours(2),
        WeeklyResetsAt: Now.AddDays(2),
        ClockDiscontinuity: false);

    [Fact]
    public void Unknown_BeforeFirstSuccess()
    {
        FreshnessInputs i = Baseline() with { HasEverSucceeded = false, StaleAtMono = null, UnknownAtMono = null, LastAcceptedMono = null };
        Assert.Equal(Freshness.Unknown, FreshnessOracle.Evaluate(i, Now, NowMono));
    }

    [Fact]
    public void Stale_WhenFingerprintUnchangedFor21Minutes()
    {
        FreshnessInputs i = Baseline() with { LastAcceptedMono = NowMono - (long)TimeSpan.FromMinutes(21).TotalMilliseconds };
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(i, Now, NowMono));
    }

    [Fact]
    public void Stale_WhenNowIsPastResetsAtPlusSixtySeconds_ForEitherWindow()
    {
        FreshnessInputs session = Baseline() with { SessionResetsAt = Now.AddSeconds(-61) };
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(session, Now, NowMono));

        FreshnessInputs weekly = Baseline() with { WeeklyResetsAt = Now.AddSeconds(-61) };
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(weekly, Now, NowMono));
    }

    [Fact]
    public void Unknown_WhenPastTheFrozenUnknownDeadline()
    {
        FreshnessInputs i = Baseline() with { UnknownAtMono = NowMono - 1000 };
        Assert.Equal(Freshness.Unknown, FreshnessOracle.Evaluate(i, Now, NowMono));
    }

    [Fact]
    public void Stale_WhenPastTheFrozenStaleDeadline_ButNotYetTheUnknownOne()
    {
        FreshnessInputs i = Baseline() with { StaleAtMono = NowMono - 1000, UnknownAtMono = NowMono + 120_000 };
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(i, Now, NowMono));
    }

    [Fact]
    public void Live_WhenEverythingIsFresh()
    {
        Assert.Equal(Freshness.Live, FreshnessOracle.Evaluate(Baseline(), Now, NowMono));
    }

    // ---- decision 6: clock discontinuity ----

    [Fact]
    public void Stale_OnClockDiscontinuity_EvenWhenEveryOtherSignalLooksFresh()
    {
        // A clock jump makes every live-computed age suspect (real data exists, hence
        // Stale rather than Unknown) until the next accepted sample re-anchors.
        FreshnessInputs i = Baseline() with { ClockDiscontinuity = true };
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(i, Now, NowMono));
    }

    // ---- decision 5: a backoff sequence must never keep Live alive, nor "improve" freshness ----

    [Fact]
    public void BackoffSequence_NeverKeepsLiveAlive_AndNeverImprovesAsTimePasses()
    {
        // Mirrors the review's exact scenario: one success (burning, c_policy=30s), then a
        // failure backoff climbing 60->600s while NO new success arrives. The frozen
        // MONOTONIC deadlines (from the success's c_policy, never the backoff's growing
        // interval) must make freshness monotonically worse, never better, and never claim
        // Live once either deadline has passed.
        const long successMono = 2_000_000;
        TimeSpan cPolicy = TimeSpan.FromSeconds(30); // burning at the moment of the last success
        FreshnessInputs frozen = new(
            HasEverSucceeded: true,
            StaleAtMono: successMono + (long)(cPolicy.TotalMilliseconds * 3),   // +90s
            UnknownAtMono: successMono + (long)(cPolicy.TotalMilliseconds * 10), // +300s
            LastAcceptedMono: successMono,
            SessionResetsAt: Now.AddHours(3),
            WeeklyResetsAt: null,
            ClockDiscontinuity: false);

        Freshness atStale = FreshnessOracle.Evaluate(frozen, Now.AddSeconds(91), successMono + 91_000);
        Assert.Equal(Freshness.Stale, atStale);

        // 15.5 min in the review's failure sequence (60,120,240,480,600s backoff): must not
        // be Live, and must not have improved back from Unknown at any later sample either.
        long at1530Mono = successMono + (long)TimeSpan.FromMinutes(15.5).TotalMilliseconds;
        Freshness at1530 = FreshnessOracle.Evaluate(frozen, Now.AddMinutes(15.5), at1530Mono);
        Assert.NotEqual(Freshness.Live, at1530);

        long earlierMono = successMono + (long)TimeSpan.FromMinutes(6).TotalMilliseconds;
        long laterMono = successMono + (long)TimeSpan.FromMinutes(20).TotalMilliseconds;
        Freshness earlier = FreshnessOracle.Evaluate(frozen, Now.AddMinutes(6), earlierMono);
        Freshness later = FreshnessOracle.Evaluate(frozen, Now.AddMinutes(20), laterMono);
        Assert.True((int)earlier <= (int)later, "freshness must never improve as unconfirmed time passes");
    }
}
