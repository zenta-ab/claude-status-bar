using System.Linq;
using System.Text.Json;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Parses a control_response envelope into a UsageSnapshot.
///
/// The rate_limits container's exact shape is undocumented and has moved before
/// (docs/mac-port.md, "the weekly-node heuristic is one key-reorder from wrong"): a response can
/// carry many `seven_day`-ish sibling keys (`seven_day_oauth_apps`, `seven_day_cowork`,
/// `seven_day_omelette`, `seven_day_breakdown`, ...), none of which carry an
/// opus/sonnet/haiku qualifier either -- so "first unqualified match" was saved only by wire
/// order, and one server-side key reorder away from reading the wrong bucket. That is exactly a
/// "confident wrong number".
///
/// Strategy, matching src/Mac/Sources/ClaudeQuotaCore/UsageParser.swift exactly (docs/mac-port.md
/// §"DIFFERS (latent bug, not yet firing)"):
///
///  1. **`limits[]` by `kind`** -- self-describing (`kind: "session"` / `kind: "weekly_all"`);
///     matches `five_hour` / `seven_day` bit-for-bit, including the microseconds, whenever present.
///  2. **The exact keys** `five_hour` and `seven_day` -- found by an exact (not substring) DFS
///     key match, so a sibling like `seven_day_cowork` can never be mistaken for `seven_day`.
///  3. **Cross-check.** When both 1 and 2 are present and usable they must agree (session
///     percentage within 0.5, matching resets_at fingerprint when both carry one; same for
///     weekly). A disagreement means the response is in a shape this parser does not understand --
///     refuse rather than guess (docs/forecast-and-states.md, "Refusal -> Measuring").
///  4. **The substring search**, last, and only when it is unambiguous -- unchanged in spirit
///     from before, but now refuses outright (no snapshot at all) rather than silently falling
///     back to a wire-order guess when more than one qualifier-less `seven_day*`/`five_hour*`
///     candidate is populated.
///
/// Any parse failure is reported as a null snapshot with an explanatory error, never an
/// exception: a parse failure must degrade the gauge visibly, not crash or restart-loop the app.
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

            string? subscriptionType = ReadSubscriptionType(response);

            Reading? fromLimits = ReadLimitsArray(response);
            Reading? fromExact = ReadExactKeys(response);

            if (fromLimits is { } limitsReading && fromExact is { } exactReading)
            {
                string? disagreement = Disagreement(limitsReading, exactReading);
                if (disagreement is not null)
                {
                    error = $"limits[] and the exact keys disagree ({disagreement}); refusing to guess";
                    return null;
                }
            }

            // Prefer limits[] but fill anything it omits from the exact keys -- already
            // cross-checked above, so this cannot import a contradicting value, and dropping a
            // deadline that one encoding carries would make a live window look closed, which
            // costs the forecast its clock.
            if (fromLimits is { } reading)
            {
                if (fromExact is { } exact) reading = Complete(reading, exact);
                error = null;
                return reading.ToSnapshot(subscriptionType);
            }
            if (fromExact is { } onlyExact)
            {
                error = null;
                return onlyExact.ToSnapshot(subscriptionType);
            }

            return ReadSubstringSearch(response, subscriptionType, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    // ---- one window's worth of readings ----

    readonly record struct Reading(
        double SessionPct, string? SessionResetsAt, bool SessionIsActive,
        double? WeeklyPct, string? WeeklyResetsAt, bool WeeklyIsActive)
    {
        public UsageSnapshot ToSnapshot(string? subscriptionType) => new(
            SessionPct, SessionResetsAt, WeeklyPct, WeeklyResetsAt, DateTimeOffset.UtcNow,
            subscriptionType, SessionIsActive, WeeklyIsActive);
    }

    /// <summary>Fills gaps in <paramref name="reading"/> from <paramref name="other"/> (the same
    /// response's other encoding). A reset present in either encoding means the window is open,
    /// whatever the other one omitted.</summary>
    static Reading Complete(Reading reading, Reading other)
    {
        Reading merged = reading;
        if (merged.SessionResetsAt is null && other.SessionResetsAt is not null)
            merged = merged with { SessionResetsAt = other.SessionResetsAt, SessionIsActive = true };
        if (merged.WeeklyPct is null)
            merged = merged with { WeeklyPct = other.WeeklyPct };
        if (merged.WeeklyResetsAt is null && other.WeeklyResetsAt is not null)
            merged = merged with { WeeklyResetsAt = other.WeeklyResetsAt, WeeklyIsActive = true };
        return merged;
    }

    /// <summary>Two encodings of one number. A difference of more than half a point, or a
    /// different reset fingerprint, means they are not describing the same thing.</summary>
    static string? Disagreement(Reading a, Reading b)
    {
        if (Math.Abs(a.SessionPct - b.SessionPct) > 0.5)
            return $"session {a.SessionPct} vs {b.SessionPct}";
        if (a.SessionResetsAt is not null && b.SessionResetsAt is not null && a.SessionResetsAt != b.SessionResetsAt)
            return $"session resets_at {a.SessionResetsAt} vs {b.SessionResetsAt}";
        if (a.WeeklyPct is { } aw && b.WeeklyPct is { } bw && Math.Abs(aw - bw) > 0.5)
            return $"weekly {aw} vs {bw}";
        if (a.WeeklyResetsAt is not null && b.WeeklyResetsAt is not null && a.WeeklyResetsAt != b.WeeklyResetsAt)
            return $"weekly resets_at {a.WeeklyResetsAt} vs {b.WeeklyResetsAt}";
        return null;
    }

    // ---- strategy 1: the self-describing limits[] array ----

    static Reading? ReadLimitsArray(JsonElement response)
    {
        if (!TryFindExactKey(response, "limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? session = null;
        JsonElement? weekly = null;
        // A duplicate `kind` means the response is malformed or ambiguous; taking the first and
        // ignoring the rest would be a silent choice between two different numbers.
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string? kind = entry.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
            switch (kind)
            {
                case "session":
                    if (session is not null) return null;
                    session = entry;
                    break;
                case "weekly_all":
                    if (weekly is not null) return null;
                    weekly = entry;
                    break;
                default: break; // weekly_scoped is a per-model bucket, never the "all models" ring
            }
        }

        if (session is not { } sessionEntry || ValidPercentage(sessionEntry, "percent") is not { } sessionPct)
            return null;
        string? sessionResets = ResetString(sessionEntry, "resets_at");
        // is_active is the server's own word for it; fall back to "has a deadline" when absent.
        bool sessionActive = ReadBoolean(sessionEntry, "is_active") ?? sessionResets is not null;

        // Weekly is validated independently: an invalid weekly must not invalidate a good
        // session reading, but it must not travel inside the snapshot either.
        double? weeklyPct = null;
        string? weeklyResets = null;
        bool weeklyActive = false;
        if (weekly is { } weeklyEntry && ValidPercentage(weeklyEntry, "percent") is { } pct)
        {
            weeklyPct = pct;
            weeklyResets = ResetString(weeklyEntry, "resets_at");
            weeklyActive = ReadBoolean(weeklyEntry, "is_active") ?? weeklyResets is not null;
        }

        return new Reading(sessionPct, sessionResets, sessionActive, weeklyPct, weeklyResets, weeklyActive);
    }

    // ---- strategy 2: the exact keys ----

    static Reading? ReadExactKeys(JsonElement response)
    {
        if (!TryFindExactKey(response, "five_hour", out var fiveHour) || fiveHour.ValueKind != JsonValueKind.Object)
            return null;
        if (ValidPercentage(fiveHour, "utilization") is not { } sessionPct) return null;
        string? sessionResets = ResetString(fiveHour, "resets_at");

        double? weeklyPct = null;
        string? weeklyResets = null;
        if (TryFindExactKey(response, "seven_day", out var sevenDay) && sevenDay.ValueKind == JsonValueKind.Object
            && ValidPercentage(sevenDay, "utilization") is { } pct)
        {
            weeklyPct = pct;
            weeklyResets = ResetString(sevenDay, "resets_at");
        }

        return new Reading(sessionPct, sessionResets, sessionResets is not null,
            weeklyPct, weeklyResets, weeklyResets is not null);
    }

    // ---- strategy 3: the substring search, and only when unambiguous ----

    static UsageSnapshot? ReadSubstringSearch(JsonElement response, string? subscriptionType, out string? error)
    {
        var sessionMatches = new List<(string Key, JsonElement Value)>();
        CollectBySubstring(response, "five_hour", sessionMatches);
        List<(string Key, JsonElement Value)> sessionObjects = sessionMatches
            .Where(m => m.Value.ValueKind == JsonValueKind.Object).ToList();

        if (sessionObjects.Count > 1)
        {
            error = $"ambiguous session buckets ({string.Join(", ", sessionObjects.Select(m => m.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal))}); refusing to guess";
            return null;
        }
        if (sessionObjects.Count == 0 || ValidPercentage(sessionObjects[0].Value, "utilization") is not { } sessionPct)
        {
            error = "no usable five_hour/session node in the response";
            return null;
        }
        string? sessionResets = ResetString(sessionObjects[0].Value, "resets_at");

        var weeklyMatches = new List<(string Key, JsonElement Value)>();
        CollectBySubstring(response, "seven_day", weeklyMatches);
        List<(string Key, JsonElement Value)> weeklyCandidates = weeklyMatches
            .Where(m => m.Value.ValueKind == JsonValueKind.Object && !ContainsModelQualifier(m.Key)).ToList();

        if (weeklyCandidates.Count > 1)
        {
            error = $"ambiguous weekly buckets ({string.Join(", ", weeklyCandidates.Select(m => m.Key).Distinct().OrderBy(k => k, StringComparer.Ordinal))}); refusing to guess";
            return null;
        }

        double? weeklyPct = null;
        string? weeklyResets = null;
        if (weeklyCandidates.Count == 1 && ValidPercentage(weeklyCandidates[0].Value, "utilization") is { } pct)
        {
            weeklyPct = pct;
            weeklyResets = ResetString(weeklyCandidates[0].Value, "resets_at");
        }

        error = "read via the substring fallback: neither limits[] nor the exact keys were usable";
        var reading = new Reading(sessionPct, sessionResets, sessionResets is not null,
            weeklyPct, weeklyResets, weeklyResets is not null);
        return reading.ToSnapshot(subscriptionType);
    }

    // ---- helpers ----

    /// <summary>
    /// docs/multi-account.md: get_usage carries subscription_type (max, team, ...) for the
    /// account that child is logged into -- used only for account labelling
    /// (Model/AccountLabel.cs), never for the quota model itself.
    /// </summary>
    static string? ReadSubscriptionType(JsonElement response)
    {
        var matches = new List<(string Key, JsonElement Value)>();
        CollectBySubstring(response, "subscription_type", matches);
        if (matches.Count == 0) return null;
        return matches[0].Value.ValueKind == JsonValueKind.String ? matches[0].Value.GetString() : null;
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

    /// <summary>Preorder depth-first walk for an EXACT (not substring) key match, so a sibling
    /// like `seven_day_cowork` can never be mistaken for `seven_day`. Returns the first hit in
    /// DFS/wire order, per the same "first hit" convention CollectBySubstring already used.</summary>
    static bool TryFindExactKey(JsonElement element, string name, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    result = prop.Value;
                    return true;
                }
            }
            foreach (var prop in element.EnumerateObject())
            {
                if (TryFindExactKey(prop.Value, name, out result)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindExactKey(item, name, out result)) return true;
            }
        }
        result = default;
        return false;
    }

    /// <summary>
    /// The same field arrives as a JSON int in some responses and a JSON float in others, always
    /// read with GetDouble(). A JSON boolean must NOT pass (System.Text.Json would otherwise
    /// coerce `true`/`false` into 1/0 via GetDouble() on some numeric-ish paths) -- a malformed
    /// `{"percent": true}` must never render as "1 % used", a confident wrong number.
    /// docs/forecast-and-states.md, round-1 decision 9: utilization must be finite and in
    /// [0, 100] at the boundary, or the window is treated as missing rather than as 0 %.
    /// </summary>
    static double? ValidPercentage(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        double d = v.GetDouble();
        return QuotaTimeUtil.IsValidPct(d) ? d : null;
    }

    /// <summary>
    /// Kept as the raw string from the wire: the exact byte sequence is the snapshot fingerprint
    /// the model dedups on. It is bounds-checked as a timestamp elsewhere but never reformatted
    /// here -- reformatting would destroy the microsecond digits that carry the signal.
    /// </summary>
    static string? ResetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? ReadBoolean(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;
}
