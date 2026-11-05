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

    // ---- an ORDINARY account (it has a 5 h window): a closed session must not change its cadence ----

    /// <summary>Eight 100 % samples (novel resets_at each time) commit a Spent session hold; returns the model and the instant of the last sample.</summary>
    static (QuotaModel Model, DateTimeOffset Reset, DateTimeOffset Last) SpentSession()
    {
        var model = new QuotaModel();
        DateTimeOffset reset = T0.AddHours(1);
        DateTimeOffset last = T0;
        for (int i = 0; i < 8; i++)
        {
            last = T0.AddMinutes(5 * i);
            model.Ingest(OpenSession(last, reset.AddTicks(i * 17), pct: 100.0), last, 300_000L * i);
        }
        Assert.Equal(TimeSpan.FromSeconds(300), model.NextPollDelay(last)); // the premise: a genuinely Spent session
        return (model, reset, last);
    }

    [Fact]
    public void SpentSession_ThenItClosesAfterItsReset_RecoversAtThe30SecondCadence_NotPinnedAtSpent()
    {
        // Decision 8 (docs/forecast-and-states.md): an expired Spent window must recover at 30 s. The session hold only
        // steps on ACCEPTED samples, so after the session closes it still reads Spent -- awaiting-reset must win.
        (QuotaModel model, DateTimeOffset reset, _) = SpentSession();

        DateTimeOffset now = reset.AddSeconds(5);
        long mono = (long)(now - T0).TotalMilliseconds;
        model.Ingest(ClosedSession(now, weeklyPct: 30.0), now, mono);

        Assert.Equal(TimeSpan.FromSeconds(30), model.NextPollDelay(now));
        Assert.Equal(TimeSpan.FromSeconds(30), model.Evaluate(now, mono).PollInterval);
        Assert.False(model.Evaluate(now, mono).NoSessionLimit);
    }

    [Fact]
    public void SpentSession_ThenItCloses_FreshnessDeadlinesAreThose_OfThe30SecondCadence()
    {
        // The deadlines are frozen at the poll from the policy interval: 3 x 30 s = 90 s to Stale, 10 x 30 s = 300 s to Unknown.
        (QuotaModel model, DateTimeOffset reset, _) = SpentSession();
        DateTimeOffset now = reset.AddSeconds(5);
        long mono = (long)(now - T0).TotalMilliseconds;
        model.Ingest(ClosedSession(now, weeklyPct: 30.0), now, mono);

        Assert.Equal(Freshness.Live, model.Evaluate(now.AddSeconds(85), mono + 85_000).Freshness);
        Assert.Equal(Freshness.Stale, model.Evaluate(now.AddSeconds(95), mono + 95_000).Freshness);
        Assert.Equal(Freshness.Stale, model.Evaluate(now.AddSeconds(290), mono + 290_000).Freshness);
        Assert.Equal(Freshness.Unknown, model.Evaluate(now.AddSeconds(310), mono + 310_000).Freshness);
    }

    [Fact]
    public void OrdinaryIdleAccount_AfterItsSessionDeadlinePasses_PollsAsBefore_30sUntilANewSessionAppears()
    {
        // A normal, unspent session ends and the account goes idle: the expired deadline keeps counting as "awaiting
        // reset" (30 s) -- exactly the cadence this account had before no-limit support existed.
        var model = new QuotaModel();
        DateTimeOffset reset = T0.AddHours(1);
        for (int i = 0; i < 4; i++)
        {
            DateTimeOffset t = T0.AddMinutes(5 * i);
            model.Ingest(OpenSession(t, reset.AddTicks(i * 17), pct: 20.0 + i), t, 300_000L * i);
        }

        foreach (TimeSpan after in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(3) })
        {
            DateTimeOffset now = reset + after;
            long mono = (long)(now - T0).TotalMilliseconds;
            model.Ingest(ClosedSession(now, weeklyPct: 30.0), now, mono);

            Assert.Equal(TimeSpan.FromSeconds(30), model.NextPollDelay(now));
        }

        // ... and a new session ends it: back to the idle cadence once the account is not burning.
        DateTimeOffset reopened = reset.AddHours(3).AddMinutes(40);
        long reopenedMono = (long)(reopened - T0).TotalMilliseconds;
        model.Ingest(OpenSession(reopened, reopened.AddHours(5), pct: 1.0), reopened, reopenedMono);
        DateTimeOffset later = reopened.AddMinutes(20);
        long laterMono = reopenedMono + 20 * 60_000L;
        model.Ingest(OpenSession(later, reopened.AddHours(5).AddTicks(17), pct: 1.0), later, laterMono);

        Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(later));
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
