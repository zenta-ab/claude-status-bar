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
/// BlockedUntil is set only while some window is Spent.
/// </summary>
public sealed record QuotaView(
    WindowView Session,
    WindowView Weekly,
    Freshness Freshness,
    DateTimeOffset? LastChangedAt,
    DateTimeOffset? LastPollAt,
    TimeSpan PollInterval,
    string? Error,
    QuotaState IconSeverity,
    DateTimeOffset? BlockedUntil)
{
    public static QuotaView Initial { get; } = new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Unknown, LastChangedAt: null, LastPollAt: null,
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
