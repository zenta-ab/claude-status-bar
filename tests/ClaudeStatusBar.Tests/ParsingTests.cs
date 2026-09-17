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
}
