namespace ClaudeStatusBar.Data;

/// <summary>
/// A single parsed get_usage response. Utilization values are the raw 0-100 percent
/// the protocol reports (49, 69.0, ...) -- NOT 0-1 fractions; callers building a
/// GaugeModel divide by 100. WeeklyUtilization/WeeklyResetsAt are null when the
/// "all models" weekly node could not be located (protocol drift) -- the ring then
/// simply renders empty rather than the app crashing or restart-looping.
///
/// SubscriptionType is the plan (max, team, ...) this response's account is on
/// (docs/multi-account.md), carried through purely for account labelling
/// (Model/AccountLabel.cs) -- it is deliberately NOT part of QuotaModel's ingest
/// math or QuotaView, so the model contract stays unchanged. Defaulted so every
/// existing positional `new UsageSnapshot(...)` call site keeps compiling.
///
/// SessionIsActive/WeeklyIsActive: whether the server says this window is
/// currently open (docs/forecast-and-states.md, "A closed window is an answer,
/// not an absence"). A 5 h session that has simply expired -- or a weekly window
/// just past its reset with nothing consumed -- comes back as {utilization: 0,
/// resets_at: null, is_active: false}: a real, meaningful state, not a broken
/// response. Default true so every existing positional call site (which never
/// mentions this field, and always supplies a resets_at when it means "open")
/// keeps compiling and keeps its old meaning.
/// </summary>
public sealed record UsageSnapshot(
    double SessionUtilization,
    string? SessionResetsAt,
    double? WeeklyUtilization,
    string? WeeklyResetsAt,
    DateTimeOffset ReceivedAt,
    string? SubscriptionType = null,
    bool SessionIsActive = true,
    bool WeeklyIsActive = true);
