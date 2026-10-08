using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// docs/forecast-and-states.md "Freshness ladder": an idle account is not a stale one. Usage only
/// happens inside an open session window, so while the session window is validly CLOSED nothing can
/// be consumed and an unchanged snapshot is expected -- the 20-minute "data age" rule must not run
/// and the (now expired) session deadline must not read as "a reset we are waiting for". An OPEN
/// window that stops changing, and every kind of failure, still age exactly as before.
///
/// Evidence: on a real installation one account answered every poll successfully for hours, yet
/// 933 of 970 state lines said Stale. Its session window was closed (the server answers
/// {utilization 0, resets_at null, is_active false}) and its weekly window Spent, so the snapshot
/// never changed; the Stale began exactly five hours after its last open session started, i.e. when
/// that window's own resets_at + 60 s passed.
/// </summary>
public class IdleAccountFreshnessTests
{
    static string Raw(DateTimeOffset instant, int jitter = 0) =>
        instant.AddTicks(jitter * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset WeeklyReset = T0.AddDays(2);

    /// <summary>The exact shape of a closed session: utilization 0, no deadline, not active.</summary>
    static UsageSnapshot ClosedSession(DateTimeOffset now, double weeklyPct = 100.0) =>
        new(0.0, null, weeklyPct, Raw(WeeklyReset), now, "max", SessionIsActive: false, WeeklyIsActive: true);

    static UsageSnapshot OpenSession(DateTimeOffset now, DateTimeOffset sessionReset, double pct = 40.0) =>
        new(pct, Raw(sessionReset), 30.0, Raw(WeeklyReset), now, "max");

    // ---- closed session + spent weekly: the reported case ----

    [Fact]
    public void ClosedSession_SpentWeekly_IdenticalRepliesForTwoHours_StaysLive()
    {
        var model = new QuotaModel();
        for (int i = 0; i <= 24; i++) // every 5 min (the Spent poll interval) for 2 h
        {
            DateTimeOffset now = T0.AddMinutes(5 * i);
            long mono = 5L * i * 60_000;
            model.Ingest(ClosedSession(now), now, mono);

            QuotaView view = model.Evaluate(now, mono);
            Assert.Equal(Freshness.Live, view.Freshness);
        }
    }

    [Fact]
    public void ClosedSession_SpentWeekly_StaysLiveEvenBetweenPolls_UpToTheTransportDeadline()
    {
        var model = new QuotaModel();
        model.Ingest(ClosedSession(T0), T0, 0);

        // Spent policy interval 300 s: stale_at = 3 * 300 s. Just inside it, still Live.
        DateTimeOffset justInside = T0.AddMinutes(14);
        Assert.Equal(Freshness.Live, model.Evaluate(justInside, 14 * 60_000L).Freshness);
    }

    [Fact]
    public void ClosedSession_NoSpentWeekly_IdleAccountEverywhere_StaysLive_AllDay()
    {
        var model = new QuotaModel();
        for (int i = 0; i <= 96; i++) // every 150 s (the idle interval) for 4 h
        {
            DateTimeOffset now = T0.AddSeconds(150 * i);
            long mono = 150_000L * i;
            model.Ingest(ClosedSession(now, weeklyPct: 20.0), now, mono);

            Assert.Equal(Freshness.Live, model.Evaluate(now, mono).Freshness);
        }
    }

    // ---- the cause in the evidence: a session window that has ended ----

    [Fact]
    public void SessionThatEnded_AndNowReportsClosed_IsNotStaleBecauseItsOldDeadlinePassed()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = T0.AddHours(5);
        DateTimeOffset open = sessionReset.AddMinutes(-200);
        model.Ingest(OpenSession(open, sessionReset), open, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(open, 0).Freshness);

        // The window ends; from here on the server says "closed". Polls every 150 s for 3 h.
        DateTimeOffset start = sessionReset.AddMinutes(2);
        for (int i = 0; i <= 72; i++)
        {
            DateTimeOffset now = start.AddSeconds(150 * i);
            long mono = (long)(now - open).TotalMilliseconds;
            model.Ingest(ClosedSession(now, weeklyPct: 30.0), now, mono);

            Assert.True(model.Evaluate(now, mono).Freshness == Freshness.Live, $"went {model.Evaluate(now, mono).Freshness} {i} polls after the session closed");
        }
    }

    [Fact]
    public void SessionThatEnded_WithTheServerStillReplayingTheOldWindow_IsStillStale()
    {
        // "Awaiting reset": the reply STILL describes the window that already ended (a cached reply
        // after sleep). That is not a validly closed window and must keep reading Stale.
        var model = new QuotaModel();
        DateTimeOffset sessionReset = T0.AddHours(5);
        DateTimeOffset open = sessionReset.AddMinutes(-200);
        model.Ingest(OpenSession(open, sessionReset), open, 0);

        DateTimeOffset after = sessionReset.AddMinutes(10);
        long mono = (long)(after - open).TotalMilliseconds;
        model.Ingest(OpenSession(after, sessionReset), after, mono);

        Assert.Equal(Freshness.Stale, model.Evaluate(after, mono).Freshness);
    }

    // ---- what must still age ----

    [Fact]
    public void OpenSession_IdenticalRepliesForMoreThanTwentyMinutes_StillGoesStale()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = T0.AddHours(5);
        DateTimeOffset start = T0.AddHours(1);
        // One novel sample, then the very same snapshot every 30 s.
        model.Ingest(OpenSession(start, sessionReset), start, 0);
        Freshness atTwentyOne = Freshness.Live;
        for (int i = 1; i <= 50; i++)
        {
            DateTimeOffset now = start.AddSeconds(30 * i);
            long mono = 30_000L * i;
            model.Ingest(OpenSession(now, sessionReset), now, mono);
            if (i == 42) atTwentyOne = model.Evaluate(now, mono).Freshness; // 21 min
            if (i == 30) Assert.Equal(Freshness.Live, model.Evaluate(now, mono).Freshness); // 15 min: not yet
        }

        Assert.Equal(Freshness.Stale, atTwentyOne);
    }

    [Fact]
    public void ClosedSession_ThenFailures_StillGoStaleAndThenUnknown()
    {
        var model = new QuotaModel();
        model.Ingest(ClosedSession(T0), T0, 0);

        // Failures only, every minute: transport deadlines are frozen at the last good poll.
        Freshness at16 = Freshness.Live, at60 = Freshness.Live;
        for (int minute = 1; minute <= 60; minute++)
        {
            DateTimeOffset now = T0.AddMinutes(minute);
            model.IngestFailure("boom", now, minute * 60_000L);
            if (minute == 16) at16 = model.Evaluate(now, minute * 60_000L).Freshness;
            if (minute == 60) at60 = model.Evaluate(now, minute * 60_000L).Freshness;
        }

        Assert.Equal(Freshness.Stale, at16);   // past 3 * 300 s
        Assert.Equal(Freshness.Unknown, at60); // past 10 * 300 s
    }

    [Fact]
    public void ClosedSession_ARecoveryAfterFailures_IsLiveAgain()
    {
        var model = new QuotaModel();
        model.Ingest(ClosedSession(T0), T0, 0);
        for (int minute = 1; minute <= 30; minute++) model.IngestFailure("boom", T0.AddMinutes(minute), minute * 60_000L);

        DateTimeOffset now = T0.AddMinutes(31);
        model.Ingest(ClosedSession(now), now, 31 * 60_000L);

        Assert.Equal(Freshness.Live, model.Evaluate(now, 31 * 60_000L).Freshness);
    }

    // ---- the oracle itself ----

    [Fact]
    public void Oracle_SessionClosed_SkipsTheDataAgeRule_ButNotTheTransportDeadlines()
    {
        long twentyFiveMin = 25L * 60_000;
        var closed = new FreshnessInputs(true, StaleAtMono: twentyFiveMin * 2, UnknownAtMono: twentyFiveMin * 4, LastAcceptedMono: 0,
            SessionResetsAt: null, WeeklyResetsAt: null, ClockDiscontinuity: false, SessionClosed: true);

        Assert.Equal(Freshness.Live, FreshnessOracle.Evaluate(closed, T0, twentyFiveMin));
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(closed with { StaleAtMono = 1000 }, T0, twentyFiveMin));
        Assert.Equal(Freshness.Unknown, FreshnessOracle.Evaluate(closed with { UnknownAtMono = 1000 }, T0, twentyFiveMin));
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(closed with { ClockDiscontinuity = true }, T0, twentyFiveMin));
        Assert.Equal(Freshness.Unknown, FreshnessOracle.Evaluate(closed with { HasEverSucceeded = false }, T0, twentyFiveMin));

        // An OPEN window with the same numbers is stale by the data-age rule.
        Assert.Equal(Freshness.Stale, FreshnessOracle.Evaluate(closed with { SessionClosed = false }, T0, twentyFiveMin));
    }
}
