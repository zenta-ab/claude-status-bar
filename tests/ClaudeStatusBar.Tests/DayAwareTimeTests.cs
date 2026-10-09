using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// A time that is not today carries its day -- everywhere a clock time is shown (panel text, status
/// boxes, tooltips, other-accounts rows, footer). "i morgon" tomorrow, the weekday up to six days
/// ahead, the date beyond that. Calendar days in LOCAL time: tested across midnight and across both
/// daylight-saving changes of Europe/Stockholm, where elapsed hours and calendar days disagree.
/// </summary>
public class DayAwareTimeTests
{
    static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    static DateTimeOffset Local(int y, int mo, int d, int h, int mi) =>
        new(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), Stockholm.GetUtcOffset(new DateTime(y, mo, d, h, mi, 0)));

    // Wednesday 2026-10-07 at 14:00 local.
    static readonly DateTimeOffset Now = Local(2026, 10, 7, 14, 0);

    // ---- the helpers ----

    [Fact]
    public void ClockWithDay_TodayTomorrowWeekdayDate()
    {
        Assert.Equal("15:00", TimeText.ClockWithDay(Local(2026, 10, 7, 15, 0), Now, Stockholm));
        Assert.Equal("i morgon 07:00", TimeText.ClockWithDay(Local(2026, 10, 8, 7, 0), Now, Stockholm));
        Assert.Equal("fre 07:00", TimeText.ClockWithDay(Local(2026, 10, 9, 7, 0), Now, Stockholm));   // 2 days
        Assert.Equal("tis 15:00", TimeText.ClockWithDay(Local(2026, 10, 13, 15, 0), Now, Stockholm)); // 6 days
        Assert.Equal("14 okt 15:00", TimeText.ClockWithDay(Local(2026, 10, 14, 15, 0), Now, Stockholm)); // 7 days: a weekday would be today's own
        Assert.Equal("2 nov 09:30", TimeText.ClockWithDay(Local(2026, 11, 2, 9, 30), Now, Stockholm));
    }

    [Fact]
    public void PointInTime_Beyond6Days_IsADate_NotAnAmbiguousWeekday()
    {
        Assert.Equal("14 okt kl 15:00", TimeText.PointInTime(Local(2026, 10, 14, 15, 0), Now, Stockholm));
        Assert.Equal("tis kl 15:00", TimeText.PointInTime(Local(2026, 10, 13, 15, 0), Now, Stockholm));
    }

    [Fact]
    public void PointInTime_Yesterday_SaysIgår()
    {
        Assert.Equal("igår kl 22:10", TimeText.PointInTime(Local(2026, 10, 6, 22, 10), Now, Stockholm));
    }

    [Fact]
    public void WeekdayOrDate_SevenDaysAhead_IsTheDate()
    {
        Assert.Equal("ons", TimeText.WeekdayOrDate(Local(2026, 10, 7, 20, 0), Now, Stockholm));   // today: names its own weekday
        Assert.Equal("14 okt", TimeText.WeekdayOrDate(Local(2026, 10, 14, 20, 0), Now, Stockholm));
    }

    [Fact]
    public void Reset_ForcedWeekday_SevenDaysAhead_CarriesTheDate()
    {
        string text = TimeText.Reset(Local(2026, 10, 14, 15, 0), Now, Stockholm, forceWeekday: true);

        Assert.StartsWith("14 okt kl 15:00 (om 7 d ", text);
    }

    [Fact]
    public void Depletion_SevenDaysAhead_CarriesTheDate_AndTodayStaysBare()
    {
        Assert.StartsWith("14 okt kl 15:00", TimeText.Depletion(Local(2026, 10, 14, 15, 0), Now, Stockholm));
        Assert.StartsWith("kl 15:00 (om 1 h)", TimeText.Depletion(Local(2026, 10, 7, 15, 0), Now, Stockholm));
    }

    [Fact]
    public void FooterClock_LastReadYesterdayOrEarlier_NamesItsDay()
    {
        Assert.Equal("kl 13:59:30", TimeText.ClockWithSecondsAndDay(Local(2026, 10, 7, 13, 59).AddSeconds(30), Now, Stockholm));
        Assert.Equal("igår kl 23:59:30", TimeText.ClockWithSecondsAndDay(Local(2026, 10, 6, 23, 59).AddSeconds(30), Now, Stockholm));
        Assert.Equal("3 okt kl 08:00:00", TimeText.ClockWithSecondsAndDay(Local(2026, 10, 3, 8, 0), Now, Stockholm));
    }

    // ---- across midnight ----

    [Fact]
    public void AcrossMidnight_ThreeHoursAheadAtEleven_IsTomorrow_NotToday()
    {
        DateTimeOffset lateEvening = Local(2026, 10, 7, 23, 0);

        Assert.Equal("i morgon 02:00", TimeText.ClockWithDay(Local(2026, 10, 8, 2, 0), lateEvening, Stockholm));
        Assert.Equal("i morgon kl 02:00", TimeText.PointInTime(Local(2026, 10, 8, 2, 0), lateEvening, Stockholm));
    }

    [Fact]
    public void AcrossMidnight_JustAfterMidnight_YesterdayEveningIsIgår()
    {
        DateTimeOffset earlyMorning = Local(2026, 10, 8, 0, 10);

        Assert.Equal("igår kl 23:50", TimeText.PointInTime(Local(2026, 10, 7, 23, 50), earlyMorning, Stockholm));
        Assert.Equal("00:30", TimeText.ClockWithDay(Local(2026, 10, 8, 0, 30), earlyMorning, Stockholm));
    }

    // ---- DST: calendar days, not 24-hour blocks ----

    [Fact]
    public void Dst_SpringForward_ThreeAmAfterTheChange_IsStillTomorrow()
    {
        // 2026-03-29 02:00 -> 03:00 in Stockholm. From the evening before, 08:00 next day is "i morgon".
        DateTimeOffset evening = Local(2026, 3, 28, 22, 0);
        DateTimeOffset target = Local(2026, 3, 29, 8, 0);

        Assert.Equal("i morgon 08:00", TimeText.ClockWithDay(target, evening, Stockholm));
        Assert.True((target - evening).TotalHours < 24, "only 9 hours of wall time but still a different day");
    }

    [Fact]
    public void Dst_SpringForward_ExactlyTwentyFourHoursLater_CanAlreadyBeTheDayAfterTomorrow()
    {
        // 23:30 on the 28th + 24 h of elapsed time lands at 00:30 on the 30th local (the day lost an hour).
        DateTimeOffset start = Local(2026, 3, 28, 23, 30);
        DateTimeOffset later = start.AddHours(24);

        Assert.Equal(2, TimeText.DayDiff(later, start, Stockholm));
        Assert.Equal("mån 00:30", TimeText.ClockWithDay(later, start, Stockholm)); // 30 Mar 2026 is a Monday
    }

    [Fact]
    public void Dst_FallBack_TheRepeatedHourDoesNotShiftTheDay()
    {
        // 2026-10-25 03:00 -> 02:00 in Stockholm: the day has 25 hours.
        DateTimeOffset morning = Local(2026, 10, 25, 0, 30);
        DateTimeOffset lateNight = Local(2026, 10, 25, 23, 30);   // same calendar day, 24 h later in elapsed terms is already tomorrow
        DateTimeOffset nextDay = Local(2026, 10, 26, 0, 10);

        Assert.Equal("23:30", TimeText.ClockWithDay(lateNight, morning, Stockholm));
        Assert.Equal("i morgon 00:10", TimeText.ClockWithDay(nextDay, morning, Stockholm));
        Assert.True((lateNight - morning).TotalHours > 23, "an elapsed-time rule would have disagreed here");
    }

    [Fact]
    public void Dst_WeekAcrossTheChange_StillCountsCalendarDays()
    {
        DateTimeOffset before = Local(2026, 10, 22, 12, 0);
        DateTimeOffset sixDays = Local(2026, 10, 28, 12, 0); // crosses the 25 Oct fall-back

        Assert.Equal(6, TimeText.DayDiff(sixDays, before, Stockholm));
        Assert.Equal("ons 12:00", TimeText.ClockWithDay(sixDays, before, Stockholm));
        Assert.Equal("29 okt 12:00", TimeText.ClockWithDay(Local(2026, 10, 29, 12, 0), before, Stockholm));
    }

    // ---- the other-accounts rows ----

    static WindowView Spent(WindowKind kind, double minutes, DateTimeOffset resetsAt) =>
        new(kind, minutes, 100.0, resetsAt, QuotaState.Spent, null, null, null, null, TimeSpan.Zero, null);

    static WindowView Fine(WindowKind kind, double minutes, DateTimeOffset resetsAt) =>
        new(kind, minutes, 10.0, resetsAt, QuotaState.Safe, 0.01, 1, 20, null, TimeSpan.Zero, null);

    static QuotaView View(WindowView session, WindowView weekly, DateTimeOffset blockedUntil) =>
        new(session, weekly, Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null,
            (QuotaState)Math.Max((int)session.State, (int)weekly.State), blockedUntil);

    [Fact]
    public void Row_WeeklySpent_SaysVeckanSlut_WithTheWeekday()
    {
        DateTimeOffset weeklyReset = Local(2026, 10, 13, 15, 0);   // Tuesday, 6 days
        QuotaView view = View(Fine(WindowKind.Session, 300, Now.AddHours(2)), Spent(WindowKind.Weekly, 10080, weeklyReset), weeklyReset);

        OtherAccountRow row = PanelText.ComposeOtherAccountRow(0, "Max", view, Now, Stockholm);

        Assert.Equal("veckan slut · öppnar tis 15:00", row.Line);
        Assert.Equal(PanelColorRole.Dead, row.Role);
    }

    [Fact]
    public void Row_WeeklySpent_ReopeningTomorrow_SaysIMorgon()
    {
        DateTimeOffset weeklyReset = Local(2026, 10, 8, 7, 0);
        QuotaView view = View(Fine(WindowKind.Session, 300, Now.AddHours(2)), Spent(WindowKind.Weekly, 10080, weeklyReset), weeklyReset);

        Assert.Equal("veckan slut · öppnar i morgon 07:00", PanelText.ComposeOtherAccountRow(0, "Max", view, Now, Stockholm).Line);
    }

    [Fact]
    public void Row_WeeklySpent_ReopeningInSevenDays_SaysTheDate()
    {
        DateTimeOffset weeklyReset = Local(2026, 10, 14, 5, 0);
        QuotaView view = View(Fine(WindowKind.Session, 300, Now.AddHours(2)), Spent(WindowKind.Weekly, 10080, weeklyReset), weeklyReset);

        Assert.Equal("veckan slut · öppnar 14 okt 05:00", PanelText.ComposeOtherAccountRow(0, "Max", view, Now, Stockholm).Line);
    }

    [Fact]
    public void Row_SessionSpent_ReopeningToday_NamesTheSession_NoDay()
    {
        DateTimeOffset sessionReset = Local(2026, 10, 7, 17, 19);
        QuotaView view = View(Spent(WindowKind.Session, 300, sessionReset), Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), sessionReset);

        Assert.Equal("sessionen slut · öppnar 17:19", PanelText.ComposeOtherAccountRow(0, "Max", view, Now, Stockholm).Line);
    }

    [Fact]
    public void Row_SessionSpent_ReopeningAfterMidnight_SaysIMorgon()
    {
        DateTimeOffset late = Local(2026, 10, 7, 22, 0);
        DateTimeOffset sessionReset = Local(2026, 10, 8, 1, 30);
        QuotaView view = View(Spent(WindowKind.Session, 300, sessionReset), Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), sessionReset);

        Assert.Equal("sessionen slut · öppnar i morgon 01:30", PanelText.ComposeOtherAccountRow(0, "Max", view, late, Stockholm).Line);
    }

    [Fact]
    public void Row_BothSpent_ReportsTheWeek_TheSessionIsIrrelevantUntilThen()
    {
        DateTimeOffset sessionReset = Local(2026, 10, 7, 17, 19);
        DateTimeOffset weeklyReset = Local(2026, 10, 13, 15, 0);
        QuotaView view = View(Spent(WindowKind.Session, 300, sessionReset), Spent(WindowKind.Weekly, 10080, weeklyReset), weeklyReset);

        string line = PanelText.ComposeOtherAccountRow(0, "Max", view, Now, Stockholm).Line;

        Assert.Equal("veckan slut · öppnar tis 15:00", line);
        Assert.DoesNotContain("17:19", line);
    }

    // ---- the tooltip ----

    [Fact]
    public void Tooltip_WeeklySpent_NamesTheWeekAndTheDay()
    {
        DateTimeOffset weeklyReset = Local(2026, 10, 13, 15, 0);
        QuotaView view = View(Fine(WindowKind.Session, 300, Now.AddHours(2)), Spent(WindowKind.Weekly, 10080, weeklyReset), weeklyReset);

        string tooltip = IconSlot.BuildTooltip(view, null, Now, Stockholm);

        Assert.Equal("Veckokvoten slut · öppnar tis 15:00", tooltip);
    }

    [Fact]
    public void Tooltip_SessionSpentToday_HasNoDay_AndTomorrowHasIMorgon()
    {
        DateTimeOffset today = Local(2026, 10, 7, 17, 19);
        QuotaView viewToday = View(Spent(WindowKind.Session, 300, today), Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), today);
        Assert.Equal("Kvoten slut · öppnar 17:19", IconSlot.BuildTooltip(viewToday, null, Now, Stockholm));

        DateTimeOffset tomorrow = Local(2026, 10, 8, 1, 30);
        QuotaView viewTomorrow = View(Spent(WindowKind.Session, 300, tomorrow), Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), tomorrow);
        Assert.Equal("Kvoten slut · öppnar i morgon 01:30", IconSlot.BuildTooltip(viewTomorrow, null, Now, Stockholm));
    }

    [Fact]
    public void Tooltip_SafeWithAWeeklyDriver_ShowsTheWeekdayOfTheReset()
    {
        WindowView weekly = new(WindowKind.Weekly, 10080, 40.0, Local(2026, 10, 13, 15, 0), QuotaState.Safe, 0.01, 1, 55.0, null, TimeSpan.Zero, null);
        QuotaView view = new(Fine(WindowKind.Session, 300, Now.AddHours(2)) with { State = QuotaState.Measuring, MeasuringReason = "x", ResetsAt = null }, weekly,
            Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

        Assert.Equal("Räcker · ~55% vid reset tis 15:00", IconSlot.BuildTooltip(view, null, Now, Stockholm));
    }

    [Fact]
    public void Tooltip_DryEarlyTomorrow_CarriesTheDay_AndStaysWithinTheCap()
    {
        WindowView session = new(WindowKind.Session, 300, 90.0, Now.AddHours(3), QuotaState.DryEarly, 1, 2, 150, Local(2026, 10, 8, 1, 5), TimeSpan.FromMinutes(95), null);
        QuotaView view = new(session, Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.DryEarly, null);

        string tooltip = IconSlot.BuildTooltip(view, "Max", Now, Stockholm);

        Assert.Contains("i morgon 01:05", tooltip);
        Assert.True(tooltip.Length <= 63, tooltip);
    }

    // ---- the status box and section headers ----

    [Fact]
    public void SessionHeader_ResetAfterMidnight_SaysIMorgon()
    {
        DateTimeOffset late = Local(2026, 10, 7, 22, 0);
        WindowView session = Fine(WindowKind.Session, 300, Local(2026, 10, 8, 1, 30));
        QuotaView view = new(session, Fine(WindowKind.Weekly, 10080, Local(2026, 10, 12, 5, 0)), Freshness.Live, late, late, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

        PanelTextResult text = PanelText.Compose(view, late, Stockholm);

        Assert.EndsWith("i morgon kl 01:30", text.Session.ResetHeader);
    }

    [Fact]
    public void WeeklyHeader_ResetSevenDaysAway_SaysTheDate()
    {
        WindowView weekly = Fine(WindowKind.Weekly, 10080, Local(2026, 10, 14, 5, 0));
        QuotaView view = new(Fine(WindowKind.Session, 300, Now.AddHours(2)), weekly, Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

        Assert.EndsWith("14 okt 05:00", PanelText.Compose(view, Now, Stockholm).Weekly.ResetHeader);
    }

    [Fact]
    public void StatusBox_LastReadYesterday_NamesTheDay()
    {
        QuotaView unknown = QuotaView.Initial with { LastSuccessAt = Local(2026, 10, 6, 22, 10) };

        string line2 = PanelText.Compose(unknown, Now, Stockholm).StatusBox.Line2;

        Assert.StartsWith("Senast avläst igår kl 22:10", line2);
    }
}
