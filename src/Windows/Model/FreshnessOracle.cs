namespace ClaudeStatusBar.Model;

/// <summary>
/// Inputs to the freshness ladder, evaluated fresh on every call -- this runs on
/// the always-running 1 s UI timer (docs/forecast-and-states.md, "Freshness
/// ladder"), never accumulated, never cached.
///
/// StaleAtMono/UnknownAtMono are FROZEN deadlines (decisions 3+5, round 2's
/// decision 2): QuotaModel computes them once, at each ACCEPTED successful poll,
/// as lastAcceptMono + 3*c_policy and lastAcceptMono + 10*c_policy (milliseconds),
/// where c_policy is the burning/idle/spent base interval WITHOUT the
/// soonest-reset cap and WITHOUT failure backoff. Neither a later failure, a
/// duplicate/deduped reply, nor the mere passage of time can move these
/// deadlines later -- they can only be superseded by a NEWER accepted poll,
/// which is the only thing allowed to improve freshness. Using monotonic time
/// (not UTC) for both the deadlines and the comparison also closes review
/// finding 2's last example: a UTC rollback too small to trip the 60s
/// clock-discontinuity check could otherwise walk the clock backward across a
/// UTC-denominated deadline and "improve" freshness with no new data at all --
/// monotonic time cannot move backward that way.
///
/// LastAcceptedMono tracks the monotonic instant of the last ACCEPTED live
/// observation (QuotaModel's own UTC&lt;-&gt;mono anchor, decision 2) in place of a
/// per-window UTC fingerprint-change time: since re-anchoring itself now only
/// happens on an accepted sample (never a duplicate), it already carries the
/// same "did real new data arrive" signal decision was built on, without a
/// second UTC-based duration to also convert.
/// </summary>
public readonly record struct FreshnessInputs(
    bool HasEverSucceeded,
    long? StaleAtMono,
    long? UnknownAtMono,
    long? LastAcceptedMono,
    DateTimeOffset? SessionResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    bool ClockDiscontinuity);

public static class FreshnessOracle
{
    static readonly long FingerprintStaleAfterMs = (long)TimeSpan.FromMinutes(20).TotalMilliseconds;
    static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(60);

    public static Freshness Evaluate(FreshnessInputs i, DateTimeOffset utcNow, long monoMs)
    {
        if (!i.HasEverSucceeded) return Freshness.Unknown;

        // Decision 6: a detected clock jump makes every live-computed age suspect. Freshness
        // drops to Stale (not Unknown -- we DO have real data, just an untrustworthy clock)
        // until the next accepted sample re-anchors.
        if (i.ClockDiscontinuity) return Freshness.Stale;

        if (i.UnknownAtMono is { } u && monoMs > u) return Freshness.Unknown;
        if (i.StaleAtMono is { } s && monoMs > s) return Freshness.Stale;

        if (i.LastAcceptedMono is { } changed && (monoMs - changed) > FingerprintStaleAfterMs) return Freshness.Stale;

        // resets_at deadlines stay UTC: this is an absolute wall-clock instant compared to the
        // current wall clock, not a duration since some past accepted event, so it does not
        // need monotonic conversion the way the durations above do. Guarded (decision 10):
        // resets_at is bounds-checked at ingest, but a defensive SafeAdd keeps this comparison
        // itself incapable of throwing regardless.
        if (i.SessionResetsAt is { } sr && QuotaTimeUtil.SafeAdd(sr, ResetGrace) is { } srg && utcNow > srg) return Freshness.Stale;
        if (i.WeeklyResetsAt is { } wr && QuotaTimeUtil.SafeAdd(wr, ResetGrace) is { } wrg && utcNow > wrg) return Freshness.Stale;

        return Freshness.Live;
    }
}
