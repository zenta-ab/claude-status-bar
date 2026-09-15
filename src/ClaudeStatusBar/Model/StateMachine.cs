namespace ClaudeStatusBar.Model;

/// <summary>
/// Raw state table, grace cap and hysteresis, per docs/forecast-and-states.md,
/// "States (per window)". Three independent, directly testable pieces:
///
///   RawStateClassifier - the priority table (Spent > Measuring > DryEarly > Tight > Safe).
///   GraceCap           - the near-reset clamp, applied after hysteresis, every tick.
///   HysteresisHold      - per-window debounce between verdict states, stepped once per
///                         ACCEPTED (novel) observation, never per poll tick or duplicate.
///
/// Clarification applied here (see doc's "Refusal -> Measuring" note): leaving
/// Measuring commits immediately, because Measuring is not a verdict and there
/// is nothing to debounce -- hysteresis only ever arbitrates between the three
/// verdict states (Safe/Tight/DryEarly). Spent, grace and rollover all bypass
/// hysteresis, as the doc says.
/// </summary>
public static class RawStateClassifier
{
    public static QuotaState Classify(double usedPct, ForecastResult forecast, double windowMinutes, bool refused)
    {
        // Decision 15: Spent only at P >= 100 (not 99.5) -- confirmed exhaustion, not "almost".
        // 97-99.9% still reaches DryEarly on its own via the existing RemainingPct <= 3 clause below.
        if (usedPct >= 100.0) return QuotaState.Spent;
        if (refused) return QuotaState.Measuring;

        double sLo = Math.Max(0.05 * windowMinutes, 10.0);
        double sHi = Math.Max(0.08 * windowMinutes, 15.0);

        if (forecast.ShortfallMinutes >= sLo || forecast.RemainingPct <= 3.0) return QuotaState.DryEarly;
        if (forecast.ShortfallMinutes > 0.0 || forecast.SlackMinutes <= sHi || forecast.RemainingPct <= 10.0) return QuotaState.Tight;
        return QuotaState.Safe;
    }
}

public static class GraceCap
{
    /// <summary>
    /// Decision 2: grace must never turn a predicted cutoff into Safe. The only
    /// remaining cap is t_reset &lt;= 15 min, which caps DryEarly at Tight (no red
    /// right before a reset that will clear it anyway); the old t_reset &lt;= 5 min
    /// -&gt; Safe rule is removed entirely. Spent is never capped. A cap can only
    /// ever lower severity, never raise it.
    /// </summary>
    public static QuotaState Apply(QuotaState state, double minutesToReset)
    {
        if (state == QuotaState.Spent) return state;
        if (minutesToReset <= 15.0) return (QuotaState)Math.Min((int)state, (int)QuotaState.Tight);
        return state;
    }
}

/// <summary>
/// Per-window hysteresis. Step() must be called only for ACCEPTED (novel)
/// observations -- decision 11: duplicate replies and poll ticks are not
/// evidence, and a failed poll neither counts as evidence nor resets pending
/// evidence (the caller simply never calls Step() for either). Committed
/// reflects the result on every subsequent read until the next Step()/
/// CommitFromMeasuring().
///
/// Round 2 (finding 2, decision 2): all persistence -- LastChangeMono, the
/// cooldown, and the de-escalation minimum-elapsed-time gate -- is monotonic
/// (long milliseconds from Environment.TickCount64), not UTC. A poll response
/// this class ever sees for Step() is always live (QuotaModel never steps
/// hysteresis from WarmStart's replay path), so its monoMs is always a
/// trustworthy live reading -- no UTC/mono reconciliation is needed here the
/// way WindowTracker needs one for out-of-order detection.
///
/// Escalation (decision 11): commits to the LOWEST severity that is still
/// above Committed, seen across the last _escalateEvidenceCount accepted
/// observations -- not to exact repeated agreement on one target state. This
/// is what lets an alternating Tight/DryEarly escalate at all (both qualify
/// as "above Committed"; the old exact-match rule could stall on Safe
/// forever, per review finding 11).
///
/// De-escalation needs _deescalateEvidenceCount consecutive accepted
/// observations agreeing on the same lower state, AND at least
/// _deescalateMinElapsedMs real time since the first of them -- replacing the
/// old poll-count-only thresholds (6/20), which scaled with polling policy
/// instead of wall-clock confidence.
///
/// The original "at most one committed change per cooldown" rule still gates
/// both directions.
/// </summary>
public sealed class HysteresisHold
{
    readonly int _escalateEvidenceCount;
    readonly int _deescalateEvidenceCount;
    readonly long _deescalateMinElapsedMs;
    readonly long _cooldownMs;

    readonly List<QuotaState> _escalateHistory = new();
    QuotaState? _pendingDeescalateState;
    int _pendingDeescalateCount;
    long? _pendingDeescalateSinceMono;

    public QuotaState Committed { get; private set; } = QuotaState.Measuring;
    public long? LastChangeMono { get; private set; }

    public HysteresisHold(bool weekly)
    {
        _escalateEvidenceCount = weekly ? 4 : 2;
        _deescalateEvidenceCount = weekly ? 4 : 3;
        _deescalateMinElapsedMs = (long)(weekly ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(10)).TotalMilliseconds;
        _cooldownMs = (long)(weekly ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(90)).TotalMilliseconds;
    }

    /// <summary>Call only for an accepted (novel) observation -- see the type doc comment.</summary>
    public QuotaState Step(QuotaState raw, long monoMs)
    {
        // Spent, Measuring (entering or leaving) and rollover (which always forces raw ==
        // Measuring while its gate holds) all bypass hysteresis: commit immediately.
        if (raw == QuotaState.Spent || raw == QuotaState.Measuring
            || Committed == QuotaState.Measuring || Committed == QuotaState.Spent)
        {
            CommitImmediately(raw, monoMs);
            return Committed;
        }

        if (raw == Committed)
        {
            ResetPendingEvidence();
            return Committed;
        }

        bool cooldownElapsed = LastChangeMono is not { } lastChange || (monoMs - lastChange) >= _cooldownMs;
        bool escalating = (int)raw > (int)Committed;

        if (escalating)
        {
            _pendingDeescalateState = null;
            _pendingDeescalateCount = 0;
            _pendingDeescalateSinceMono = null;

            _escalateHistory.Add(raw);
            if (_escalateHistory.Count > _escalateEvidenceCount) _escalateHistory.RemoveAt(0);

            bool allAboveCommitted = _escalateHistory.Count == _escalateEvidenceCount
                && _escalateHistory.All(s => (int)s > (int)Committed);
            if (allAboveCommitted && cooldownElapsed)
            {
                QuotaState target = (QuotaState)_escalateHistory.Min(s => (int)s);
                CommitImmediately(target, monoMs);
            }
        }
        else
        {
            _escalateHistory.Clear();

            if (_pendingDeescalateState != raw)
            {
                _pendingDeescalateState = raw;
                _pendingDeescalateCount = 1;
                _pendingDeescalateSinceMono = monoMs;
            }
            else
            {
                _pendingDeescalateCount++;
            }

            bool enoughEvidence = _pendingDeescalateCount >= _deescalateEvidenceCount;
            bool enoughTime = _pendingDeescalateSinceMono is { } since && (monoMs - since) >= _deescalateMinElapsedMs;
            if (enoughEvidence && enoughTime && cooldownElapsed)
            {
                CommitImmediately(raw, monoMs);
            }
        }

        return Committed;
    }

    /// <summary>
    /// Decision 4 (round 2): leaving Measuring on live time/eligibility alone, with no new
    /// accepted sample -- the rollover/weekly-24h gates lifting purely with elapsed time, or a
    /// WarmStart-seeded window whose only live poll so far was deduped. Bypasses all evidence,
    /// exactly like Step()'s own "leaving Measuring commits immediately" branch, but is safe to
    /// call from a pure evaluation tick (unlike Step(), which would otherwise treat a
    /// non-Measuring Committed's raw as escalate/de-escalate evidence on every 1s tick, in
    /// violation of decision 11). A no-op unless Committed is currently Measuring.
    /// </summary>
    public QuotaState CommitFromMeasuring(QuotaState raw, long monoMs)
    {
        if (Committed != QuotaState.Measuring) return Committed;
        CommitImmediately(raw, monoMs);
        return Committed;
    }

    void ResetPendingEvidence()
    {
        _escalateHistory.Clear();
        _pendingDeescalateState = null;
        _pendingDeescalateCount = 0;
        _pendingDeescalateSinceMono = null;
    }

    void CommitImmediately(QuotaState state, long monoMs)
    {
        Committed = state;
        LastChangeMono = monoMs;
        ResetPendingEvidence();
    }
}
