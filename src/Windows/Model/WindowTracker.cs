namespace ClaudeStatusBar.Model;

/// <summary>Why a window is refused to Measuring, for the panel's forecast line.</summary>
public enum MeasuringReasonCode
{
    None,
    NoData,        // no sample ingested yet this window
    Rollover,      // less than 5 min since the window key last advanced
    TooEarly,      // E &lt; W/30 (session)
    TooEarlyInWeek, // E &lt; 24h (weekly only -- a partial day/night cycle skews the pace)
    TooLittleUsage, // P &lt; 3
    AwaitingReset, // resets_at has passed but no new window has been observed yet (review decision, "sleep across a reset")
    DataMissing,   // this window was absent/invalid in the latest successful poll (decision 13)
    /// The server reported this window as CLOSED: a valid percentage, no resets_at, is_active
    /// false. Distinct from DataMissing -- the data is present and says "nothing is running",
    /// which is an answer, not an absence (docs/forecast-and-states.md, "A closed window is an
    /// answer, not an absence").
    WindowInactive,
    ClockJump,     // utcNow is inconsistent with the monotonic anchor (decision 6)
}

/// <summary>Everything WindowTracker knows, evaluated fresh against utcNow. Never mutates state.</summary>
public readonly record struct WindowSnapshot(
    DateTimeOffset? ResetsAt,
    ForecastResult? Forecast,
    bool Refused,
    MeasuringReasonCode ReasonCode,
    int SampleCount);

/// <summary>
/// Per-window ingest: window-key rollover, fingerprint dedup, the monotone
/// envelope, and the ratio-EWMA rate accumulators. One instance per window
/// (session, weekly); see docs/forecast-and-states.md "Ingest" and "Estimator".
///
/// Ingest() is the only mutator, and accepts (mutates state) or rejects a
/// sample atomically: every timing/validity check runs before ANY field is
/// touched (envelope, fingerprint set, sample count, rise time included) --
/// review decision 7. It returns true iff the sample was accepted; callers
/// (QuotaModel) use that to drive hysteresis only from genuinely novel
/// observations (decision 11), never from duplicates or rejected samples.
///
/// ComputeSnapshot(utcNow, monoMs) is a pure read: E, t_reset, r and everything
/// derived from them are recomputed on every call, never accumulated (per the
/// doc: "recomputed on every use").
/// </summary>
public sealed class WindowTracker
{
    /// <summary>
    /// Decision 1: a deadline within this tolerance of the current one is the same window
    /// (jitter), not a rollover candidate. Also decision 6 (round 2): CsvReplay.FilterForWindow
    /// uses this exact same tolerance for replay-row membership, so a row a live poll would
    /// have accepted as "the same window" is never excluded from warm start on a sub-second/
    /// jitter technicality alone.
    /// </summary>
    public static readonly TimeSpan JitterTolerance = TimeSpan.FromSeconds(120);

    /// <summary>Decision 1's safety valve: an implausible later deadline seen this many consecutive times is accepted as a rollover anyway.</summary>
    const int ImplausibleRolloverSafetyValve = 3;

    /// <summary>Decision 10 (round 2, finding 7): the WTD floor decays with confirmed idle time (half-life-style softening, not a hard cutoff).</summary>
    const double FloorDecayMinutes = 30.0;

    /// <summary>
    /// The weekly window's own minimum elapsed time before a seed/verdict is trusted: a full
    /// day/night cycle (24h), not W/30 (336min). A Friday-morning-only pace read at ~5.6h in
    /// extrapolates straight through the coming nights and weekend and was producing false
    /// DryEarly alarms on day one of the week. Session keeps W/30.
    /// </summary>
    const double WeeklyMinElapsedMinutes = 1440.0;

    public double WindowMinutes { get; }
    public double Tau { get; }
    readonly bool _isWeekly;

    readonly HashSet<string> _seenFingerprints = new();

    public DateTimeOffset? WindowKey { get; private set; } // the tracked deadline, jitter-tolerant (decision 1)
    public DateTimeOffset? ResetsAt { get; private set; } // latest accepted full-precision resets_at seen this window
    public double EnvelopeP { get; private set; }
    public int SampleCount { get; private set; }
    public bool Seeded { get; private set; }
    public double Sp { get; private set; }
    public double St { get; private set; }
    public DateTimeOffset? LastAcceptedUtc { get; private set; }
    long? _lastAcceptedMono;
    public DateTimeOffset? RolloverAt { get; private set; }

    /// <summary>Decision 2/7 (round 2): monotonic anchor for the rollover gate, so it survives a UTC clock jump. Only set for a LIVE rollover -- a replay-driven one has no comparable monotonic epoch and falls back to UTC (ComputeSnapshot).</summary>
    public long? RolloverMono { get; private set; }

    public DateTimeOffset? LastRiseAt { get; private set; }

    /// <summary>Decision 2/7 (round 2): monotonic time of the last accepted LIVE rise (dP&gt;0). Never set by replay -- see the class doc's atomic-accept note on why replay stays UTC-only.</summary>
    public long? LastRiseMono { get; private set; }

    /// <summary>
    /// Decision 7 (round 2): idle minutes as of the LAST ACCEPTED LIVE observation, frozen at
    /// that accept -- never recomputed against the live evaluation clock. "No decay without
    /// evidence": a window with no live poll in a while must not look more relaxed just because
    /// time passed with nobody asking. Stays 0 (maximally protective) until a live sample has
    /// actually confirmed it.
    /// </summary>
    public double ConfirmedIdleMinutes { get; private set; }

    DateTimeOffset? _pendingRolloverKey;
    int _pendingRolloverCount;

    // ---- Cycle archive bookkeeping (docs/statistics.md), live-tracked only (never during
    // isReplay=true: warm start seeds the live envelope/rate state, not the archive -- the
    // archive's own history comes from the dedicated backfill pass over the same CSVs instead,
    // so replay must not produce a phantom closed cycle/hour or corrupt live coverage timing). ----

    /// <summary>
    /// Codex review #3/#17: the cadence-based continuity allowance. A gap between two
    /// consecutive polls (window coverage, hour coverage, OR the envelope/consumption
    /// re-baseline check below) no larger than this is genuine, continuous observation and
    /// counts in full; a gap LARGER than this contributes NOTHING -- never capped down to this
    /// value either (that used to fabricate up to 10 minutes of "coverage" for an outage of any
    /// length, and used to credit an unobserved jump in the envelope to whichever hour the app
    /// happened to resume in). Comfortably above every real poll cadence (idle 150s, spent 300s)
    /// but small enough that a genuine multi-hour gap still shows as (mostly) uncovered.
    /// </summary>
    const double ContinuityAllowanceMinutes = 10.0;

    double _coveredMinutes;
    DateTimeOffset? _lastPollUtcForCoverage;
    DateTimeOffset? _ceilingReachedAtUtc; // first UTC this window's envelope reached >=100
    bool _ceilingCensored; // Codex review #14: true when that crossing was only observed after a re-baseline gap -- the true crossing instant is unknown, somewhere inside the gap
    ClosedWindowInfo? _pendingClosedWindow;

    DateTimeOffset? _hourBucketStartUtc;
    DateTimeOffset? _hourLastPollUtc;
    double _hourConsumedPct;
    int _hourSamples;
    double _hourCoveredMinutes;
    ClosedHourInfo? _pendingClosedHour;

    /// <summary>
    /// docs/statistics.md: one closed WINDOW (session or weekly rollover), as WindowTracker
    /// itself can observe it -- everything the archive needs that is purely a function of this
    /// tracker's own envelope/timing. QuotaModel adds the calibration fields (plan tier,
    /// warned_dry_early, predicted_peak_pct) only it knows, and account_key, which belongs to
    /// whichever caller owns the account's identity.
    /// </summary>
    public readonly record struct ClosedWindowInfo(
        DateTimeOffset ResetUtc, double WindowMinutes, double PeakPct, double FinalPct,
        bool HitCeiling, double BlockedMinutes, double CoveredMinutes,
        DateTimeOffset? CeilingReachedAtUtc = null, bool CeilingReachedCensored = false);

    /// <summary>docs/statistics.md: one closed CLOCK HOUR for this window kind.</summary>
    public readonly record struct ClosedHourInfo(
        DateTimeOffset HourStartUtc, double ConsumedPct, int Samples, double CoveredMinutes);

    /// <summary>Returns and clears the window closed by the most recent Ingest call, if any.</summary>
    public ClosedWindowInfo? TakePendingClosedWindow()
    {
        ClosedWindowInfo? result = _pendingClosedWindow;
        _pendingClosedWindow = null;
        return result;
    }

    /// <summary>Returns and clears the clock hour closed by the most recent Ingest call, if any.</summary>
    public ClosedHourInfo? TakePendingClosedHour()
    {
        ClosedHourInfo? result = _pendingClosedHour;
        _pendingClosedHour = null;
        return result;
    }

    public WindowTracker(double windowMinutes, double halfLifeMinutes, bool isWeekly = false)
    {
        WindowMinutes = windowMinutes;
        Tau = halfLifeMinutes / Math.Log(2.0); // half-life -> tau: exp(-t/tau) = 0.5 at t = halfLife
        _isWeekly = isWeekly;
    }

    /// <summary>The minimum elapsed time before a seed (and later, a verdict) is trusted -- W/30 for session, a full 24h for weekly.</summary>
    double MinElapsedForSeedMinutes => _isWeekly ? WeeklyMinElapsedMinutes : WindowMinutes / 30.0;

    /// <summary>
    /// One poll's (or one replayed CSV row's) sample for this window. No-op when
    /// resetsAtRaw is null/unparseable (weekly node missing, malformed CSV row),
    /// resets_at is implausibly far from utcNow (decision 10, round 2 -- guards every
    /// derived deadline computation downstream against a garbled value), or the
    /// percentage is not finite/in [0,100] (decision 9) -- ingest must never throw.
    /// isReplay must be true for CSV warm-start rows (decision 7): replay uses UTC-only
    /// deltas and never persists a monotonic baseline, so the live epoch after replay is
    /// always established fresh, never mixed with a previous boot's monoMs. Returns true
    /// iff the sample was accepted (mutated tracker state) -- a duplicate fingerprint,
    /// out-of-order sample, or implausible/ignored window-key change all return false.
    /// </summary>
    public bool Ingest(double pct, string? resetsAtRaw, DateTimeOffset utcNow, long monoMs, bool isReplay = false)
    {
        if (!QuotaTimeUtil.TryParseResetsAt(resetsAtRaw, out DateTimeOffset resetsAt)) { ResetPendingRollover(); return false; }
        if (!QuotaTimeUtil.IsPlausibleResetsAt(resetsAt, utcNow)) { ResetPendingRollover(); return false; } // decision 10 (round 2)
        if (!QuotaTimeUtil.IsValidPct(pct)) { ResetPendingRollover(); return false; } // decision 9: reject before any mutation
        string fingerprint = resetsAtRaw!;

        // Decision 1 (round 2, finding 1): ClassifyWindowKey itself resets pending
        // safety-valve evidence on a SameWindow or RegressedIgnore outcome -- an
        // intervening non-matching reply (even one that ends up fingerprint-deduped
        // below) must break continuity, or non-consecutive candidate replies can
        // accumulate across replica-alternating polls into a destructive rollover.
        WindowKeyOutcome outcome = ClassifyWindowKey(resetsAt, utcNow);
        if (outcome is WindowKeyOutcome.RegressedIgnore or WindowKeyOutcome.ImplausibleIgnored) return false;

        // From here on we are committed to accepting the window-key change (if any); still
        // nothing else has mutated yet -- fingerprint dedup and the dt check come first.
        bool isRollover = outcome is WindowKeyOutcome.Rollover or WindowKeyOutcome.SafetyValveRollover;

        // Coverage/hour bookkeeping runs for every poll that reaches this point (Cold,
        // SameWindow or Rollover) -- including one that fingerprint dedup or the ordering
        // check below will still reject -- because a deduped/out-of-order reply still proves
        // the app was up and polling at utcNow. A RegressedIgnore/ImplausibleIgnored reply
        // (already returned above) is rarer replica noise that never advances any window's
        // clock anyway. Never runs during replay -- see the field block's doc comment.
        if (!isReplay) TrackPollForCoverageAndHour(utcNow, isRollover);

        // Fingerprint dedup only makes sense against the CURRENT window's set, which a
        // rollover is about to clear -- so a rollover's own fingerprint can never collide.
        if (!isRollover && _seenFingerprints.Contains(fingerprint)) return false; // stale cache / replica revert

        // Ordering check BEFORE any mutation (atomic accept, decision 7), and -- decision 1
        // (round 2) -- applied to EVERY accepted sample, rollover included: read against
        // LastAcceptedUtc/_lastAcceptedMono here, BEFORE ClearForRollover() wipes them, so a
        // rollover is validated against "a live timing baseline that survives window
        // clearing" instead of skipping the check entirely.
        double? dtMinutes = null;
        if (LastAcceptedUtc is { } lastUtc)
        {
            dtMinutes = isReplay ? (utcNow - lastUtc).TotalMinutes : ComputeDtMinutes(utcNow, monoMs);
            if (dtMinutes <= 0) return false; // out of order / clock reorder: reject entirely, no mutation
        }

        // -- accepted: now mutate. Clear first (it wipes the fingerprint set), THEN record
        // this sample's fingerprint -- otherwise a rollover's own fingerprint would be
        // recorded and immediately erased by the clear, leaving it un-deduped afterwards.
        if (isRollover)
        {
            ClearForRollover();
            RolloverAt = utcNow;
            RolloverMono = isReplay ? null : monoMs; // decision 2/7: no comparable monotonic epoch for a replay-driven rollover
        }
        _seenFingerprints.Add(fingerprint);
        WindowKey = resetsAt; // freshest known deadline, jitter-tolerant (decision 1: "keep the latest raw string")
        ResetPendingRollover();

        ResetsAt = resetsAt;

        // Codex review #3: the first observation of a window (LastAcceptedUtc is still null here
        // -- either truly cold, or just-rolled-over via ClearForRollover above), and the first
        // observation after a gap beyond the continuity allowance, re-baselines the envelope: the
        // jump is real (it still feeds EnvelopeP/PeakPct below) but WHEN within the unobserved
        // interval it actually happened is unknown, so it must never be credited to this poll's
        // clock hour as "consumption that happened now". Read BEFORE LastAcceptedUtc is
        // overwritten at the end of this method; correctly null after ClearForRollover ran above.
        bool rebaseline = LastAcceptedUtc is not { } lastAcceptedForGap
            || (utcNow - lastAcceptedForGap).TotalMinutes > ContinuityAllowanceMinutes;

        double previousP = EnvelopeP;
        EnvelopeP = Math.Max(EnvelopeP, pct); // monotone envelope: drops are replica skew, never applied
        double dP = EnvelopeP - previousP;
        if (dP > 0)
        {
            LastRiseAt = utcNow;
            if (!isReplay) LastRiseMono = monoMs;
        }
        if (!isReplay)
        {
            ConfirmedIdleMinutes = LastRiseMono is { } lrm ? Math.Max(0.0, (monoMs - lrm) / 60_000.0) : 0.0;

            // docs/statistics.md: hourly-*.csv's consumed_pct is the sum of positive envelope
            // deltas in the clock hour, samples counts accepted observations, and the ceiling
            // timestamp feeds cycles.csv's blocked_minutes. A re-baselining jump (see above)
            // contributes zero to _hourConsumedPct -- it still counts as a sample, though: the
            // app WAS observed to be running at utcNow, even if the size of the jump it revealed
            // cannot be attributed to this specific hour.
            if (!rebaseline) _hourConsumedPct += Math.Max(0.0, dP);
            _hourSamples++;
            if (_ceilingReachedAtUtc is null && EnvelopeP >= 100.0)
            {
                _ceilingReachedAtUtc = utcNow;
                _ceilingCensored = rebaseline; // Codex review #14: exact only under continuous observation, else interval-censored
            }
        }

        SampleCount++;

        if (!Seeded)
        {
            double e = WindowMinutes - (resetsAt - utcNow).TotalMinutes;
            if (e >= MinElapsedForSeedMinutes && EnvelopeP >= 3.0)
            {
                double m0 = Math.Min(e, Tau);
                double rWtd = e > 0 ? EnvelopeP / e : 0.0;
                Sp = rWtd * m0;
                St = m0;
                Seeded = true;
            }
        }
        else if (dtMinutes is { } dt)
        {
            double d = Math.Exp(-dt / Tau);
            Sp = Sp * d + dP;
            St = St * d + dt;
        }

        LastAcceptedUtc = utcNow;
        _lastAcceptedMono = isReplay ? null : monoMs; // decision 7: never persist a replay-era monotonic value
        return true;
    }

    void ResetPendingRollover()
    {
        _pendingRolloverKey = null;
        _pendingRolloverCount = 0;
    }

    /// <summary>
    /// docs/statistics.md: window-level coverage/closed-window and hour-bucket bookkeeping, run
    /// for every live poll that reaches this point in Ingest (see the call site's comment for
    /// exactly which outcomes that is). Two independent concerns share one method only because
    /// they are both driven by the same utcNow and the same "did a boundary just pass" question:
    /// </summary>
    void TrackPollForCoverageAndHour(DateTimeOffset utcNow, bool isRollover)
    {
        // ---- window-level: coverage minutes, and the just-closed window if this poll rolled over ----
        if (isRollover)
        {
            if (WindowKey is { } oldKey)
            {
                _pendingClosedWindow = new ClosedWindowInfo(
                    // Codex review #15: FinalPct is the final ENVELOPE value (EnvelopeP), never
                    // the raw last-accepted reading -- the envelope is exactly what the monotone-
                    // regression rule already exists to protect, so "final" must go through it too.
                    ResetUtc: oldKey, WindowMinutes: WindowMinutes, PeakPct: EnvelopeP,
                    FinalPct: EnvelopeP, HitCeiling: EnvelopeP >= 100.0,
                    BlockedMinutes: _ceilingReachedAtUtc is { } ca ? Math.Max(0.0, (oldKey - ca).TotalMinutes) : 0.0,
                    CoveredMinutes: _coveredMinutes,
                    CeilingReachedAtUtc: _ceilingReachedAtUtc,
                    CeilingReachedCensored: _ceilingCensored);
            }
            _coveredMinutes = 0.0;
            _ceilingReachedAtUtc = null;
            _ceilingCensored = false;
            _lastPollUtcForCoverage = utcNow; // the new window's clock starts now
        }
        else
        {
            // Codex review #17: a gap no larger than the continuity allowance counts in full;
            // a LARGER gap contributes zero -- never capped down to the allowance, which used to
            // fabricate coverage for a real outage of any length.
            if (_lastPollUtcForCoverage is { } lastPoll && utcNow >= lastPoll)
            {
                double gapMinutes = (utcNow - lastPoll).TotalMinutes;
                if (gapMinutes <= ContinuityAllowanceMinutes) _coveredMinutes += gapMinutes;
            }
            if (_lastPollUtcForCoverage is null || utcNow >= _lastPollUtcForCoverage)
                _lastPollUtcForCoverage = utcNow;
        }

        // ---- clock-hour bucket: independent of rollover -- a window can roll over mid-hour
        // and the hour bucket keeps accumulating across it (docs/statistics.md, hourly-*.csv is
        // "per window kind and clock hour", not per window instance) ----
        DateTimeOffset hourStart = TruncateToHour(utcNow);
        if (_hourBucketStartUtc is { } currentHour && currentHour != hourStart)
        {
            _pendingClosedHour = new ClosedHourInfo(currentHour, _hourConsumedPct, _hourSamples, _hourCoveredMinutes);
            _hourBucketStartUtc = hourStart;
            _hourConsumedPct = 0.0;
            _hourSamples = 0;
            _hourCoveredMinutes = 0.0;
            _hourLastPollUtc = utcNow;
        }
        else
        {
            _hourBucketStartUtc ??= hourStart;
            if (_hourLastPollUtc is { } lastHourPoll && utcNow >= lastHourPoll)
            {
                double gapMinutes = (utcNow - lastHourPoll).TotalMinutes;
                if (gapMinutes <= ContinuityAllowanceMinutes) _hourCoveredMinutes += gapMinutes;
            }
            _hourLastPollUtc = utcNow;
        }
    }

    static DateTimeOffset TruncateToHour(DateTimeOffset utc)
    {
        DateTime u = utc.UtcDateTime;
        return new DateTimeOffset(new DateTime(u.Year, u.Month, u.Day, u.Hour, 0, 0, DateTimeKind.Utc));
    }

    enum WindowKeyOutcome { Cold, SameWindow, Rollover, ImplausibleIgnored, SafetyValveRollover, RegressedIgnore }

    /// <summary>
    /// Decision 1. A deadline within +-120s of the tracked one is the same window
    /// (jitter). A genuinely later deadline (&gt;120s ahead) is a rollover only if
    /// it is also plausible -- now is at least within 120s of the OLD deadline,
    /// i.e. the window was actually due to end. An implausible later deadline
    /// (too early to be real) is ignored, unless it has now recurred 3
    /// consecutive times (the safety valve), in which case it is accepted
    /// anyway. A deadline that regressed by more than 120s is replica noise on
    /// the key itself, never a rollover -- ignored unconditionally, as before.
    ///
    /// Round 2 (finding 1): SameWindow and RegressedIgnore both reset the
    /// pending safety-valve counter -- either one is proof the window has NOT
    /// actually ended, so an implausible candidate seen before it must not keep
    /// accumulating across it.
    /// </summary>
    WindowKeyOutcome ClassifyWindowKey(DateTimeOffset resetsAt, DateTimeOffset utcNow)
    {
        if (WindowKey is not { } currentKey) return WindowKeyOutcome.Cold;

        double diffSeconds = (resetsAt - currentKey).TotalSeconds;
        if (Math.Abs(diffSeconds) <= JitterTolerance.TotalSeconds)
        {
            ResetPendingRollover();
            return WindowKeyOutcome.SameWindow;
        }

        if (diffSeconds < 0)
        {
            ResetPendingRollover();
            return WindowKeyOutcome.RegressedIgnore; // regressed by >120s: noise on the key itself
        }

        // diffSeconds > 120: a later deadline. Plausible only once we're within 120s of the
        // OLD deadline (the window was actually due to end around now).
        bool plausible = utcNow >= currentKey - JitterTolerance;
        if (plausible) return WindowKeyOutcome.Rollover;

        if (_pendingRolloverKey is { } pending && Math.Abs((resetsAt - pending).TotalSeconds) <= JitterTolerance.TotalSeconds)
            _pendingRolloverCount++;
        else
        {
            _pendingRolloverKey = resetsAt;
            _pendingRolloverCount = 1;
        }

        return _pendingRolloverCount >= ImplausibleRolloverSafetyValve
            ? WindowKeyOutcome.SafetyValveRollover
            : WindowKeyOutcome.ImplausibleIgnored;
    }

    double ComputeDtMinutes(DateTimeOffset utcNow, long monoMs)
    {
        double utcDtSec = (utcNow - LastAcceptedUtc!.Value).TotalSeconds;
        if (_lastAcceptedMono is not { } lastMono) return utcDtSec / 60.0;

        double monoDtSec = (monoMs - lastMono) / 1000.0;

        // A negative monotonic delta cannot happen within one continuous live process --
        // Environment.TickCount64 only decreases across an actual reboot, which also means a
        // fresh process (a fresh, Cold WindowTracker) in practice, never a same-instance
        // transition. When it happens anyway (e.g. replaying two real days' worth of raw log
        // data spanning a genuine reboot through one tracker), mono is simply not comparable
        // across that gap -- fall back to the UTC delta instead of a monotonic value that is
        // nonsensical on its face, rather than letting it masquerade as "definitely out of
        // order".
        if (monoDtSec < 0) return utcDtSec / 60.0;

        double dtSec = Math.Abs(utcDtSec - monoDtSec) > 15.0 ? monoDtSec : utcDtSec;
        return dtSec / 60.0;
    }

    void ClearForRollover()
    {
        _seenFingerprints.Clear();
        ResetsAt = null;
        EnvelopeP = 0;
        SampleCount = 0;
        Seeded = false;
        Sp = 0;
        St = 0;
        LastAcceptedUtc = null;
        _lastAcceptedMono = null;
        LastRiseAt = null;
        LastRiseMono = null;
        ConfirmedIdleMinutes = 0.0;
        // RolloverAt/RolloverMono/WindowKey are set by the caller right after this.
    }

    /// <summary>Pure read: forecast + raw-classification inputs as of utcNow. Never mutates state.</summary>
    public WindowSnapshot ComputeSnapshot(DateTimeOffset utcNow, long monoMs, bool isWeekly)
    {
        if (ResetsAt is null)
            return new WindowSnapshot(null, null, Refused: true, MeasuringReasonCode.NoData, SampleCount);

        DateTimeOffset resetsAt = ResetsAt.Value;
        double tReset = (resetsAt - utcNow).TotalMinutes;
        double e = WindowMinutes - tReset;

        double rWtd = e > 0 ? EnvelopeP / e : 0.0;
        double rEwma = St > 0 ? Sp / St : 0.0;
        double r = isWeekly
            ? WeeklyRate(rWtd, rEwma)
            : Math.Max(rEwma, DecayedSessionFloor(rWtd)); // decision 10 (round1)/7 (round2): a decaying floor frozen at last live accept

        var forecast = ForecastMath.Evaluate(EnvelopeP, e, r, WindowMinutes, tReset);

        // Decision 2 (round 2): since-rollover uses a monotonic anchor when one exists (a live
        // rollover); a replay-driven rollover has no comparable monotonic epoch and falls back
        // to UTC (decision 7: replay is UTC-only, and by the time a live Evaluate runs after
        // warm start, the real wall clock has not itself jumped -- clock jumps are caught
        // separately by QuotaModel's own anchor check).
        double? sinceRolloverMinutes = RolloverMono is { } rm ? (monoMs - rm) / 60_000.0
            : RolloverAt is { } ra ? (utcNow - ra).TotalMinutes
            : null;

        MeasuringReasonCode reason = MeasuringReasonCode.None;
        bool refused = false;

        // Decision 12: seed eligibility is re-checked live, so time alone (not just a fresh
        // Ingest call) can satisfy the refusal rule's "no valid seed" clause once E/EnvelopeP
        // now qualify -- even if every poll since arrival was deduped as a duplicate.
        // The weekly window uses a 24h minimum instead of W/30 (336min): a partial day/night
        // cycle's pace extrapolates straight through the nights and weekend the 7-day budget
        // actually contains, producing false alarms early in the week.
        MeasuringReasonCode tooEarlyReason = _isWeekly ? MeasuringReasonCode.TooEarlyInWeek : MeasuringReasonCode.TooEarly;
        bool seedValidNow = e >= MinElapsedForSeedMinutes && EnvelopeP >= 3.0;
        if (!seedValidNow && SampleCount < 2) { refused = true; reason = tooEarlyReason; }
        if (e < MinElapsedForSeedMinutes) { refused = true; reason = tooEarlyReason; }
        if (EnvelopeP < 3.0) { refused = true; reason = MeasuringReasonCode.TooLittleUsage; }
        if (sinceRolloverMinutes is { } sr && sr < 5.0) { refused = true; reason = MeasuringReasonCode.Rollover; }

        // Added condition (review, "sleep across a reset"): the deadline has passed but no
        // later window has been observed yet -- the cached data still describes the old
        // window. Takes priority over the other reasons when true.
        if (resetsAt <= utcNow) { refused = true; reason = MeasuringReasonCode.AwaitingReset; }

        return new WindowSnapshot(resetsAt, forecast, refused, refused ? reason : MeasuringReasonCode.None, SampleCount);
    }

    double WeeklyRate(double rWtd, double rEwma)
    {
        double w = Math.Min(0.5, St / Tau);
        return (1 - w) * rWtd + w * rEwma;
    }

    /// <summary>
    /// Decision 7 (round 2): floor = r_wtd * exp(-idle / 30min), where idle is
    /// ConfirmedIdleMinutes -- frozen at the last accepted LIVE observation, never
    /// recomputed against the live evaluation clock. "There is no decay without
    /// evidence": silence alone (no further polls) must not relax the floor any
    /// further than it already was at the last confirming poll.
    /// </summary>
    double DecayedSessionFloor(double rWtd) => rWtd * Math.Exp(-ConfirmedIdleMinutes / FloorDecayMinutes);
}
