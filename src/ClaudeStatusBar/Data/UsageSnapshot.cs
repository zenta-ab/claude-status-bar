namespace ClaudeStatusBar.Data;

/// <summary>
/// A single parsed get_usage response. Utilization values are the raw 0-100 percent
/// the protocol reports (49, 69.0, ...) -- NOT 0-1 fractions; callers building a
/// GaugeModel divide by 100. WeeklyUtilization/WeeklyResetsAt are null when the
/// "all models" weekly node could not be located (protocol drift) -- the ring then
/// simply renders empty rather than the app crashing or restart-looping.
/// </summary>
public sealed record UsageSnapshot(
    double SessionUtilization,
    string? SessionResetsAt,
    double? WeeklyUtilization,
    string? WeeklyResetsAt,
    DateTimeOffset ReceivedAt);
