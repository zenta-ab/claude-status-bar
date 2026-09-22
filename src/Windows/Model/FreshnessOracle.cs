namespace ClaudeStatusBar.Model;

/// <summary>
/// Inputs to the freshness ladder, evaluated fresh on every call -- this runs on
/// the always-running 1 s UI timer (docs/forecast-and-states.md, "Freshness
/// ladder"), never accumulated, never cached.
///
/// StaleAtMono/UnknownAtMono are FROZEN transport-liveness deadlines (decisions
/// 3+5, round 2's decision 2, and the 2026-09-22 transport/data-age split):
/// QuotaModel computes them once, at each poll that returns a valid reading for
/// the SESSION window -- novel accepted, a cached duplicate of an already-open
/// window, or a validly closed window, see QuotaModel.Ingest's
/// "sessionCurrentThisPoll" -- as t + 3*c_policy and t + 10*c_policy
/// (milliseconds), where c_policy is the burning/idle/spent base interval
/// WITHOUT the soonest-reset cap and WITHOUT failure backoff. They answer "is
/// the pipeline working" (are polls succeeding at roughly the expected cadence),
/// which is a different question from "is the data moving" below. Neither a
/// later failure, a poll lacking a valid session reading, nor the mere passage
/// of time can move these deadlines later -- they can only be superseded by a
/// NEWER qualifying poll, which is the only thing allowed to improve this half
/// of freshness. Using monotonic time (not UTC) for both the deadlines and the
/// comparison also closes review finding 2's last example: a UTC rollback too
/// small to trip the 60s clock-discontinuity check could otherwise walk the
/// clock backward across a UTC-denominated deadline and "improve" freshness
/// with no new data at all -- monotonic time cannot move backward that way.
///
/// LastAcceptedMono answers the OTHER question, "is the data moving": it tracks
/// the monotonic instant of the last USABLE reading (QuotaModel's
/// _lastUsableMono -- accepted, OR a window validly reporting itself closed;
/// see that field's doc comment) in place of a per-window UTC fingerprint-change
/// time. Deliberately narrower than StaleAtMono/UnknownAtMono above: a cached
/// duplicate of an already-open window renews transport liveness (the poll
/// round-tripped) but NOT this field (no new data arrived) -- a duplicate cannot
/// fake elapsed monotonic time, so a server that only ever replays the same
/// snapshot still ages past FingerprintStaleAfterMs and goes Stale here even
/// while transport stays Live.
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
