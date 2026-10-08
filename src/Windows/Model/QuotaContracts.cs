using ClaudeStatusBar.Data;

namespace ClaudeStatusBar.Model;

// Contract between the quota model (ingest, estimator, state machine, freshness)
// and everything that renders it (tray icon, panel). Spec: docs/forecast-and-states.md.

public enum WindowKind { Session, Weekly }

/// <summary>Ordered by severity. Measuring carries no verdict, so it ranks lowest.</summary>
public enum QuotaState { Measuring = 0, Safe = 1, Tight = 2, DryEarly = 3, Spent = 4 }

public enum Freshness { Live, Stale, Unknown }

public static class QuotaWindows
{
    public const double SessionMinutes = 300;
    public const double WeeklyMinutes = 10_080;
}

/// <summary>
/// One quota window as the UI should show it. The nullable forecast fields are null
/// while the window is Measuring. ProjectedPctAtReset is uncapped (renderers clamp
/// to 100). Shortfall is the time you would be blocked before the reset, zero when
/// safe -- EXCEPT while State is Measuring: round-2 decision 11 kept Shortfall
/// non-nullable for contract stability, so its TimeSpan.Zero there is a fixed
/// placeholder, not "no blockage". Every renderer already gates display on State
/// (a UI rule since panel v1); Shortfall must never be read on its own without
/// checking State first.
/// </summary>
public sealed record WindowView(
    WindowKind Kind,
    double WindowMinutes,
    double? UsedPct,
    DateTimeOffset? ResetsAt,
    QuotaState State,
    double? RatePctPerMin,
    double? PaceMultiple,
    double? ProjectedPctAtReset,
    DateTimeOffset? DepletesAt,
    TimeSpan Shortfall,
    string? MeasuringReason)
{
    public static WindowView Empty(WindowKind kind, double windowMinutes) => new(
        kind, windowMinutes, UsedPct: null, ResetsAt: null, QuotaState.Measuring,
        RatePctPerMin: null, PaceMultiple: null, ProjectedPctAtReset: null,
        DepletesAt: null, Shortfall: TimeSpan.Zero, MeasuringReason: "Hämtar…");
}

/// <summary>
/// Everything the icon and panel render. LastChangedAt is when a novel fingerprint
/// last arrived (the honest data age), not when the last poll round-tripped.
/// LastSuccessAt is when a poll last returned a reading ("Senast avläst"); failures never move it.
/// BlockedUntil is set only while some window is Spent.
///
/// Loading (docs/multi-account.md "Loading"): the account has not produced its first result yet (see
/// Model/LoadingTracker). Overlaid by AccountRuntime on an Unknown view so the icon, tooltip and panel say
/// "Hämtar kvoten…" instead of looking like an error.
///
/// NeedsLogin (docs/multi-account.md "NeedsLogin"): the account's login has expired or is missing.
/// It is not part of the model's own verdict -- AccountRuntime overlays it, together with
/// Freshness.Unknown (the grey ring with "!"), once a fresh child has confirmed the "not logged in"
/// answer. It is distinct from a plain Unknown, which means "could not read, will retry".
/// </summary>
public sealed record QuotaView(
    WindowView Session,
    WindowView Weekly,
    Freshness Freshness,
    DateTimeOffset? LastChangedAt,
    DateTimeOffset? LastSuccessAt,
    TimeSpan PollInterval,
    string? Error,
    QuotaState IconSeverity,
    DateTimeOffset? BlockedUntil,
    bool NeedsLogin = false,
    bool Loading = false)
{
    public static QuotaView Initial { get; } = new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, LastChangedAt: null, LastSuccessAt: null,
        PollInterval: TimeSpan.FromSeconds(30), Error: null,
        QuotaState.Measuring, BlockedUntil: null);
}

/// <summary>
/// Owned and called on the UI thread only. Ingest/IngestFailure run once per poll
/// result; Evaluate runs on the 1 s UI timer and must be cheap.
/// </summary>
public interface IQuotaModel
{
    void Ingest(UsageSnapshot snapshot, DateTimeOffset utcNow, long monoMs);
    void IngestFailure(string error, DateTimeOffset utcNow, long monoMs);
    QuotaView Evaluate(DateTimeOffset utcNow, long monoMs);
    TimeSpan NextPollDelay(DateTimeOffset utcNow);
}

/// <summary>
/// docs/statistics.md: one window (session or weekly) that just closed -- a rollover QuotaModel
/// itself observed live. Drain via QuotaModel.TakeClosedCycles() once after every Ingest call and
/// append each to that account's cycles.csv; the overwhelmingly common case is an empty list (a
/// session closes at most twice a day, weekly about once a week). PlanTier/WarnedDryEarly/
/// PredictedPeakPct/PredictedAtUtc are the calibration fields only QuotaModel can supply --
/// see docs/statistics.md for exactly when/how each is captured. AccountKey is deliberately not
/// here: it belongs to whichever caller owns the account's identity, not to the quota model.
/// </summary>
public sealed record CycleClosed(
    WindowKind Kind,
    DateTimeOffset StartedUtc,
    DateTimeOffset ResetUtc,
    double PeakPct,
    double FinalPct,
    bool HitCeiling,
    double BlockedMinutes,
    double CoveredMinutes,
    string? PlanTier,
    bool WarnedDryEarly,
    double? PredictedPeakPct,
    DateTimeOffset? PredictedAtUtc,
    DateTimeOffset? CeilingReachedAtUtc = null,
    bool CeilingReachedCensored = false);

/// <summary>docs/statistics.md: one clock hour that just closed for one window kind, drained the same way via TakeClosedHours() and appended to hourly-YYYY.csv.</summary>
public sealed record HourClosed(
    WindowKind Kind,
    DateTimeOffset HourStartUtc,
    double ConsumedPct,
    int Samples,
    double CoveredMinutes);
