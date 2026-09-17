using ClaudeStatusBar.Data;

namespace ClaudeStatusBar.Model;

/// <summary>
/// Composes WindowTracker (ingest/estimator), HysteresisHold (state machine)
/// and FreshnessOracle into the IQuotaModel contract. Owned and called on the
/// UI thread only, per the interface's doc comment.
///
/// Poll-rate work (raw classification, hysteresis stepping, success/failure
/// bookkeeping, freshness-deadline freezing, the clock anchor) happens once
/// per Ingest/IngestFailure call. Evaluate() re-reads live-computable numbers
/// (E, t_reset, r, grace, the clock-discontinuity check) against
/// already-committed/frozen state -- cheap enough for the 1 s UI tick. Round 2
/// (decision 4) adds exactly one exception to "never re-runs hysteresis on the
/// 1 s tick": a window stuck in Measuring is allowed to commit its very first
/// verdict on live time/eligibility alone (HysteresisHold.CommitFromMeasuring),
/// since that is a direct, un-debounced transition (like Spent or grace), not
/// evidence accumulation -- see MaybeCommitFromMeasuring below.
/// </summary>
public sealed class QuotaModel : IQuotaModel
{
    const double SessionHalfLifeMinutes = 15.0;
    const double WeeklyHalfLifeMinutes = 8.0 * 60.0;

    static readonly TimeSpan BurningWindow = TimeSpan.FromMinutes(10);
    static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(150);
    static readonly TimeSpan BurningInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan SpentInterval = TimeSpan.FromSeconds(300);
    static readonly TimeSpan AwaitingResetInterval = TimeSpan.FromSeconds(30); // decision 4 (round 1)
    static readonly TimeSpan ResetPollGrace = TimeSpan.FromSeconds(3);
    static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1); // floor so the UI timer never gets Interval <= 0
    static readonly TimeSpan ClockJumpThreshold = TimeSpan.FromSeconds(60); // decision 6
    const double BackoffBaseSeconds = 60.0;
    const double BackoffCapSeconds = 600.0;

    readonly WindowTracker _session = new(QuotaWindows.SessionMinutes, SessionHalfLifeMinutes, isWeekly: false);
    readonly WindowTracker _weekly = new(QuotaWindows.WeeklyMinutes, WeeklyHalfLifeMinutes, isWeekly: true);
    readonly HysteresisHold _sessionHold = new(weekly: false);
    readonly HysteresisHold _weeklyHold = new(weekly: true);

    bool _everSucceeded;
    bool _liveDataIngested; // decision 6 (round 2): consumed only by an ACCEPTED (usable) live ingest, not merely a poll that round-tripped
    DateTimeOffset? _lastPollAt;
    int _consecutiveFailures;
    string? _lastError;

    bool _sessionValidLastPoll; // decision 13: was this window present/parseable in the latest successful poll
    bool _weeklyValidLastPoll;

    // Decision 3+5 (round 1) / decision 2 (round 2): freshness deadlines frozen -- in
    // MONOTONIC milliseconds, not UTC -- at the last ACCEPTED poll (never a duplicate), using
    // the POLICY interval only (no reset cap, no backoff). Only a newer accepted poll can move
    // them; monotonic time cannot be walked backward across them by a UTC clock adjustment.
    long? _freshnessStaleAtMono;
    long? _freshnessUnknownAtMono;

    // Decision 6 (round 1) / decision 2 (round 2): UTC<->monotonic anchor from the last
    // ACCEPTED LIVE sample only -- a duplicate/deduped reply must never re-anchor, or it
    // silently defeats clock-jump detection on the very next tick (review finding 2). Replay
    // never touches this (WarmStart calls WindowTracker.Ingest directly, bypassing
    // QuotaModel.Ingest).
    DateTimeOffset? _anchorUtc;
    long? _anchorMono;

    // Decision 2 (round 2): the latest monoMs the model has received via Ingest/Evaluate (NOT
    // IngestFailure -- the hard constraint names Ingest/Evaluate specifically), used so
    // NextPollDelay(utcNow) -- whose public signature carries no monoMs -- can still classify
    // "burning" against monotonic time instead of a UTC duration.
    long? _lastKnownMono;

    public void Ingest(UsageSnapshot snapshot, DateTimeOffset utcNow, long monoMs)
    {
        _lastKnownMono = monoMs;

        // Decision 3 (round 2): session validity requires IsValidPct too, exactly like
        // weekly -- an invalid utilization value must not retain a stale verdict and Live.
        bool sessionValid = QuotaTimeUtil.TryParseResetsAt(snapshot.SessionResetsAt, out _)
            && QuotaTimeUtil.IsValidPct(snapshot.SessionUtilization);
        bool weeklyValid = snapshot.WeeklyUtilization is { } wpct
            && QuotaTimeUtil.TryParseResetsAt(snapshot.WeeklyResetsAt, out _)
            && QuotaTimeUtil.IsValidPct(wpct);

        // Decision 13: a missing/invalid window this poll is never substituted with a
        // default (no more "?? 0.0") -- it simply is not fed to the tracker this round, and
        // Evaluate reports that window Measuring ("data saknas") instead of a stale/blended forecast.
        bool sessionAccepted = sessionValid && _session.Ingest(snapshot.SessionUtilization, snapshot.SessionResetsAt, utcNow, monoMs);
        bool weeklyAccepted = weeklyValid && _weekly.Ingest(snapshot.WeeklyUtilization!.Value, snapshot.WeeklyResetsAt, utcNow, monoMs);

        _sessionValidLastPoll = sessionValid;
        _weeklyValidLastPoll = weeklyValid;

        // Decision 11: hysteresis advances only on accepted (novel) observations -- a
        // duplicate/deduped/out-of-order sample must not count as evidence.
        if (sessionAccepted) StepHysteresis(_session, _sessionHold, utcNow, monoMs, isWeekly: false);
        if (weeklyAccepted) StepHysteresis(_weekly, _weeklyHold, utcNow, monoMs, isWeekly: true);

        bool anyAccepted = sessionAccepted || weeklyAccepted;
        if (anyAccepted) _liveDataIngested = true; // decision 6 (round 2): a wholly-unusable poll must not consume WarmStart eligibility

        _everSucceeded = true;
        _lastPollAt = utcNow;
        _consecutiveFailures = 0;
        _lastError = null;

        // Decision 2 (round 2): re-anchor and re-freeze freshness deadlines ONLY when this
        // poll actually delivered a genuinely novel observation. A cached duplicate must never
        // renew either -- doing so silently defeats clock-jump detection on the very next tick
        // and extends freshness/a confident verdict on data that is not actually new (review
        // finding 2's three examples).
        if (anyAccepted)
        {
            _anchorUtc = utcNow;
            _anchorMono = monoMs;

            TimeSpan cPolicy = PolicyInterval(utcNow);
            _freshnessStaleAtMono = monoMs + (long)(cPolicy.TotalMilliseconds * 3);
            _freshnessUnknownAtMono = monoMs + (long)(cPolicy.TotalMilliseconds * 10);
        }
    }

    public void IngestFailure(string error, DateTimeOffset utcNow, long monoMs)
    {
        _lastPollAt = utcNow;
        _lastError = error;
        _consecutiveFailures++;
        // Decision 14/6: a failure must NOT gate WarmStart (only an ACCEPTED Ingest does).
        // Decision 3+5/2: a failure must never move the frozen freshness deadlines or the anchor.
        // Decision 11: a failure neither counts as hysteresis evidence nor resets pending
        // evidence -- both follow simply by never touching those here.
        // _lastKnownMono is deliberately NOT updated here -- see its doc comment.
    }

    /// <summary>
    /// One-time warm start from window-shape.csv (docs/forecast-and-states.md,
    /// "Ingest" rule 6). Call exactly once, with the FIRST successful snapshot,
    /// BEFORE calling Ingest() with that same snapshot -- replay must reconstruct
    /// history older than the live sample, and WindowTracker's dt<=0 guard would
    /// otherwise drop every replayed row as "older than the last accepted sample".
    /// A no-op (never throws) once a LIVE, USABLE SAMPLE HAS BEEN ACCEPTED
    /// (decision 6/14, round 2: an initial transport failure -- or an initial
    /// response that failed EVERY window's own validity check, e.g. an
    /// unparseable resets_at -- does not consume this; WarmStart stays eligible
    /// for the first snapshot that actually contributes usable data), on a
    /// missing/locked CSV, or when a window's resets_at is not yet known.
    /// </summary>
    /// <param name="logDirOverride">
    /// docs/multi-account.md: AccountRuntime passes its own account's
    /// <c>logs\&lt;identityKey&gt;</c> directory here (identityKey = AccountIdentity.StateKey, the
    /// accountUuid+organizationUuid pair -- see that type's doc comment) so warm start replays
    /// that account's own history, never another account's, and never another plan under the
    /// same person's accountUuid. Null (the single-account default) keeps reading the top-level
    /// logs directory exactly as before.
    /// </param>
    public void WarmStart(UsageSnapshot snapshot, DateTimeOffset utcNow, string? csvPath = null, string? logDirOverride = null)
    {
        if (_liveDataIngested) return;

        // Deviation (Codex runtime review Medium #10, docs/reviews/2026-09-11-codex-runtime.md
        // Decisions): window-shape.csv is now rotated monthly by DiskLogSink, so the single
        // fixed path this used to read no longer exists. csvPath stays an exact single-file
        // override for tests (unchanged: CsvReplay.ReadRows on exactly that file); the
        // production default now reads the log directory and merges the current + previous
        // month's files, since a window can straddle a month boundary.
        IReadOnlyList<CsvReplay.RawRow> rows = csvPath is null
            ? CsvReplay.ReadRowsForWarmStart(logDirOverride ?? DefaultLogDir(), utcNow)
            : CsvReplay.ReadRows(csvPath);
        if (rows.Count == 0) return;

        ReplayWindow(_session, WindowKind.Session, snapshot.SessionResetsAt, rows, utcNow);
        ReplayWindow(_weekly, WindowKind.Weekly, snapshot.WeeklyResetsAt, rows, utcNow);
    }

    static void ReplayWindow(WindowTracker tracker, WindowKind kind, string? resetsAtRaw, IReadOnlyList<CsvReplay.RawRow> rows, DateTimeOffset utcNow)
    {
        if (!QuotaTimeUtil.TryParseResetsAt(resetsAtRaw, out DateTimeOffset resetsAt)) return;
        if (!QuotaTimeUtil.IsPlausibleResetsAt(resetsAt, utcNow)) return; // decision 10: never derive replay math from an extreme deadline

        // Decision 6 (round 2): pass the full-precision resets_at, not a truncated-to-the-
        // second key -- FilterForWindow now matches membership with the same +-120s jitter
        // tolerance a live poll uses (WindowTracker.JitterTolerance), not exact equality.
        IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(rows, kind, resetsAt, tracker.WindowMinutes, utcNow);
        foreach (CsvReplay.MatchedRow row in matched)
            tracker.Ingest(row.Pct, row.ResetsAtRaw, row.UtcIso, row.MonoMs, isReplay: true); // decision 7: UTC-only, atomic accept
    }

    static string DefaultLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");

    static void StepHysteresis(WindowTracker tracker, HysteresisHold hold, DateTimeOffset utcNow, long monoMs, bool isWeekly)
    {
        WindowSnapshot snap = tracker.ComputeSnapshot(utcNow, monoMs, isWeekly);
        QuotaState raw = snap.Forecast is { } f
            ? RawStateClassifier.Classify(f.UsedPct, f, tracker.WindowMinutes, snap.Refused)
            : QuotaState.Measuring;
        hold.Step(raw, monoMs);
    }

    public QuotaView Evaluate(DateTimeOffset utcNow, long monoMs)
    {
        _lastKnownMono = monoMs;

        TimeSpan pollInterval = ComputePollInterval(utcNow);
        bool clockJump = DetectClockDiscontinuity(utcNow, monoMs);

        WindowSnapshot sessionSnap = _session.ComputeSnapshot(utcNow, monoMs, isWeekly: false);
        WindowSnapshot weeklySnap = _weekly.ComputeSnapshot(utcNow, monoMs, isWeekly: true);

        // Decision 4 (round 2): a window stuck in Measuring commits its first verdict the
        // instant it is no longer refused, even with no new accepted sample -- covers the
        // rollover/weekly-24h gates lifting purely with elapsed time, and a WarmStart-seeded
        // window whose only live poll so far was deduped by fingerprint. Skipped during a
        // clock jump (the snapshot's own E/t_reset are themselves suspect then) and while this
        // window's latest poll was invalid (nothing new to trust yet either way).
        if (!clockJump && _sessionValidLastPoll) MaybeCommitFromMeasuring(_sessionHold, sessionSnap, QuotaWindows.SessionMinutes, monoMs);
        if (!clockJump && _weeklyValidLastPoll) MaybeCommitFromMeasuring(_weeklyHold, weeklySnap, QuotaWindows.WeeklyMinutes, monoMs);

        QuotaState sessionFinal = clockJump ? QuotaState.Measuring : FinalState(_sessionHold, sessionSnap, _sessionValidLastPoll);
        QuotaState weeklyFinal = clockJump ? QuotaState.Measuring : FinalState(_weeklyHold, weeklySnap, _weeklyValidLastPoll);

        MeasuringReasonCode? sessionForcedReason = clockJump ? MeasuringReasonCode.ClockJump
            : !_sessionValidLastPoll ? MeasuringReasonCode.DataMissing : null;
        MeasuringReasonCode? weeklyForcedReason = clockJump ? MeasuringReasonCode.ClockJump
            : !_weeklyValidLastPoll ? MeasuringReasonCode.DataMissing : null;

        WindowView sessionView = BuildView(WindowKind.Session, QuotaWindows.SessionMinutes, sessionSnap, sessionFinal, utcNow, sessionForcedReason);
        WindowView weeklyView = BuildView(WindowKind.Weekly, QuotaWindows.WeeklyMinutes, weeklySnap, weeklyFinal, utcNow, weeklyForcedReason);

        QuotaState severity = (QuotaState)Math.Max((int)sessionFinal, (int)weeklyFinal);

        DateTimeOffset? blockedUntil = null;
        if (sessionFinal == QuotaState.Spent && sessionSnap.ResetsAt is { } sbu) blockedUntil = sbu;
        if (weeklyFinal == QuotaState.Spent && weeklySnap.ResetsAt is { } wbu)
            blockedUntil = blockedUntil is null || wbu > blockedUntil ? wbu : blockedUntil;

        var freshnessInputs = new FreshnessInputs(
            _everSucceeded, _freshnessStaleAtMono, _freshnessUnknownAtMono,
            _anchorMono, sessionSnap.ResetsAt, weeklySnap.ResetsAt, clockJump);
        Freshness freshness = FreshnessOracle.Evaluate(freshnessInputs, utcNow, monoMs);

        // Decision 13: global Live requires a valid session window.
        if (freshness == Freshness.Live && !_sessionValidLastPoll) freshness = Freshness.Stale;

        return new QuotaView(
            sessionView, weeklyView, freshness,
            LastChangedAt: _session.LastAcceptedUtc,
            LastPollAt: _lastPollAt,
            PollInterval: pollInterval,
            Error: _lastError,
            IconSeverity: severity,
            BlockedUntil: blockedUntil);
    }

    /// <summary>See the class doc comment and HysteresisHold.CommitFromMeasuring. A no-op unless the hold is currently Measuring AND the snapshot is no longer refused.</summary>
    static void MaybeCommitFromMeasuring(HysteresisHold hold, WindowSnapshot snap, double windowMinutes, long monoMs)
    {
        if (hold.Committed != QuotaState.Measuring) return;
        if (snap.Refused || snap.Forecast is not { } f) return;
        QuotaState raw = RawStateClassifier.Classify(f.UsedPct, f, windowMinutes, refused: false);
        hold.CommitFromMeasuring(raw, monoMs);
    }

    /// <summary>
    /// The committed verdict, with grace applied live, EXCEPT for two conditions that
    /// override it straight to Measuring without going through hysteresis at all --
    /// both are live, time-based facts rather than sample-based verdicts, so (like
    /// Measuring itself) there is nothing to debounce: decision 13's per-poll validity,
    /// and the added "awaiting reset" condition (resumed/cached data still describing an
    /// already-passed window -- "sleep across a reset").
    /// </summary>
    static QuotaState FinalState(HysteresisHold hold, WindowSnapshot snap, bool validLastPoll)
    {
        if (!validLastPoll) return QuotaState.Measuring;
        if (snap.Refused && snap.ReasonCode == MeasuringReasonCode.AwaitingReset) return QuotaState.Measuring;
        return snap.Forecast is { } f ? GraceCap.Apply(hold.Committed, f.MinutesToReset) : hold.Committed;
    }

    static WindowView BuildView(WindowKind kind, double windowMinutes, WindowSnapshot snap, QuotaState finalState, DateTimeOffset utcNow, MeasuringReasonCode? forcedReason)
    {
        bool measuring = finalState == QuotaState.Measuring;
        MeasuringReasonCode reasonCode = forcedReason ?? snap.ReasonCode;

        if (snap.Forecast is not { } f)
        {
            return WindowView.Empty(kind, windowMinutes) with
            {
                ResetsAt = snap.ResetsAt,
                State = finalState,
                MeasuringReason = measuring ? ReasonText(reasonCode) : null,
            };
        }

        // Decision 12: ALL forecast display fields are null whenever the final state is
        // Measuring, regardless of whether the underlying ForecastResult carries numbers --
        // a window can be Measuring for a reason unrelated to the estimator (rollover gate,
        // awaiting reset, data missing, clock jump) while still holding a stale/meaningless
        // rate. % and the countdown (ResetsAt) still show, per the doc's refusal rule.
        // Decision 11 (round 2, "partial"): Shortfall stays non-nullable for contract
        // stability -- TimeSpan.Zero here is a fixed placeholder, meaningless (not "no
        // blockage") while State is Measuring. Every renderer already gates display on State.
        if (measuring)
        {
            return new WindowView(
                kind, windowMinutes, f.UsedPct, snap.ResetsAt, finalState,
                RatePctPerMin: null, PaceMultiple: null, ProjectedPctAtReset: null,
                DepletesAt: null, Shortfall: TimeSpan.Zero,
                MeasuringReason: ReasonText(reasonCode));
        }

        DateTimeOffset? depletesAt = QuotaTimeUtil.SafeAddMinutes(utcNow, f.MinutesToDeplete); // decision 9: guarded, never throws
        return new WindowView(
            kind, windowMinutes, f.UsedPct, snap.ResetsAt, finalState,
            RatePctPerMin: f.RatePctPerMin, PaceMultiple: f.Pace, ProjectedPctAtReset: f.ProjectedPctAtReset,
            DepletesAt: depletesAt, Shortfall: TimeSpan.FromMinutes(f.ShortfallMinutes), MeasuringReason: null);
    }

    static string ReasonText(MeasuringReasonCode reason) => reason switch
    {
        MeasuringReasonCode.Rollover => "Nytt fönster, mäter takt…",
        MeasuringReasonCode.TooEarly => "För tidigt att mäta takt…",
        MeasuringReasonCode.TooEarlyInWeek => "För tidigt i veckan — väntar på ett helt dygn",
        MeasuringReasonCode.TooLittleUsage => "För lite förbrukning ännu…",
        MeasuringReasonCode.NoData => "Hämtar…",
        MeasuringReasonCode.AwaitingReset => "Nytt fönster väntas",
        MeasuringReasonCode.DataMissing => "Data saknas",
        MeasuringReasonCode.ClockJump => "Klockan ändrades, mäter om…",
        _ => "Mäter takt…",
    };

    /// <summary>Decision 6: is utcNow consistent with elapsed monotonic time since the last accepted live sample?</summary>
    bool DetectClockDiscontinuity(DateTimeOffset utcNow, long monoMs)
    {
        if (_anchorUtc is not { } anchorUtc || _anchorMono is not { } anchorMono) return false;
        DateTimeOffset expected = anchorUtc + TimeSpan.FromMilliseconds(monoMs - anchorMono);
        return Math.Abs((utcNow - expected).TotalSeconds) > ClockJumpThreshold.TotalSeconds;
    }

    public TimeSpan NextPollDelay(DateTimeOffset utcNow) => ComputePollInterval(utcNow);

    TimeSpan ComputePollInterval(DateTimeOffset utcNow)
    {
        TimeSpan baseInterval = _consecutiveFailures > 0
            ? BackoffInterval()
            : PolicyInterval(utcNow);

        return CapToSoonestReset(baseInterval, utcNow);
    }

    TimeSpan BackoffInterval()
    {
        double backoffSeconds = Math.Min(BackoffBaseSeconds * Math.Pow(2, _consecutiveFailures - 1), BackoffCapSeconds);
        return TimeSpan.FromSeconds(backoffSeconds);
    }

    /// <summary>
    /// The base poll-rate policy: awaiting-reset/Spent/burning/idle, WITHOUT the
    /// soonest-reset cap and WITHOUT failure backoff (decisions 3+5 and 4, round 1). Used
    /// both for NextPollDelay's non-failure base interval and to freeze freshness deadlines
    /// at each accepted poll.
    ///
    /// Decision 8 (round 2): awaiting-reset is checked BEFORE Spent. An expired Spent window
    /// (its own resets_at has already passed) must recover at the fast 30s cadence, not stay
    /// pinned to the slow 300s Spent interval for a window that is not actually current
    /// anymore -- the 300s interval is reserved for an ACTUALLY CURRENT blocking window.
    /// </summary>
    TimeSpan PolicyInterval(DateTimeOffset utcNow)
    {
        if (IsAwaitingReset(utcNow)) return AwaitingResetInterval;

        if (_sessionHold.Committed == QuotaState.Spent || _weeklyHold.Committed == QuotaState.Spent)
            return SpentInterval;

        return IsBurning() ? BurningInterval : IdleInterval;
    }

    bool IsAwaitingReset(DateTimeOffset utcNow) =>
        (_session.ResetsAt is { } sr && QuotaTimeUtil.SafeAdd(sr, ResetPollGrace) is { } srg && utcNow >= srg) ||
        (_weekly.ResetsAt is { } wr && QuotaTimeUtil.SafeAdd(wr, ResetPollGrace) is { } wrg && utcNow >= wrg);

    /// <summary>Decision 2 (round 2): monotonic, using the latest monoMs the model has received via Ingest/Evaluate -- never a UTC duration against LastRiseAt.</summary>
    bool IsBurning()
    {
        if (_lastKnownMono is not { } m) return false;
        long windowMs = (long)BurningWindow.TotalMilliseconds;
        return (_session.LastRiseMono is { } sr && (m - sr) <= windowMs)
            || (_weekly.LastRiseMono is { } wr && (m - wr) <= windowMs);
    }

    /// <summary>
    /// Decision 4 (round 1): only a deadline still AHEAD of us can cap the interval, and only down
    /// to reset+3s -- once that instant has passed, it never caps anything again (that's
    /// what IsAwaitingReset/PolicyInterval takes over for). This is what turns the old
    /// "NextPollDelay returns 1s forever after a reset" hot loop into a single poll landing
    /// exactly at reset+3s, then a steady 30s cadence.
    /// </summary>
    TimeSpan CapToSoonestReset(TimeSpan interval, DateTimeOffset utcNow)
    {
        DateTimeOffset? soonest = null;
        if (_session.ResetsAt is { } sr && QuotaTimeUtil.SafeAdd(sr, ResetPollGrace) is { } srg
            && srg > utcNow && (soonest is null || srg < soonest)) soonest = srg;
        if (_weekly.ResetsAt is { } wr && QuotaTimeUtil.SafeAdd(wr, ResetPollGrace) is { } wrg
            && wrg > utcNow && (soonest is null || wrg < soonest)) soonest = wrg;
        if (soonest is null) return interval;

        TimeSpan untilCap = soonest.Value - utcNow; // always > 0: soonest was required to be > utcNow above
        TimeSpan effective = untilCap < interval ? untilCap : interval;
        return effective < MinPollInterval ? MinPollInterval : effective;
    }
}
