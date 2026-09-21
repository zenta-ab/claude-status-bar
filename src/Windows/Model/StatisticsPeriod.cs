namespace ClaudeStatusBar.Model;

/// <summary>docs/statistics.md, decision 4: the statistics window's three fixed period presets -- never a zoomable chart. Drives both which cycles/hours feed the named tiles/heatmap and, via StatisticsEngine's own N-gating, how quickly a short period's tiles clear their learning state.</summary>
public enum StatisticsPeriod { TwoWeeks, ThreeMonths, Year }
