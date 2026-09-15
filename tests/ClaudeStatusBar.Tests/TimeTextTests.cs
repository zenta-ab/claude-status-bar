using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class TimeTextTests
{
    // A fixed +01:00 offset stands in for local (Swedish) time -- deterministic regardless
    // of the machine running the tests, and DST-agnostic since panel-v2 doesn't need it.
    static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("Test/+1", TimeSpan.FromHours(1), "Test/+1", "Test/+1");

    // ---- Duration band boundaries (docs/panel-v2.md, "Time text") ----

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(59, "59 s")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(119, "1 min 59 s")]
    [InlineData(120, "2 min")]
    [InlineData(59 * 60, "59 min")]
    [InlineData(60 * 60, "1 h")]
    [InlineData(23 * 3600 + 59 * 60, "23 h 59 min")]
    [InlineData(24 * 3600, "1 d 0 h")]
    [InlineData(6 * 86400 + 18 * 3600, "6 d 18 h")]
    public void Duration_HitsEveryBandBoundary(int seconds, string expected)
    {
        Assert.Equal(expected, TimeText.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_NegativeSpan_ClampsToZero()
    {
        Assert.Equal("0 s", TimeText.Duration(TimeSpan.FromSeconds(-30)));
    }

    [Fact]
    public void Duration_NeverEmitsARawMinuteCountAtOrAboveSixty()
    {
        // The whole point of panel-v2's formatter: "6975 min" must never happen.
        foreach (int minutes in new[] { 60, 90, 200, 1440, 10_080 })
        {
            string text = TimeText.Duration(TimeSpan.FromMinutes(minutes));
            Assert.DoesNotMatch(@"\b([6-9][0-9]|[1-9][0-9]{2,}) min\b", text);
        }
    }

    // ---- Points in time (today / tomorrow / weekday), including crossing midnight ----

    [Fact]
    public void PointInTime_SameLocalDay_IsClockOnly()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // 11:00 local
        var t = new DateTimeOffset(2026, 9, 11, 11, 36, 0, TimeSpan.Zero); // 12:36 local
        Assert.Equal("kl 12:36", TimeText.PointInTime(t, now, Tz));
    }

    [Fact]
    public void PointInTime_NextLocalDay_SaysImorgon()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // 11:00 local
        var t = new DateTimeOffset(2026, 9, 12, 9, 44, 0, TimeSpan.Zero); // tomorrow 10:44 local
        Assert.Equal("i morgon kl 10:44", TimeText.PointInTime(t, now, Tz));
    }

    [Fact]
    public void PointInTime_FurtherOut_UsesWeekdayAbbreviation()
    {
        // 2026-09-11 is a Friday; +3 local days lands on Monday.
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var t = new DateTimeOffset(2026, 9, 14, 9, 44, 0, TimeSpan.Zero);
        Assert.Equal("mån kl 10:44", TimeText.PointInTime(t, now, Tz));
    }

    [Fact]
    public void PointInTime_CrossingMidnight_NowLateEvening_TargetEarlyNextMorning_IsImorgon()
    {
        // now = 23:50 local; target = 00:10 local the next calendar day -- only 20
        // minutes away in wall-clock terms, but a genuine calendar-date crossing.
        var now = new DateTimeOffset(2026, 9, 11, 22, 50, 0, TimeSpan.Zero); // 23:50 local
        var t = new DateTimeOffset(2026, 9, 11, 23, 10, 0, TimeSpan.Zero); // 00:10 local, next day
        Assert.Equal("i morgon kl 00:10", TimeText.PointInTime(t, now, Tz));
    }

    [Fact]
    public void PointInTime_CrossingMidnight_NowEarlyMorning_TargetLastNight_IsNotTomorrow()
    {
        // now = 00:10 local; target = 23:50 the PREVIOUS calendar day is impossible for a
        // future reset/depletion, but this guards DayDiff itself is calendar-date, not
        // elapsed-time, based -- a target 20 minutes earlier on the previous date is day -1.
        var now = new DateTimeOffset(2026, 9, 11, 23, 10, 0, TimeSpan.Zero); // 00:10 local, next day
        var t = new DateTimeOffset(2026, 9, 11, 22, 50, 0, TimeSpan.Zero); // 23:50 local
        Assert.Equal(-1, TimeText.DayDiff(t, now, Tz));
    }

    [Fact]
    public void Reset_Session_TodayNoWeekday()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // 11:00 local
        var resetsAt = now.AddMinutes(61); // 12:01 local
        Assert.Equal("kl 12:01 (om 1 h 1 min)", TimeText.Reset(resetsAt, now, Tz, forceWeekday: false));
    }

    [Fact]
    public void Reset_Weekly_AlwaysCarriesWeekday_EvenIfTechnicallyToday()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // Friday 11:00 local
        var resetsAt = now.AddMinutes(37); // still Friday, same local day
        Assert.Equal("fre kl 11:37 (om 37 min)", TimeText.Reset(resetsAt, now, Tz, forceWeekday: true));
    }

    // ---- A reset/depletion at or before now must never render "(om 0 s)" or a negative duration ----

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Reset_AtOrBeforeNow_IsNu(int secondsFromNow)
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var resetsAt = now.AddSeconds(secondsFromNow);
        Assert.Equal("nu", TimeText.Reset(resetsAt, now, Tz, forceWeekday: false));
        Assert.Equal("nu", TimeText.Reset(resetsAt, now, Tz, forceWeekday: true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Depletion_AtOrBeforeNow_IsNu(int secondsFromNow)
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var dep = now.AddSeconds(secondsFromNow);
        Assert.Equal("nu", TimeText.Depletion(dep, now, Tz));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void CountdownFragment_AtOrBeforeNow_IsNu(int seconds)
    {
        Assert.Equal("nu", TimeText.CountdownFragment(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Depletion_Today_IncludesCountdown()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // 11:00 local
        var dep = now.AddMinutes(103); // 12:43 local
        Assert.Equal("kl 12:43 (om 1 h 43 min)", TimeText.Depletion(dep, now, Tz));
    }

    [Fact]
    public void Depletion_LaterDay_UsesWeekdayAndCountdown()
    {
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero); // Friday 11:00 local
        var dep = now.AddHours(46); // ~1 d 22 h out, a distinct weekday
        Assert.Matches(@"^(sön|mån|tis|ons|tor|fre|lör) kl \d{2}:\d{2} \(om .+\)$", TimeText.Depletion(dep, now, Tz));
    }
}
