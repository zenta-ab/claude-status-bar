using System.Text.Json;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class ParsingTests
{
    [Fact]
    public void QuotaTimeUtil_ParsesSixDigitFractionalResetsAt()
    {
        // The real wire format, e.g. "2026-09-11T11:20:00.524090+00:00" -- 6 fraction digits,
        // not the 7 .NET's own round-trip ("O") format writes.
        bool ok = QuotaTimeUtil.TryParseResetsAt("2026-09-11T11:20:00.524090+00:00", out DateTimeOffset parsed);
        Assert.True(ok);

        DateTimeOffset expected = new DateTimeOffset(2026, 9, 11, 11, 20, 0, TimeSpan.Zero).AddTicks(524_090 * 10); // 1 us = 10 ticks
        Assert.Equal(expected, parsed);

        Assert.Equal(new DateTimeOffset(2026, 9, 11, 11, 20, 0, TimeSpan.Zero), QuotaTimeUtil.TruncateToSecond(parsed));
    }

    [Fact]
    public void CsvReplay_ParsesBothIntegerAndFractionalUtilizationFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pct-parsing-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[]
        {
            "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source",
            "2026-09-11T08:28:20.4533557+00:00,27756093,13,2026-09-11T11:20:00.468249+00:00,3,2026-09-18T05:00:00.468314+00:00,1213.9,get_usage", // int-looking
            "2026-09-11T08:28:49.4858810+00:00,27785125,13.5,2026-09-11T11:20:00.528454+00:00,3.25,2026-09-18T05:00:00.528479+00:00,256.1,get_usage", // float-looking
        });

        try
        {
            IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(path);
            Assert.Equal(2, rows.Count);
            Assert.Equal(13.0, rows[0].SessionPct);
            Assert.Equal(13.5, rows[1].SessionPct);
            Assert.Equal(3.25, rows[1].WeeklyPct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UsageParser_ReadsUtilization_WhenJsonIsIntOrFloat()
    {
        // UsageParser.ReadUtilization always calls GetDouble(); both encodings must work.
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 49, "resets_at": "2026-09-11T11:20:00.524090+00:00" },
            "seven_day": { "utilization": 69.5, "resets_at": "2026-09-18T05:00:00.524116+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.Equal(49.0, snapshot!.SessionUtilization);   // JSON integer
        Assert.Equal(69.5, snapshot.WeeklyUtilization);     // JSON float
    }

    [Fact]
    public void UsageParser_ReadsSubscriptionType_ForAccountLabelling()
    {
        // docs/multi-account.md: subscription_type feeds AccountLabel, never the quota model
        // itself -- this only checks it is carried onto UsageSnapshot at all.
        string json = """
        {
          "response": {
            "subscription_type": "team",
            "five_hour": { "utilization": 10, "resets_at": "2026-09-11T11:20:00.524090+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out _);

        Assert.Equal("team", snapshot!.SubscriptionType);
    }

    [Fact]
    public void UsageParser_MissingSubscriptionType_LeavesItNull()
    {
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 10, "resets_at": "2026-09-11T11:20:00.524090+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out _);

        Assert.Null(snapshot!.SubscriptionType);
    }

    // ---- decision 10 (round 2): resets_at bounds and guarded deadline arithmetic ----

    [Fact]
    public void IsPlausibleResetsAt_AcceptsRealSessionAndWeeklyDeadlines_RejectsExtremeOnes()
    {
        DateTimeOffset now = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

        Assert.True(QuotaTimeUtil.IsPlausibleResetsAt(now.AddHours(3), now));   // a normal session deadline
        Assert.True(QuotaTimeUtil.IsPlausibleResetsAt(now.AddDays(6), now));    // a normal weekly deadline
        Assert.True(QuotaTimeUtil.IsPlausibleResetsAt(now.AddHours(-20), now)); // just-passed, still within a day (awaiting reset)

        Assert.False(QuotaTimeUtil.IsPlausibleResetsAt(DateTimeOffset.Parse("9999-12-31T23:59:59.000000+00:00"), now));
        Assert.False(QuotaTimeUtil.IsPlausibleResetsAt(DateTimeOffset.Parse("0001-01-01T00:00:00.000000+00:00"), now));
        Assert.False(QuotaTimeUtil.IsPlausibleResetsAt(now.AddDays(9), now));
        Assert.False(QuotaTimeUtil.IsPlausibleResetsAt(now.AddDays(-2), now));
    }

    [Fact]
    public void SafeAdd_GuardsOverflow_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(QuotaTimeUtil.SafeAdd(DateTimeOffset.MaxValue, TimeSpan.FromSeconds(3)));
        Assert.Null(QuotaTimeUtil.SafeAdd(DateTimeOffset.MinValue, TimeSpan.FromSeconds(-3)));

        DateTimeOffset now = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddSeconds(3), QuotaTimeUtil.SafeAdd(now, TimeSpan.FromSeconds(3)));
    }

    // ==================== A2: weekly/session node selection order (2026-09-21) ====================
    // docs/mac-port.md, "the weekly-node heuristic is one key-reorder from wrong": rate_limits
    // carries many seven_day-ish siblings with no model qualifier either
    // (seven_day_oauth_apps/cowork/omelette/breakdown), so "first unqualified match" was only
    // ever saved by wire order. Fix order, matching src/Mac's UsageParser exactly: limits[] by
    // kind, then the exact keys (exact match, not substring), then the substring search last.

    [Fact]
    public void UsageParser_ExactKeys_QualifierLessDecoyBeforeSevenDay_StillPicksSevenDay()
    {
        // seven_day_cowork carries no opus/sonnet/haiku qualifier either, and comes BEFORE
        // seven_day on the wire here -- the old substring-DFS heuristic would have picked it.
        // The exact-key match must not be fooled by wire order at all.
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 10, "resets_at": "2026-09-11T11:20:00.524090+00:00" },
            "seven_day_cowork": { "utilization": 99, "resets_at": "2026-09-18T05:00:00.999999+00:00" },
            "seven_day": { "utilization": 41, "resets_at": "2026-09-18T05:00:00.524116+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Equal(41.0, snapshot!.WeeklyUtilization);
        Assert.Equal("2026-09-18T05:00:00.524116+00:00", snapshot.WeeklyResetsAt);
    }

    [Fact]
    public void UsageParser_LimitsArrayPresent_PreferredOverExactKeysAndDecoys()
    {
        // docs/mac-port.md's exact wire example: limits[] is self-describing and matches the
        // exact keys bit-for-bit (including microseconds) whenever both are present.
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 76, "resets_at": "2026-09-11T11:20:00.650307+00:00" },
            "seven_day_cowork": { "utilization": 5, "resets_at": "2026-09-18T05:00:00.999999+00:00" },
            "seven_day": { "utilization": 41, "resets_at": "2026-09-18T05:00:00.650340+00:00" },
            "limits": [
              { "group": "session", "kind": "session", "percent": 76, "resets_at": "2026-09-11T11:20:00.650307+00:00", "is_active": true },
              { "group": "weekly", "kind": "weekly_all", "percent": 41, "resets_at": "2026-09-18T05:00:00.650340+00:00", "is_active": true },
              { "group": "weekly", "kind": "weekly_scoped", "percent": 0 }
            ]
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.Equal(76.0, snapshot!.SessionUtilization);
        Assert.Equal(41.0, snapshot.WeeklyUtilization);
        Assert.True(snapshot.SessionIsActive);
        Assert.True(snapshot.WeeklyIsActive);
    }

    [Fact]
    public void UsageParser_LimitsArrayAbsent_FallsBackToExactKeys_IgnoringDecoys()
    {
        string json = """
        {
          "response": {
            "seven_day_omelette": { "utilization": 2, "resets_at": "2026-09-18T05:00:00.111111+00:00" },
            "five_hour": { "utilization": 22, "resets_at": "2026-09-11T11:20:00.524090+00:00" },
            "seven_day": { "utilization": 33, "resets_at": "2026-09-18T05:00:00.524116+00:00" },
            "seven_day_breakdown": { "utilization": 4, "resets_at": "2026-09-18T05:00:00.222222+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Equal(22.0, snapshot!.SessionUtilization);
        Assert.Equal(33.0, snapshot.WeeklyUtilization);
    }

    [Fact]
    public void UsageParser_LimitsArrayDisagreesWithExactKeys_RefusesRatherThanGuessing()
    {
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 76, "resets_at": "2026-09-11T11:20:00.650307+00:00" },
            "seven_day": { "utilization": 41, "resets_at": "2026-09-18T05:00:00.650340+00:00" },
            "limits": [
              { "kind": "session", "percent": 12, "resets_at": "2026-09-11T11:20:00.650307+00:00" },
              { "kind": "weekly_all", "percent": 41, "resets_at": "2026-09-18T05:00:00.650340+00:00" }
            ]
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.Null(snapshot);
        Assert.NotNull(error);
        Assert.Contains("disagree", error);
    }

    [Fact]
    public void UsageParser_SubstringFallback_StillWorks_WhenNeitherLimitsNorExactKeysMatch()
    {
        // Neither "limits" nor an exact "five_hour" key exists -- only a key that CONTAINS
        // "five_hour" -- so strategies 1 and 2 both miss and this must fall through to the
        // substring search, unchanged in spirit from before the fix.
        string json = """
        {
          "response": {
            "five_hour_data": { "utilization": 15, "resets_at": "2026-09-11T11:20:00.524090+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Equal(15.0, snapshot!.SessionUtilization);
        Assert.Contains("substring fallback", error);
    }

    // ---- A1: is_active flows through the parser (docs/forecast-and-states.md, "A closed
    // window is an answer, not an absence") ----

    [Fact]
    public void UsageParser_ClosedWindow_IsActiveFalse_NoResetsAt_ParsesAsInactiveNotAsError()
    {
        // The exact real shape from the idle account: utilization 0, resets_at null,
        // is_active false. Read via the exact-keys strategy (no limits[] in this response).
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 0, "resets_at": null, "is_active": false },
            "seven_day": { "utilization": 0, "resets_at": null, "is_active": false }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Equal(0.0, snapshot!.SessionUtilization);
        Assert.Null(snapshot.SessionResetsAt);
        Assert.False(snapshot.SessionIsActive);
        Assert.False(snapshot.WeeklyIsActive);
    }

    [Fact]
    public void UsageParser_ClosedWindow_ViaLimitsArray_ReadsIsActiveDirectly()
    {
        string json = """
        {
          "response": {
            "limits": [
              { "kind": "session", "percent": 0, "resets_at": null, "is_active": false },
              { "kind": "weekly_all", "percent": 17, "resets_at": "2026-09-18T05:00:00.524116+00:00", "is_active": true }
            ]
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.SessionIsActive);
        Assert.Null(snapshot.SessionResetsAt);
        Assert.True(snapshot.WeeklyIsActive);
        Assert.Equal(17.0, snapshot.WeeklyUtilization);
    }

    // ---- An account without a 5 h limit: `five_hour: null` and no session entry in limits[] (observed from 2026-10-31, at any weekly percentage) ----

    [Fact]
    public void UsageParser_NoSessionLimit_NullFiveHour_ReadsAsClosedSessionWithWeekly_AndFlagsIt()
    {
        // The real shape, trimmed (here with the week at 100 %, but the shape does not depend on it).
        string json = """
        {
          "response": {
            "subscription_type": "max",
            "rate_limits": {
              "five_hour": null,
              "seven_day": { "utilization": 100, "resets_at": "2026-11-06T05:00:00.051971+00:00" },
              "seven_day_opus": null,
              "limits": [
                { "kind": "weekly_all", "group": "weekly", "percent": 100, "resets_at": "2026-11-06T05:00:00.051971+00:00", "is_active": true },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 60, "resets_at": "2026-11-06T05:00:00.052132+00:00", "is_active": false }
              ]
            }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.Equal(0.0, snapshot!.SessionUtilization);
        Assert.Null(snapshot.SessionResetsAt);
        Assert.False(snapshot.SessionIsActive);
        Assert.True(snapshot.NoSessionLimit);
        Assert.Equal(100.0, snapshot.WeeklyUtilization);
        Assert.Equal("2026-11-06T05:00:00.051971+00:00", snapshot.WeeklyResetsAt);
        Assert.True(snapshot.WeeklyIsActive);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(36.0)]
    public void UsageParser_NoSessionLimit_WithWeeklyBelowHundred_IsFlagged(double weeklyPct)
    {
        // Observed at weekly 0 %, 3-36 % and 100 %: the missing session is not a sign of a spent week.
        string json = $$"""
        {
          "response": {
            "subscription_type": "max",
            "rate_limits": {
              "five_hour": null,
              "seven_day": { "utilization": {{weeklyPct}}, "resets_at": "2026-11-10T13:00:00.469372+00:00" },
              "limits": [
                { "kind": "weekly_all", "group": "weekly", "percent": {{weeklyPct}}, "resets_at": "2026-11-10T13:00:00.469372+00:00", "is_active": true },
                { "kind": "weekly_scoped", "group": "weekly", "percent": 4, "resets_at": "2026-11-10T13:00:00.469576+00:00", "is_active": false }
              ]
            }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);

        Assert.NotNull(snapshot);
        Assert.Null(error);
        Assert.True(snapshot!.NoSessionLimit);
        Assert.False(snapshot.SessionIsActive);
        Assert.Null(snapshot.SessionResetsAt);
        Assert.Equal(weeklyPct, snapshot.WeeklyUtilization);
    }

    [Fact]
    public void UsageParser_SessionPresent_IsNotFlaggedAsNoSessionLimit()
    {
        string json = """
        {
          "response": {
            "five_hour": { "utilization": 0, "resets_at": null },
            "seven_day": { "utilization": 20, "resets_at": "2026-11-10T13:00:00.469372+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out _);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.NoSessionLimit); // an object with resets_at null is a closed 5 h window, not the absence of one
    }

    [Fact]
    public void UsageParser_NoSessionLimit_ViaExactKeysAlone()
    {
        string json = """
        {
          "response": {
            "five_hour": null,
            "seven_day": { "utilization": 100, "resets_at": "2026-11-10T13:00:00.469372+00:00" }
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out _);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.SessionIsActive);
        Assert.True(snapshot.NoSessionLimit);
        Assert.Equal(100.0, snapshot.WeeklyUtilization);
        Assert.True(snapshot.WeeklyIsActive);
    }

    [Fact]
    public void UsageParser_NullFiveHour_WithoutUsableWeekly_StillRefuses()
    {
        // A null session is only an answer next to a weekly reading; alone it says nothing.
        string json = """
        {
          "response": {
            "five_hour": null,
            "seven_day": null,
            "limits": [ { "kind": "weekly_scoped", "percent": 4, "resets_at": "2026-11-10T13:00:00.469576+00:00" } ]
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Null(UsageParser.TryParse(doc.RootElement, out _));
    }

    [Fact]
    public void UsageParser_MissingFiveHourKey_WithWeekly_StillRefuses()
    {
        // No `five_hour` key at all is an unknown shape, not a closed window.
        string json = """
        {
          "response": {
            "limits": [ { "kind": "weekly_all", "percent": 40, "resets_at": "2026-11-10T13:00:00.469372+00:00", "is_active": true } ]
          }
        }
        """;

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Null(UsageParser.TryParse(doc.RootElement, out _));
    }
}
