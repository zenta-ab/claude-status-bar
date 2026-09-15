using System.Text.Json;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Parses a control_response envelope into a UsageSnapshot. The rate_limits
/// container's exact shape is undocumented and has moved before, so this never
/// hardcodes a path -- it depth-first-searches the tree by key substring and
/// always reads numbers with GetDouble() (the same field arrives as JSON int in
/// some responses and JSON float in others). Any shape surprise is reported as a
/// null field or an error string, never an exception: a parse failure must
/// degrade the gauge visibly, not crash or restart-loop the app.
/// </summary>
public static class UsageParser
{
    public static UsageSnapshot? TryParse(JsonElement controlResponse, out string? error)
    {
        try
        {
            if (!controlResponse.TryGetProperty("response", out var response))
            {
                error = "control_response had no 'response' property";
                return null;
            }

            var matches = new List<(string Key, JsonElement Value)>();
            CollectBySubstring(response, "five_hour", matches);
            if (matches.Count == 0)
            {
                error = "no five_hour node found anywhere in response";
                return null;
            }

            var fiveHour = matches[0].Value; // first hit in DFS order, per measured protocol behaviour
            double sessionUtil = ReadUtilization(fiveHour);
            string? sessionResets = ReadResetsAt(fiveHour);

            var weekly = FindWeeklyAllModelsNode(response);
            double? weeklyUtil = weekly is { } w ? ReadUtilization(w) : null;
            string? weeklyResets = weekly is { } w2 ? ReadResetsAt(w2) : null;

            error = weekly is null ? "no weekly 'all models' node found (ring will render empty)" : null;
            return new UsageSnapshot(sessionUtil, sessionResets, weeklyUtil, weeklyResets, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    static double ReadUtilization(JsonElement node) =>
        node.TryGetProperty("utilization", out var u) ? u.GetDouble() : 0.0;

    static string? ReadResetsAt(JsonElement node) =>
        node.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

    /// <summary>
    /// Heuristic: among every "seven_day"-ish node, prefer one whose key carries no
    /// per-model qualifier (opus/sonnet/haiku) -- that is the "all models" bucket the
    /// ring is defined against. Falls back to the first match if every candidate is
    /// qualified, so the ring still renders something rather than nothing.
    /// </summary>
    static JsonElement? FindWeeklyAllModelsNode(JsonElement response)
    {
        var matches = new List<(string Key, JsonElement Value)>();
        CollectBySubstring(response, "seven_day", matches);
        if (matches.Count == 0) return null;

        foreach (var (key, value) in matches)
        {
            if (!ContainsModelQualifier(key)) return value;
        }
        return matches[0].Value;
    }

    static bool ContainsModelQualifier(string key) =>
        key.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("haiku", StringComparison.OrdinalIgnoreCase);

    /// <summary>Preorder depth-first walk; collects every object whose owning key contains needle.</summary>
    static void CollectBySubstring(JsonElement element, string needle, List<(string Key, JsonElement Value)> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    results.Add((prop.Name, prop.Value));
                CollectBySubstring(prop.Value, needle, results);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectBySubstring(item, needle, results);
        }
    }
}
