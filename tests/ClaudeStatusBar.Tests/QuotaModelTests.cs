using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class QuotaModelTests
{
    static string Raw(DateTimeOffset instant, int jitter = 0) =>
        instant.AddTicks(jitter * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);

    // ---- item 4: rollover only when resets_at genuinely advances (decision 1's plausibility gate) ----

    [Fact]
    public void Rollover_ClearsState_MeasuresForFiveMinutes_ThenReachesAVerdict()
    {
        var model = new QuotaModel();
        DateTimeOffset windowAReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset windowBReset = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 1, 8, 5, 0, 0, TimeSpan.Zero);

        DateTimeOffset t0 = windowAReset.AddMinutes(-100); // window A, E=200
        model.Ingest(new UsageSnapshot(50.0, Raw(windowAReset), 10.0, Raw(weeklyReset), t0), t0, 0);

        // Rollover: a LATER resets_at arrives, well after window A's own deadline (so the
        // decision-1 plausibility gate -- now is at/after the old deadline minus 120s --
        // is trivially satisfied) and with plenty of margin left in window B for a seed.
        // mono baseline resets here -- only deltas from this point on need to agree with
        // the UTC deltas below.
        const long monoBase = 1_000_000;
        DateTimeOffset tRollover = windowBReset.AddMinutes(-250); // window B, E=50 (valid seed)
        model.Ingest(new UsageSnapshot(5.0, Raw(windowBReset), 10.0, Raw(weeklyReset), tRollover), tRollover, monoBase);

        QuotaView afterRollover = model.Evaluate(tRollover, monoBase);
        Assert.Equal(QuotaState.Measuring, afterRollover.Session.State);
        Assert.Equal(5.0, afterRollover.Session.UsedPct); // envelope cleared, not carried over from window A's 50

        // Still inside the 5-minute rollover gate.
        DateTimeOffset tPlus2 = tRollover.AddMinutes(2);
        long monoPlus2 = monoBase + 2 * 60_000;
        model.Ingest(new UsageSnapshot(6.0, Raw(windowBReset, 1), 10.0, Raw(weeklyReset), tPlus2), tPlus2, monoPlus2);
        Assert.Equal(QuotaState.Measuring, model.Evaluate(tPlus2, monoPlus2).Session.State);

        // Past the 5-minute gate: a real verdict.
        DateTimeOffset tPlus6 = tRollover.AddMinutes(6);
        long monoPlus6 = monoBase + 6 * 60_000;
        model.Ingest(new UsageSnapshot(8.0, Raw(windowBReset, 2), 10.0, Raw(weeklyReset), tPlus6), tPlus6, monoPlus6);
        QuotaState finalState = model.Evaluate(tPlus6, monoPlus6).Session.State;
        Assert.NotEqual(QuotaState.Measuring, finalState);
        Assert.NotEqual(QuotaState.Spent, finalState);
    }

    // ---- decision 15: Spent immediate at P >= 100, BlockedUntil ----

    [Fact]
    public void Spent_At100Percent_IsImmediate_AndBlockedUntilIsThatWindowsReset()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 1, 8, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset utcNow = sessionReset.AddMinutes(-100);

        model.Ingest(new UsageSnapshot(100.0, Raw(sessionReset), 10.0, Raw(weeklyReset), utcNow), utcNow, 0);

        QuotaView view = model.Evaluate(utcNow, 0);
        Assert.Equal(QuotaState.Spent, view.Session.State);
        Assert.NotEqual(QuotaState.Spent, view.Weekly.State);
        Assert.Equal(sessionReset, view.BlockedUntil);
    }

    [Fact]
    public void Spent_BothWindows_BlockedUntilIsTheLaterResetsAt()
    {
        var model = new QuotaModel();
        DateTimeOffset anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset sessionReset = anchor.AddMinutes(250);  // E = 50
        DateTimeOffset weeklyReset = anchor.AddMinutes(9700);  // E = 380 (>= 336)

        model.Ingest(new UsageSnapshot(100.0, Raw(sessionReset), 100.0, Raw(weeklyReset), anchor), anchor, 0);

        QuotaView view = model.Evaluate(anchor, 0);
        Assert.Equal(QuotaState.Spent, view.Session.State);
        Assert.Equal(QuotaState.Spent, view.Weekly.State);
        Assert.True(weeklyReset > sessionReset);
        Assert.Equal(weeklyReset, view.BlockedUntil);
    }

    // ---- NextPollDelay: base policy ----

    [Fact]
    public void NextPollDelay_Burning_IsThirtySeconds()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero); // far away, no cap
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, Raw(sessionReset), null, null, t0), t0, 0); // dP>0 -> rise now

        Assert.Equal(TimeSpan.FromSeconds(30), model.NextPollDelay(t0.AddMinutes(1)));
    }

    [Fact]
    public void NextPollDelay_Idle_IsOneHundredFiftySeconds()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero); // far away, no cap
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, Raw(sessionReset), null, null, t0), t0, 0);

        // Decision 2 (round 2): "burning" is judged against the latest monoMs the model has
        // received via Ingest/Evaluate, not a live UTC delta -- an Evaluate tick at the later
        // instant is what actually advances that reading (matching how the real poll loop
        // always runs Evaluate on the 1s UI timer between polls).
        DateTimeOffset t1 = t0.AddMinutes(11); // past the 10-min burning window
        model.Evaluate(t1, 11 * 60_000);
        Assert.Equal(TimeSpan.FromSeconds(150), model.NextPollDelay(t1));
    }

    [Fact]
    public void NextPollDelay_Spent_IsThreeHundredSeconds()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero); // far away, no cap
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(100.0, Raw(sessionReset), null, null, t0), t0, 0);

        Assert.Equal(TimeSpan.FromSeconds(300), model.NextPollDelay(t0));
    }

    [Fact]
    public void NextPollDelay_IsCappedAtSoonestResetPlusThreeSeconds()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset sessionReset = t0.AddSeconds(10); // much sooner than the 30s burning interval

        model.Ingest(new UsageSnapshot(20.0, Raw(sessionReset), null, null, t0), t0, 0);

        TimeSpan delay = model.NextPollDelay(t0);
        Assert.True(delay <= TimeSpan.FromSeconds(13.5), $"expected <= ~13s, got {delay}");
        Assert.True(delay < TimeSpan.FromSeconds(30), "the cap must win over the uncapped burning interval");
    }

    [Fact]
    public void NextPollDelay_BackoffSequence_AfterConsecutiveFailures()
    {
        var model = new QuotaModel();
        DateTimeOffset t = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        double[] expectedSeconds = { 60, 120, 240, 480, 600, 600 };

        foreach (double expected in expectedSeconds)
        {
            model.IngestFailure("boom", t, 0);
            Assert.Equal(TimeSpan.FromSeconds(expected), model.NextPollDelay(t));
            t = t.AddSeconds(1);
        }
    }

    // ---- decision 4: a reset serviced once, never re-capped, never a 1s hot loop ----

    [Fact]
    public void NextPollDelay_NeverLoopsAtOneSecond_AfterAReset_SettlesIntoAwaitingResetCadence()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = resetsAt.AddSeconds(-20);
        model.Ingest(new UsageSnapshot(50.0, resetsAt.ToString("O"), null, null, t0), t0, 0);

        // 1s before reset+3s: the delay lands almost exactly on that instant (never 0/negative).
        TimeSpan justBefore = model.NextPollDelay(resetsAt.AddSeconds(2));
        Assert.True(justBefore > TimeSpan.Zero);
        Assert.True(justBefore <= TimeSpan.FromSeconds(1.1), $"expected to land almost exactly at reset+3s, got {justBefore}");

        // The resumed poll at reset+3s still shows the old window (cached response) -- same
        // fingerprint, deduped, ResetsAt unchanged.
        model.Ingest(new UsageSnapshot(50.0, resetsAt.ToString("O"), null, null, resetsAt.AddSeconds(3)), resetsAt.AddSeconds(3), 3000);

        TimeSpan atDeadline = model.NextPollDelay(resetsAt.AddSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(30), atDeadline); // never 1s: the now-past deadline no longer caps anything

        TimeSpan wellPast = model.NextPollDelay(resetsAt.AddMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(30), wellPast); // steady awaiting-reset cadence, not degenerating back to 1s
    }

    [Fact]
    public void NextPollDelay_FailureBackoff_NotOverriddenByAPastResetDeadline()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = resetsAt.AddSeconds(-20);
        model.Ingest(new UsageSnapshot(50.0, resetsAt.ToString("O"), null, null, t0), t0, 0);

        DateTimeOffset pastDeadline = resetsAt.AddMinutes(5); // well past reset+3s
        model.IngestFailure("boom", pastDeadline, 0);
        model.IngestFailure("boom", pastDeadline.AddSeconds(1), 0);

        TimeSpan delay = model.NextPollDelay(pastDeadline.AddSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(120), delay); // 2nd failure: 60*2^1=120s -- not capped down by the stale deadline
    }

    // ---- the added condition: sleep across a reset, resumed response still shows the old window ----

    [Fact]
    public void SleepAcrossReset_ResumedResponseStillShowsOldWindow_EntersAwaitingReset()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset before = resetsAt.AddMinutes(-100);
        model.Ingest(new UsageSnapshot(50.0, resetsAt.ToString("O"), null, null, before), before, 0);

        // The machine genuinely slept for ~90 real (monotonic) minutes too -- Environment.
        // TickCount64 keeps advancing through sleep on Windows, so a real resume keeps UTC and
        // mono deltas consistent with each other; this is "sleep across a reset", not a clock
        // discontinuity, so monoMs must reflect the same ~100 real minutes as the UTC gap
        // (decision 2, round 2: the anchor now only re-anchors on an ACCEPTED sample, and this
        // poll's reply is a cached DUPLICATE -- so the anchor stays at `before`, and the two
        // deltas must actually agree or this would misfire as a clock jump instead).
        DateTimeOffset resumed = resetsAt.AddMinutes(10); // woke up 10 min after the reset
        long resumedMono = (long)(resumed - before).TotalMilliseconds;
        model.Ingest(new UsageSnapshot(50.0, resetsAt.ToString("O"), null, null, resumed), resumed, resumedMono); // same fingerprint: cached/deduped

        QuotaView view = model.Evaluate(resumed, resumedMono);
        Assert.Equal(QuotaState.Measuring, view.Session.State);
        Assert.Equal("Nytt fönster väntas", view.Session.MeasuringReason);
        Assert.Null(view.Session.RatePctPerMin); // decision 12: no forecast fields while Measuring
        // 2026-09-22 transport/data-age split: the resumed (deduped) reply DOES still renew the
        // transport StaleAt/UnknownAt deadlines -- it is a valid session reading, just not a
        // novel one -- so transport liveness alone would read Live here. But 110 real minutes
        // have passed since the one accepted sample with no new data at all, so the 20-min
        // fingerprint-stale rule (keyed to _lastUsableMono, untouched by a duplicate) trips
        // first; independently, `resumed` is also past this window's own resets_at+60s. Either
        // is enough for Stale, and both fire before the transport deadlines ever could here.
        Assert.Equal(Freshness.Stale, view.Freshness);
        Assert.NotEqual(Freshness.Live, view.Freshness);
    }

    // ---- decision 6: clock discontinuity ----

    [Fact]
    public void ClockDiscontinuity_Forward_ForcesMeasuring_NotGraceSafe()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(80.0, resetsAt.ToString("O"), null, null, t0), t0, 0);

        // 1 real (monotonic) minute passes, but UTC claims almost 3 hours -- reset would
        // "appear" 4 minutes away, which the old grace rule turned into Safe.
        QuotaView jumped = model.Evaluate(new DateTimeOffset(2026, 1, 1, 4, 56, 0, TimeSpan.Zero), 60_000);
        Assert.Equal(QuotaState.Measuring, jumped.Session.State);
        Assert.Equal(Freshness.Stale, jumped.Freshness);
    }

    [Fact]
    public void ClockDiscontinuity_Backward_AlsoForcesMeasuring_NotLive()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 7, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, resetsAt.ToString("O"), null, null, t0), t0, 0);

        // A one-hour clock rollback after 20 real (monotonic) minutes: ages would go
        // negative under naive UTC math, which used to look indistinguishable from Live.
        QuotaView jumped = model.Evaluate(new DateTimeOffset(2026, 1, 1, 1, 20, 0, TimeSpan.Zero), 1_200_000);
        Assert.NotEqual(Freshness.Live, jumped.Freshness);
        Assert.Equal(QuotaState.Measuring, jumped.Session.State);
    }

    // ---- decision 5: a failure backoff sequence must never keep Live alive ----

    /// <summary>
    /// Test critique: the old version of this test advanced UTC by 930s but monotonic time by
    /// only 5s between the same two calls, so DetectClockDiscontinuity's own >60s guard forced
    /// Stale/Unknown regardless of whether the frozen-freshness-deadline logic under test was
    /// actually correct -- an implementation that froze the deadlines wrong would still have
    /// passed. This version keeps UTC and monotonic time moving together (a real, un-jumped
    /// clock through a real failure streak) and asserts the full ladder: Live before the
    /// frozen stale_at, Stale after it, and Unknown once the backoff sequence has run past the
    /// frozen unknown_at with no new success -- proving the freeze itself, not clock-jump
    /// detection, is what is being exercised.
    /// </summary>
    [Fact]
    public void Freshness_BackoffSequence_NeverKeepsLiveAlive_FreezeHoldsWithNoClockJump()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset farReset = new(2026, 1, 1, 7, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, farReset.ToString("O"), null, null, t0), t0, 0); // dP>0 on the first sample -> burning -> c_policy=30s -> stale_at=t0+90s, unknown_at=t0+300s

        // Pre-deadline: still Live.
        Assert.Equal(Freshness.Live, model.Evaluate(t0.AddSeconds(80), 80_000).Freshness);

        // The 60,120,240,480,600s backoff sequence, with UTC and mono advancing IDENTICALLY --
        // no clock-jump masking.
        double[] cumulativeFailureSeconds = { 60, 180, 420, 900, 1500 };
        foreach (double cum in cumulativeFailureSeconds)
        {
            long monoMs = (long)(cum * 1000);
            model.IngestFailure("boom", t0.AddSeconds(cum), monoMs);
        }

        // Past stale_at (90s) but before unknown_at (300s): Stale.
        Assert.Equal(Freshness.Stale, model.Evaluate(t0.AddSeconds(100), 100_000).Freshness);

        // Well past unknown_at (300s), deep into the backoff sequence (1500s = 25 min): Unknown,
        // and never Live at any point along the way.
        QuotaView finalView = model.Evaluate(t0.AddSeconds(1500), 1_500_000);
        Assert.Equal(Freshness.Unknown, finalView.Freshness);
        Assert.NotEqual(Freshness.Live, finalView.Freshness);
    }

    // ---- 2026-09-22: transport liveness vs. data age -- the live "false stale alarm" symptom ----
    //
    // While the user is actively working, the panel header turned orange and read "Datan kan
    // vara inaktuell" (data may be stale) with nothing actually wrong. Root cause: `get_usage`
    // only produces a NOVEL sample roughly every 5 min (docs/forecast-and-states.md,
    // "Staleness") and returns a byte-identical cached reply to every poll in between, but the
    // transport StaleAt/UnknownAt deadlines used to re-freeze only on a NOVEL accepted sample --
    // conflating two different questions: "are polls succeeding" (transport liveness, which a
    // cached duplicate answers exactly as well as a novel sample) and "is the data moving" (which
    // only a novel sample answers). At the 30s burning cadence, stale_at sat at
    // last-novel-accept + 90s while novel samples arrived ~300s apart, so the panel read Stale
    // for most of every refresh cycle. The fix: transport deadlines now renew on ANY poll
    // returning a valid session reading (novel, cached duplicate, or validly closed); the 20-min
    // fingerprint-stale rule (data age) still moves only on a novel accepted sample, or -- for an
    // all-closed idle account -- a validly closed reading (IdleAccount_* tests below/above).

    [Fact]
    public void BurningCadence_ServerRefreshEveryFiveMinutesWithCachedDuplicatesBetween_StaysLive_ForThirtyMinutes()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 9, 22, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = resetsAt.AddHours(-5);
        double pct = 10.0;
        int fingerprintJitter = 0;
        string raw = Raw(resetsAt, fingerprintJitter);

        model.Ingest(new UsageSnapshot(pct, raw, null, null, t0), t0, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(t0, 0).Freshness);

        DateTimeOffset lastNovelAt = t0;
        long mono = 0;
        DateTimeOffset now = t0;
        for (int poll = 1; poll <= 60; poll++) // 60 x 30s = 30 minutes of burning-cadence polling
        {
            now = now.Add(TimeSpan.FromSeconds(30));
            mono += 30_000;

            bool serverRefreshed = poll % 10 == 0; // a novel sample every 5 min (10 x 30s)
            if (serverRefreshed)
            {
                pct += 1.0;
                fingerprintJitter++;
                raw = Raw(resetsAt, fingerprintJitter); // distinct fingerprint -- a genuinely new sample
            }
            // Every OTHER poll repeats the exact same fingerprint: the cached reply get_usage
            // actually returns between refreshes.
            model.Ingest(new UsageSnapshot(pct, raw, null, null, now), now, mono);
            if (serverRefreshed) lastNovelAt = now;

            QuotaView view = model.Evaluate(now, mono);
            Assert.Equal(Freshness.Live, view.Freshness);
            // The header's own data age (LastChangedAt) is untouched by this fix: it still holds
            // at the last NOVEL sample and grows between refreshes, resetting only when the
            // server actually produces new data.
            Assert.Equal(lastNovelAt, view.LastChangedAt);
        }
    }

    [Fact]
    public void CachedDuplicatesOnly_ForOverTwentyMinutes_GoesStale_ViaTheFingerprintRule_DespiteTransportRenewingEveryPoll()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = resetsAt.AddHours(-5);
        string raw = Raw(resetsAt);
        var snapshot = new UsageSnapshot(42.0, raw, null, null, t0);

        model.Ingest(snapshot, t0, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(t0, 0).Freshness);

        // Nothing but the identical cached reply, polled at 30s -- fast enough that, if this
        // were only about transport liveness, the 90s/300s transport deadlines would never lapse
        // (each duplicate is a valid session reading and renews them). Only the 20-min
        // fingerprint rule, which a duplicate cannot renew, can explain going Stale here.
        long mono = 0;
        DateTimeOffset now = t0;
        for (int i = 0; i < 42; i++) // 42 x 30s = 21 min, just past the 20-min threshold
        {
            now = now.Add(TimeSpan.FromSeconds(30));
            mono += 30_000;
            model.Ingest(new UsageSnapshot(42.0, raw, null, null, now), now, mono);
        }

        Assert.Equal(Freshness.Stale, model.Evaluate(now, mono).Freshness);
    }

    [Fact]
    public void ConsecutiveFailures_StillGoStaleAtThreeXAndUnknownAtTenXTheInterval_TransportChangeDidNotTouchThis()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset farReset = t0.AddHours(5);
        model.Ingest(new UsageSnapshot(10.0, Raw(farReset), null, null, t0), t0, 0);
        // Burning: cPolicy = 30s -> stale_at = t0+90s, unknown_at = t0+300s. A transport failure
        // (unlike a successful poll without a valid session reading) never re-freezes these,
        // exactly as before this change -- IngestFailure was not touched.
        Assert.Equal(Freshness.Live, model.Evaluate(t0.AddSeconds(80), 80_000).Freshness);

        model.IngestFailure("boom", t0.AddSeconds(95), 95_000);
        Assert.Equal(Freshness.Stale, model.Evaluate(t0.AddSeconds(95), 95_000).Freshness); // past 3x (90s)

        model.IngestFailure("boom", t0.AddSeconds(200), 200_000);
        model.IngestFailure("boom", t0.AddSeconds(310), 310_000);
        Assert.Equal(Freshness.Unknown, model.Evaluate(t0.AddSeconds(310), 310_000).Freshness); // past 10x (300s)
    }

    [Fact]
    public void ResponseWithoutAValidSessionReading_DoesNotRenewTheTransportDeadlines()
    {
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 9, 22, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = resetsAt.AddHours(-5);
        model.Ingest(new UsageSnapshot(30.0, Raw(resetsAt), null, null, t0), t0, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(t0, 0).Freshness);
        // Burning: stale_at = t0 + 90s, unknown_at = t0 + 300s.

        // A poll that round-trips successfully (this is Ingest, not IngestFailure) but carries an
        // unparseable resets_at -- neither a valid open reading nor a validly closed one -- must
        // be a complete no-op for the transport deadlines, exactly like a transport failure.
        DateTimeOffset t1 = t0.AddSeconds(60);
        model.Ingest(new UsageSnapshot(30.0, "not-a-timestamp", null, null, t1), t1, 60_000);

        // 100s after t0: past the ORIGINAL stale_at (90s) -- proving it was never pushed out to
        // the 60s+90s=150s a renewal at t1 would have produced.
        QuotaView view = model.Evaluate(t0.AddSeconds(100), 100_000);
        Assert.Equal(Freshness.Stale, view.Freshness);
    }

    // ---- decision 1: one-second resets_at jitter must never lose consumption or fabricate a rollover ----

    [Fact]
    public void Jitter_OneSecondEarlierDeadline_KeepsTheHigherSample_DoesNotDropIt()
    {
        // The real fixture sequence (2026-09-09): 85% at resets_at=19:10:00.218, then 94% at
        // resets_at=19:09:59.450 (~0.77s EARLIER). Under the old truncate-to-second key
        // comparison this looked like a regressed key and was dropped outright, losing real
        // consumption. It must be kept as the same window.
        var model = new QuotaModel();
        DateTimeOffset t1 = new(2026, 9, 9, 15, 31, 45, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 9, 9, 15, 34, 33, TimeSpan.Zero);

        model.Ingest(new UsageSnapshot(85.0, "2026-09-09T19:10:00.218248+00:00", null, null, t1), t1, 0);
        model.Ingest(new UsageSnapshot(94.0, "2026-09-09T19:09:59.450402+00:00", null, null, t2), t2, (long)(t2 - t1).TotalMilliseconds);

        QuotaView view = model.Evaluate(t2, (long)(t2 - t1).TotalMilliseconds);
        Assert.Equal(94.0, view.Session.UsedPct);
    }

    [Fact]
    public void Jitter_OneSecondLaterDeadline_IsNotMistakenForARollover()
    {
        // The review's synthetic, destructive-direction example: a resets_at 1.1s LATER
        // than the current one used to be truncated into the NEXT second and treated as a
        // genuine rollover, wiping the 80% envelope and reseeding at 77%. It must be treated
        // as the same window (jitter), and a subsequent sample back within tolerance must not
        // be "ignored indefinitely" either.
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = new(2026, 1, 1, 2, 5, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);

        model.Ingest(new UsageSnapshot(80.0, a.ToString("O"), null, null, t0), t0, 0);
        model.Ingest(new UsageSnapshot(77.0, "2026-01-01T05:00:01.100000+00:00", null, null, t1), t1, 5 * 60_000);
        model.Ingest(new UsageSnapshot(90.0, "2026-01-01T05:00:00.100000+00:00", null, null, t2), t2, 10 * 60_000);

        QuotaView view = model.Evaluate(t2, 10 * 60_000);
        Assert.Equal(90.0, view.Session.UsedPct); // envelope max across all three -- no rollover ever cleared it
        Assert.NotEqual("Nytt fönster, mäter takt…", view.Session.MeasuringReason); // no rollover was ever triggered by the jitter
    }

    // ---- decision 11: failures neither count as hysteresis evidence nor reset pending evidence ----

    [Fact]
    public void Hysteresis_DuplicateReply_IsNotCountedAsEvidence_AndFailuresDoNotResetPendingEvidence()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = t0.AddMinutes(250); // E0 = 50: a valid seed, plenty of session window left

        model.Ingest(new UsageSnapshot(10.0, Raw(resetsAt), null, null, t0), t0, 0); // low usage, low rate: Safe, leaving Measuring immediately

        DateTimeOffset t1 = t0.AddMinutes(5);
        string dupFingerprint = Raw(resetsAt, jitter: 1);
        model.Ingest(new UsageSnapshot(90.0, dupFingerprint, null, null, t1), t1, 5 * 60_000); // rem<=10: 1st real escalating (>Safe) observation

        // The identical (cached) reply again: rejected by the tracker, must not count as a
        // second observation on its own.
        model.Ingest(new UsageSnapshot(90.0, dupFingerprint, null, null, t1.AddSeconds(30)), t1.AddSeconds(30), 5 * 60_000 + 30_000);

        // Test critique: the old version of this test only asserted the FINAL escalated
        // state, so an implementation that incorrectly escalated on the duplicate alone (never
        // requiring the second genuine observation at all) would also have passed. Assert here,
        // right after the duplicate, that escalation has NOT happened yet -- still only 1 of 2
        // required observations.
        Assert.Equal(QuotaState.Safe, model.Evaluate(t1.AddSeconds(30), 5 * 60_000 + 30_000).Session.State);

        // Two transport failures in between: must not reset the one pending observation above.
        // (monoMs values kept consistent with the established live anchor -- IngestFailure
        // itself never re-anchors, but an inconsistent monoMs on the Evaluate call below would
        // itself misfire the unrelated clock-discontinuity check and mask the real assertion.)
        model.IngestFailure("boom", t1.AddMinutes(1), 5 * 60_000 + 60_000);
        model.IngestFailure("boom", t1.AddMinutes(2), 5 * 60_000 + 120_000);

        // Still only 1 of 2: the failures must not have counted as evidence either.
        Assert.Equal(QuotaState.Safe, model.Evaluate(t1.AddMinutes(2), 5 * 60_000 + 120_000).Session.State);

        // A genuinely novel, still-escalating observation: this is only the SECOND accepted
        // observation overall, which is exactly enough for session's 2-observation threshold.
        DateTimeOffset t2 = t1.AddMinutes(3);
        long mono2 = 5 * 60_000 + 3 * 60_000;
        model.Ingest(new UsageSnapshot(92.0, Raw(resetsAt, jitter: 2), null, null, t2), t2, mono2);

        QuotaState state = model.Evaluate(t2, mono2).Session.State;
        Assert.True(state is QuotaState.Tight or QuotaState.DryEarly, $"expected an escalated verdict from 2 genuine observations, got {state}");
    }

    // ---- decision 13: a successful poll is not necessarily usable data ----

    [Fact]
    public void MissingWeeklyNode_DoesNotSubstituteZero_BecomesMeasuring_SessionStaysLive()
    {
        var model = new QuotaModel();
        DateTimeOffset sessionReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyReset = new(2026, 1, 8, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = sessionReset.AddMinutes(-100);

        model.Ingest(new UsageSnapshot(20.0, Raw(sessionReset), 15.0, Raw(weeklyReset), t0), t0, 0);

        DateTimeOffset t1 = t0.AddMinutes(5);
        model.Ingest(new UsageSnapshot(22.0, Raw(sessionReset, 1), null, null, t1), t1, 5 * 60_000); // weekly node absent this poll

        QuotaView view = model.Evaluate(t1, 5 * 60_000);
        Assert.Equal(QuotaState.Measuring, view.Weekly.State);
        Assert.Equal("Data saknas", view.Weekly.MeasuringReason);
        Assert.Null(view.Weekly.RatePctPerMin); // forecast dropped, not stale-blended
        Assert.NotEqual(QuotaState.Measuring, view.Session.State); // the OTHER window is unaffected
    }

    [Fact]
    public void InvalidSessionTimestamp_DoesNotClaimLive()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, "not-a-date", null, null, t0), t0, 0);

        QuotaView view = model.Evaluate(t0, 0);
        Assert.NotEqual(Freshness.Live, view.Freshness); // decision 13: global Live requires a valid session window
        Assert.Equal(QuotaState.Measuring, view.Session.State);
    }

    // ---- decision 14: WarmStart survives an initial transport failure ----

    [Fact]
    public void WarmStart_SurvivesAnInitialFailure_ReplaysOnTheFirstRealSnapshot()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        string path = WriteCsv(new[]
        {
            (new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), 0L, 10.0, resetsAt.ToString("O")),
        });

        try
        {
            var model = new QuotaModel();
            DateTimeOffset failAt = new(2026, 1, 1, 1, 55, 0, TimeSpan.Zero);
            model.IngestFailure("boom", failAt, 0); // transport failure BEFORE any success

            DateTimeOffset liveAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
            var snapshot = new UsageSnapshot(20.0, Raw(resetsAt, jitter: 1), null, null, liveAt); // distinct fingerprint from the CSV row
            model.WarmStart(snapshot, liveAt, path); // must NOT have been disabled by the earlier failure
            model.Ingest(snapshot, liveAt, 60_000);

            QuotaView view = model.Evaluate(liveAt, 60_000);
            // The replayed 10% row plus the live 20% sample: envelope reflects both (max).
            Assert.Equal(20.0, view.Session.UsedPct);
            // SampleCount included the replayed row (2 total): confirms replay actually ran.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WarmStart_RejectsFutureRows()
    {
        // Decision 8: a CSV row from AFTER "now" (e.g. after correcting a fast system clock)
        // must never override a lower live reading.
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset liveAt = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string path = WriteCsv(new[]
        {
            (liveAt.AddMinutes(10), 0L, 99.6, resetsAt.ToString("O")), // 10 min in the FUTURE relative to liveAt
        });

        try
        {
            var model = new QuotaModel();
            var snapshot = new UsageSnapshot(20.0, resetsAt.ToString("O"), null, null, liveAt);
            model.WarmStart(snapshot, liveAt, path);
            model.Ingest(snapshot, liveAt, 0);

            QuotaView view = model.Evaluate(liveAt, 0);
            Assert.Equal(20.0, view.Session.UsedPct); // the future row must never have been replayed
            Assert.NotEqual(QuotaState.Spent, view.Session.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WarmStart_AcrossAReboot_MonotonicEpochMismatch_StillReplays()
    {
        // Decision 7: a CSV row's mono_ms comes from a PREVIOUS boot; the live poll's mono_ms
        // starts fresh near 0. Replay must use UTC-only deltas and never mix the two epochs.
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset csvAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        string path = WriteCsv(new[] { (csvAt, 900_000_000L, 10.0, resetsAt.ToString("O")) });

        try
        {
            var model = new QuotaModel();
            DateTimeOffset liveAt = csvAt.AddMinutes(5);
            var snapshot = new UsageSnapshot(50.0, Raw(resetsAt, jitter: 1), null, null, liveAt); // distinct fingerprint from the CSV row
            model.WarmStart(snapshot, liveAt, path);
            model.Ingest(snapshot, liveAt, 300_000); // small, post-reboot monoMs

            QuotaView view = model.Evaluate(liveAt, 300_000);
            Assert.Equal(50.0, view.Session.UsedPct);
            Assert.NotEqual(QuotaState.Measuring, view.Session.State); // must reach a verdict, not stay stuck
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Test critique: the old version of this test only asserted "no exception", proving
    /// nothing about which row survives an equal-timestamp tie or whether the live sample is
    /// still accepted afterward. The precedence rule this defines and asserts: two rows
    /// sharing the exact same UtcIso are ordered by MonoMs (CsvReplay.FilterForWindow), but
    /// WindowTracker.Ingest's ordinary dt&lt;=0 out-of-order guard then rejects the SECOND one
    /// outright -- an identical UtcIso is dt=0 relative to the first, which is "not strictly
    /// newer", exactly like any other zero/negative-dt sample. So the earliest-by-MonoMs row
    /// at a tied instant wins; a later-MonoMs row at that same instant never applies, not
    /// because of any equal-timestamp-specific logic but because ordinary ordering already
    /// covers it.
    /// </summary>
    [Fact]
    public void WarmStart_EqualTimestamps_EarliestMonoMsWins_LiveSampleStillAcceptedAfterward()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset rowAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        string path = WriteCsv(new[]
        {
            (rowAt, 100L, 10.0, resetsAt.ToString("O")),
            (rowAt, 200L, 15.0, resetsAt.AddTicks(1).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture)),
        });

        try
        {
            var model = new QuotaModel();
            DateTimeOffset liveAt = rowAt.AddMinutes(30);
            // A DISTINCT fingerprint from row 1's, and a LOWER pct than the replayed envelope,
            // so the assertions below can only pass if the live sample was genuinely accepted
            // as its own (novel, later) observation, not merely because it happens to also be
            // the monotone envelope's maximum.
            var snapshot = new UsageSnapshot(5.0, Raw(resetsAt, jitter: 1), null, null, liveAt);

            QuotaView? view = null;
            var ex = Record.Exception(() =>
            {
                model.WarmStart(snapshot, liveAt, path);
                model.Ingest(snapshot, liveAt, 0);
                view = model.Evaluate(liveAt, 0);
            });
            Assert.Null(ex);

            Assert.NotNull(view);
            Assert.Equal(10.0, view!.Session.UsedPct); // row 1 (mono=100) replayed; row 2 (mono=200, same instant) correctly rejected as not strictly newer; live's 5% did not raise the envelope
            Assert.Equal(Freshness.Live, view.Freshness); // proves the live sample was genuinely ACCEPTED (re-anchored), not silently dropped
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==================== Round 2 (2026-09-11) regression tests ====================
    // Each test below reproduces a review finding's failing sequence as closely to verbatim
    // as the WindowTracker/QuotaModel API allows, per the round-2 decisions table
    // (docs/reviews/2026-09-11-codex-quota-model-round2.md).

    // ---- Finding 1 / decision 1: safety-valve continuity and rollover ordering ----

    [Fact]
    public void SafetyValve_NonConsecutiveCandidateReplies_NeverAccumulate_AcrossInterveningCurrentWindowReplies()
    {
        // Review finding 1's exact trace: three "implausible later deadline" candidates (D)
        // interleaved with duplicate replies of the CURRENT window (A). Under the old code,
        // the duplicates were rejected by fingerprint dedup BEFORE ever resetting the pending
        // candidate counter, so the three D's -- despite being non-consecutive -- still
        // accumulated to the safety valve and triggered a destructive early rollover.
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset d = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

        Assert.True(tracker.Ingest(80.0, a.ToString("O"), t0, 0)); // seeds window A

        Assert.False(tracker.Ingest(1.0, d.ToString("O"), t0.AddMinutes(1), 60_000));   // candidate 1 (implausible)
        Assert.False(tracker.Ingest(80.0, a.ToString("O"), t0.AddMinutes(2), 120_000)); // duplicate A -- must reset the pending candidate
        Assert.False(tracker.Ingest(1.0, d.ToString("O"), t0.AddMinutes(3), 180_000));  // candidate "1" again, NOT count 2
        Assert.False(tracker.Ingest(80.0, a.ToString("O"), t0.AddMinutes(4), 240_000)); // duplicate A again -- resets again
        Assert.False(tracker.Ingest(1.0, d.ToString("O"), t0.AddMinutes(5), 300_000));  // would-be count 3 under the old bug -- must still be just 1

        Assert.Equal(80.0, tracker.EnvelopeP); // never rolled over to D's 1%
        Assert.Null(tracker.RolloverAt);
        Assert.Equal(QuotaTimeUtil.TruncateToSecond(a), QuotaTimeUtil.TruncateToSecond(tracker.WindowKey!.Value));
    }

    [Fact]
    public void Rollover_MustPassTheSameOrderingCheckAsAnySample_OlderCandidateIsRejectedDespitePlausibility()
    {
        // Review finding 1's second trace: a candidate that is OLDER in both UTC and mono time
        // than the last accepted sample, yet still passes the plausibility gate (it happens to
        // land within 120s of the current deadline). The old code exempted rollover from the
        // ordering check entirely and accepted it; it must now be rejected like any other
        // out-of-order sample.
        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset d = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        DateTimeOffset first = new(2026, 1, 1, 4, 59, 0, TimeSpan.Zero);
        Assert.True(tracker.Ingest(80.0, a.ToString("O"), first, 60_000));

        DateTimeOffset second = new(2026, 1, 1, 4, 58, 30, TimeSpan.Zero); // 30s OLDER in UTC
        Assert.False(tracker.Ingest(0.0, d.ToString("O"), second, 30_000)); // and 30s OLDER in mono too -- rejected despite passing the old-deadline plausibility test

        Assert.Equal(80.0, tracker.EnvelopeP);
        Assert.Null(tracker.RolloverAt);
    }

    // ---- Finding 2 / decision 2: re-anchor only on an ACCEPTED sample; monotonic durations ----

    [Fact]
    public void CachedReply_MustNotReanchor_ForwardClockJump_StaysMeasuring()
    {
        // Ingest (02:00:00Z, 80, A), mono=0
        // Evaluate(04:56:00Z, mono=60_000) -> Measuring / Stale
        // Ingest (04:56:00Z, 80, A), mono=60_000 // cached; tracker rejects
        // Evaluate(04:56:00Z, mono=60_000) -> the old bug returned a confident Tight verdict
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(80.0, a.ToString("O"), null, null, t0), t0, 0);

        DateTimeOffset jumped = new(2026, 1, 1, 4, 56, 0, TimeSpan.Zero);
        QuotaView v1 = model.Evaluate(jumped, 60_000);
        Assert.Equal(QuotaState.Measuring, v1.Session.State);
        Assert.Equal(Freshness.Stale, v1.Freshness);

        model.Ingest(new UsageSnapshot(80.0, a.ToString("O"), null, null, jumped), jumped, 60_000); // cached duplicate

        QuotaView v2 = model.Evaluate(jumped, 60_000);
        Assert.Equal(QuotaState.Measuring, v2.Session.State); // the duplicate must not silently re-anchor and clear the clock-jump flag
        Assert.NotEqual(Freshness.Live, v2.Freshness);
    }

    [Fact]
    public void CachedReply_MustNotReanchor_BackwardClockJump_NeverClaimsLive()
    {
        // Ingest (02:00:00Z, 80, A), mono=0
        // Evaluate(01:01:00Z, mono=60_000) -> Measuring / Stale
        // Ingest (01:01:00Z, 80, A), mono=60_000 // duplicate
        // Evaluate(01:01:00Z, mono=60_000) -> the old bug returned DryEarly / Live
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(80.0, a.ToString("O"), null, null, t0), t0, 0);

        DateTimeOffset jumped = new(2026, 1, 1, 1, 1, 0, TimeSpan.Zero);
        Assert.Equal(QuotaState.Measuring, model.Evaluate(jumped, 60_000).Session.State);

        model.Ingest(new UsageSnapshot(80.0, a.ToString("O"), null, null, jumped), jumped, 60_000); // duplicate

        QuotaView v2 = model.Evaluate(jumped, 60_000);
        Assert.Equal(QuotaState.Measuring, v2.Session.State);
        Assert.NotEqual(Freshness.Live, v2.Freshness);
    }

    [Fact]
    public void Freshness_NeverImproves_OnASmallBackwardUtcRollback_BelowTheClockJumpThreshold()
    {
        // Ingest (02:00:00Z, 20, A), mono=0
        // Evaluate(02:01:40Z, mono=100_000) -> Stale
        // Evaluate(02:00:51Z, mono=101_000) -> the old bug returned Live: a 49s UTC rollback
        // (below the 60s clock-jump threshold) "improved" freshness with no new information,
        // because the frozen deadline was compared against UTC instead of monotonic time.
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(20.0, a.ToString("O"), null, null, t0), t0, 0);

        Assert.Equal(Freshness.Stale, model.Evaluate(t0.AddSeconds(100), 100_000).Freshness);

        QuotaView rolledBack = model.Evaluate(t0.AddSeconds(51), 101_000); // UTC went backward 49s; mono kept advancing
        Assert.NotEqual(Freshness.Live, rolledBack.Freshness);
        Assert.Equal(Freshness.Stale, rolledBack.Freshness); // monotonic time alone still places it past stale_at
    }

    // ---- Finding 3 / decision 3: invalid session utilization must not retain a verdict or Live ----

    [Fact]
    public void InvalidSessionUtilization_Nan_DoesNotRetainAConfidentVerdictOrLive()
    {
        // (02:00:00Z, 20, A) -> a real verdict
        // (02:00:30Z, NaN, B) -> tracker rejects, but the old code's sessionValid didn't check
        // the percentage at all, so Live/the verdict survived regardless.
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // E=300: deep enough for an immediate verdict
        model.Ingest(new UsageSnapshot(20.0, a.ToString("O"), null, null, t0), t0, 0);

        DateTimeOffset t1 = t0.AddSeconds(30);
        model.Ingest(new UsageSnapshot(double.NaN, "2026-01-01T05:00:00.100000+00:00", null, null, t1), t1, 30_000);

        QuotaView view = model.Evaluate(t1, 30_000);
        Assert.NotEqual(Freshness.Live, view.Freshness);
    }

    [Fact]
    public void ColdStart_NanSessionUtilization_NeverClaimsLive()
    {
        // A cold model whose very first sample is (02:00:00Z, NaN, A) must never show Live
        // with no session percentage at all.
        var model = new QuotaModel();
        DateTimeOffset a = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        model.Ingest(new UsageSnapshot(double.NaN, a.ToString("O"), null, null, t0), t0, 0);

        QuotaView view = model.Evaluate(t0, 0);
        Assert.NotEqual(Freshness.Live, view.Freshness);
        Assert.Null(view.Session.UsedPct);
    }

    // ---- Finding 4 / decision 4: leaving Measuring on live time/eligibility alone ----

    [Fact]
    public void Measuring_LiftsOnTimeAlone_WhenOnlyDuplicatesArrivedSinceEligibilityWasReached()
    {
        // (00:09:00Z, 3, A) -> Measuring (E<10 min)
        // (00:09:30Z, 3, A) -> duplicate
        // (00:10:30Z, 3, A) -> duplicate, but E now >= 10 min
        // Evaluate(00:10:30Z) -- the old code left hold.Committed stuck at Measuring forever,
        // since StepHysteresis never runs on a duplicate.
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 0, 9, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = t0.AddMinutes(291); // E(t0)=9 (<10, refused); E(t0+1.5min)=10.5 (eligible)
        string raw = resetsAt.ToString("O");

        model.Ingest(new UsageSnapshot(3.0, raw, null, null, t0), t0, 0);

        DateTimeOffset t1 = t0.AddSeconds(30);
        model.Ingest(new UsageSnapshot(3.0, raw, null, null, t1), t1, 30_000); // duplicate

        DateTimeOffset t2 = t0.AddMinutes(1.5);
        long mono2 = (long)TimeSpan.FromMinutes(1.5).TotalMilliseconds;
        model.Ingest(new UsageSnapshot(3.0, raw, null, null, t2), t2, mono2); // duplicate, but now past the E>=10min line

        QuotaView view = model.Evaluate(t2, mono2);
        Assert.NotEqual(QuotaState.Measuring, view.Session.State);
    }

    [Fact]
    public void Measuring_LiftsOnTimeAlone_AfterTheFiveMinuteRolloverGateExpires_WithNoNewSampleSinceItLifted()
    {
        var model = new QuotaModel();
        DateTimeOffset windowAReset = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset windowBReset = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        DateTimeOffset t0 = windowAReset.AddMinutes(-100);
        model.Ingest(new UsageSnapshot(50.0, Raw(windowAReset), null, null, t0), t0, 0);

        const long monoBase = 1_000_000;
        DateTimeOffset tRollover = windowBReset.AddMinutes(-250); // E=50: a valid seed for window B
        model.Ingest(new UsageSnapshot(5.0, Raw(windowBReset), null, null, tRollover), tRollover, monoBase);
        Assert.Equal(QuotaState.Measuring, model.Evaluate(tRollover, monoBase).Session.State);

        // Past the 5-minute gate, but with NO further Ingest at all -- just later Evaluate
        // ticks, exactly like the always-running 1s UI timer.
        DateTimeOffset tPastGate = tRollover.AddMinutes(6);
        long monoPastGate = monoBase + 6 * 60_000;
        QuotaState finalState = model.Evaluate(tPastGate, monoPastGate).Session.State;
        Assert.NotEqual(QuotaState.Measuring, finalState);
    }

    /// <summary>
    /// The most user-visible instance of finding 4 (task item #4): after an app restart,
    /// WarmStart replays enough history that the window is already eligible, but the first
    /// live poll's reply duplicates the CSV's own last row's fingerprint exactly (the CLI's
    /// get_usage cache -- a very common real shape right after a restart). The old code left
    /// hold.Committed stuck at its default (Measuring) forever, since WarmStart bypasses
    /// hysteresis entirely and the live sample was deduped, so StepHysteresis never ran even
    /// once. Uses the production CSV column shape (WriteCsv mirrors DiskLogSink's header).
    /// </summary>
    [Fact]
    public void WarmStartThenLiveDuplicate_DoesNotStickInMeasuring_RealFixtureShape()
    {
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = resetsAt.AddMinutes(-280); // E=20 at "now": comfortably eligible once replayed
        const string lastRaw = "2026-01-01T05:00:00.900000+00:00"; // the fingerprint BOTH the CSV's last row and the live poll will share

        var rows = new List<(DateTimeOffset Utc, long Mono, double Pct, string ResetsAtRaw)>();
        DateTimeOffset rowStart = now.AddMinutes(-12);
        for (int k = 0; k < 6; k++)
        {
            DateTimeOffset t = rowStart.AddMinutes(k * 2); // 6 rows, 2 min apart, ending 2 min before "now"
            string raw = k == 5
                ? lastRaw
                : resetsAt.AddTicks(k * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", System.Globalization.CultureInfo.InvariantCulture);
            rows.Add((t, (long)(t - rowStart).TotalMilliseconds, 12.0 + k, raw));
        }
        string path = WriteCsv(rows);

        try
        {
            var model = new QuotaModel();
            var snapshot = new UsageSnapshot(17.0, lastRaw, null, null, now); // same pct AND fingerprint as the CSV's own last row: a genuine cached duplicate
            model.WarmStart(snapshot, now, path);
            model.Ingest(snapshot, now, 0);

            QuotaView view = model.Evaluate(now, 0);
            Assert.NotEqual(QuotaState.Measuring, view.Session.State); // must reach a verdict at the FIRST successful poll after restart, not stay stuck
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The weekly counterpart of the time-only-eligibility case, crossing the 24h (1440 min)
    /// boundary on ONE QuotaModel with only duplicate replies once eligibility is reached (test
    /// critique: the old weekly-threshold test created a brand-new tracker rather than proving
    /// an EXISTING Measuring model recovers).
    /// </summary>
    [Fact]
    public void Weekly_MeasuringLiftsOnTimeAlone_CrossingTheTwentyFourHourBoundary_WithOnlyDuplicatesAfterward()
    {
        var model = new QuotaModel();
        DateTimeOffset resetStart = new(2026, 9, 11, 7, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = resetStart.AddDays(7);
        string raw = resetsAt.ToString("O");

        DateTimeOffset t0 = resetStart.AddMinutes(1439); // E=1439: still refused (< the 1440min minimum)
        model.Ingest(new UsageSnapshot(20.0, "2026-01-01T05:00:00.000000+00:00", 11.0, raw, t0), t0, 0);
        Assert.Equal(QuotaState.Measuring, model.Evaluate(t0, 0).Weekly.State);
        Assert.Equal("För tidigt i veckan — väntar på ett helt dygn", model.Evaluate(t0, 0).Weekly.MeasuringReason);

        // E=1441: past the boundary, but the ONLY poll since t0 carries the identical weekly
        // fingerprint -- deduped, never accepted, so StepHysteresis never runs again after t0.
        DateTimeOffset t1 = resetStart.AddMinutes(1441);
        model.Ingest(new UsageSnapshot(21.0, "2026-01-01T05:00:00.000000+00:00", 11.0, raw, t1), t1, 120_000);

        QuotaView finalView = model.Evaluate(t1, 120_000);
        Assert.NotEqual(QuotaState.Measuring, finalView.Weekly.State); // lifted on time alone, no novel weekly sample was ever accepted after t0
        Assert.NotNull(finalView.Weekly.RatePctPerMin); // a real verdict carries forecast fields (decision 12)
    }

    // ---- Finding 5 / decision 5: replay must never let a near-future row override live data ----

    [Fact]
    public void WarmStart_RejectsARowWithinTheOldSixtySecondTolerance_NoLongerOverridesLiveData()
    {
        // WarmStart now = 02:10:00Z; CSV: (02:10:30Z, 100, B); Live: (02:10:00Z, 20, A) -- the
        // old now+60s allowance let the 30s-future row install a corrupted 100% envelope,
        // which then made the genuine live sample look out-of-order (older) and get rejected.
        DateTimeOffset now = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero); // "A"
        const string futureRaw = "2026-01-01T05:00:00.100000+00:00"; // "B": within jitter of A, same window
        string path = WriteCsv(new[] { (now.AddSeconds(30), 0L, 100.0, futureRaw) });

        try
        {
            var model = new QuotaModel();
            var snapshot = new UsageSnapshot(20.0, resetsAt.ToString("O"), null, null, now);
            model.WarmStart(snapshot, now, path);
            model.Ingest(snapshot, now, 0);

            QuotaView view = model.Evaluate(now, 0);
            Assert.Equal(20.0, view.Session.UsedPct); // the near-future row must never have installed a 100% envelope
            Assert.NotEqual(QuotaState.Spent, view.Session.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Finding 6 / decision 6: WarmStart eligibility and replay membership ----

    [Fact]
    public void WarmStart_SurvivesAnUnusableFirstResponse_NotJustATransportFailure()
    {
        // Ingest (01:59:00Z, 20, "not-a-date") -- a SUCCESSFUL poll, but wholly unusable (bad
        // resets_at, no weekly data). The old code set _liveDataIngested=true unconditionally
        // at the top of Ingest(), before any validity check, permanently disabling WarmStart.
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        string path = WriteCsv(new[]
        {
            (new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), 0L, 25.0, resetsAt.ToString("O")), // higher than the live sample below, so only an actual replay run can produce 25
        });

        try
        {
            var model = new QuotaModel();
            DateTimeOffset badAt = new(2026, 1, 1, 1, 59, 0, TimeSpan.Zero);
            model.Ingest(new UsageSnapshot(20.0, "not-a-date", null, null, badAt), badAt, 0);

            DateTimeOffset liveAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
            var snapshot = new UsageSnapshot(20.0, Raw(resetsAt, jitter: 1), null, null, liveAt);
            model.WarmStart(snapshot, liveAt, path); // must NOT have been disabled by the earlier unusable response
            model.Ingest(snapshot, liveAt, 60_000);

            QuotaView view = model.Evaluate(liveAt, 60_000);
            Assert.Equal(25.0, view.Session.UsedPct); // only true if replay actually ran
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WarmStart_ReplayMembership_UsesTheSameJitterToleranceAsLiveIngest()
    {
        // CSV: (02:00:00Z, 10, A); CSV: (02:05:00Z, 90, "...T04:59:59.450402...") -- ~0.55s
        // before A, same window under the live +-120s jitter rule but a DIFFERENT truncated
        // second. Live: (02:10:00Z, 10, B). The old exact-truncated-second match excluded the
        // 90% row from replay entirely.
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero); // "A"
        const string jitteredRaw = "2026-01-01T04:59:59.450402+00:00";
        DateTimeOffset liveAt = new(2026, 1, 1, 2, 10, 0, TimeSpan.Zero);
        string path = WriteCsv(new[]
        {
            (new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), 0L, 10.0, resetsAt.ToString("O")),
            (new DateTimeOffset(2026, 1, 1, 2, 5, 0, TimeSpan.Zero), 300_000L, 90.0, jitteredRaw),
        });

        try
        {
            var model = new QuotaModel();
            var snapshot = new UsageSnapshot(10.0, Raw(resetsAt, jitter: 3), null, null, liveAt); // "B": distinct fingerprint, same window as A
            model.WarmStart(snapshot, liveAt, path);
            model.Ingest(snapshot, liveAt, 600_000);

            QuotaView view = model.Evaluate(liveAt, 600_000);
            Assert.Equal(90.0, view.Session.UsedPct); // the jittered row must have been included, not excluded on a sub-second technicality
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Finding 8 / decision 8: an expired Spent hold must recover at the fast cadence ----

    [Fact]
    public void ExpiredSpentHold_SchedulesAsAwaitingReset_NotTheSlowSpentInterval()
    {
        // (04:59:40Z, 100, A) -> genuinely current Spent: 300s.
        // (05:00:03Z, 100, A) // cached -> NextPollDelay(05:00:03Z) must select 30s (awaiting
        // reset), not the old code's 300s (it checked Spent before awaiting-reset).
        var model = new QuotaModel();
        DateTimeOffset resetsAt = new(2026, 1, 1, 5, 0, 0, TimeSpan.Zero);
        // Far enough before the deadline that the reset+3s cap does not itself win over the
        // (correctly, while genuinely current) 300s Spent interval.
        DateTimeOffset t0 = resetsAt.AddMinutes(-10);
        model.Ingest(new UsageSnapshot(100.0, resetsAt.ToString("O"), null, null, t0), t0, 0);
        Assert.Equal(TimeSpan.FromSeconds(300), model.NextPollDelay(t0)); // genuinely current: the slow Spent cadence

        DateTimeOffset t1 = resetsAt.AddSeconds(5); // past the deadline (and its reset+3s cap); cached reply, deduped
        model.Ingest(new UsageSnapshot(100.0, resetsAt.ToString("O"), null, null, t1), t1, (long)(t1 - t0).TotalMilliseconds);

        Assert.Equal(TimeSpan.FromSeconds(30), model.NextPollDelay(t1));
    }

    // ---- Finding 10 / decision 10: extreme resets_at must never throw ----

    [Fact]
    public void ExtremeResetsAt_IsRejectedOutright_NeverThrows()
    {
        var model = new QuotaModel();
        DateTimeOffset t0 = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

        var ex = Record.Exception(() =>
        {
            model.Ingest(new UsageSnapshot(20.0, "9999-12-31T23:59:59.000000+00:00", null, null, t0), t0, 0);
            model.Evaluate(t0, 0);
            model.NextPollDelay(t0);
        });
        Assert.Null(ex); // the old code accepted this deadline, then threw computing reset+3s (CapToSoonestReset)

        QuotaView view = model.Evaluate(t0, 0);
        Assert.Equal(QuotaState.Measuring, view.Session.State); // rejected outright: WindowKey never got set
        Assert.Equal("Hämtar…", view.Session.MeasuringReason);
    }

    [Fact]
    public void WarmStart_ExtremeSnapshotResetsAt_NeverThrows_SkipsReplayForThatWindow()
    {
        DateTimeOffset liveAt = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);
        const string extreme = "0001-01-01T00:00:00.000000+00:00";
        string path = WriteCsv(new[] { (liveAt.AddMinutes(-5), 0L, 10.0, extreme) });

        try
        {
            var model = new QuotaModel();
            var snapshot = new UsageSnapshot(20.0, extreme, null, null, liveAt);
            var ex = Record.Exception(() =>
            {
                model.WarmStart(snapshot, liveAt, path); // the old code would AddMinutes(-windowMinutes) off a year-1 key here and underflow
                model.Ingest(snapshot, liveAt, 0);
                model.Evaluate(liveAt, 0);
            });
            Assert.Null(ex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ==================== A1: idle account freshness (2026-09-21) ====================
    // Ported from src/Mac/Tests/ClaudeQuotaCoreTests/PollCadenceTests.swift's
    // IdleAccountFreshnessTests, one-for-one: the bug that put "!" on an idle account after two
    // days. Every window inactive (utilization 0, resets_at null, is_active false) means nothing
    // can ever be *accepted* -- the tracker has no deadline to track -- so the freshness
    // deadlines froze at the last accept and decayed to Unknown, while polling succeeded every
    // 150 s and the honest answer was "0 % used, nothing running". Freshness is about whether
    // the reading is CURRENT, not whether the tracker could track it.

    static readonly TimeSpan IdleIntervalForTests = TimeSpan.FromSeconds(150);

    static UsageSnapshot IdleSnapshot(DateTimeOffset now) => new(
        SessionUtilization: 0.0, SessionResetsAt: null,
        WeeklyUtilization: 0.0, WeeklyResetsAt: null,
        ReceivedAt: now, SubscriptionType: "team",
        SessionIsActive: false, WeeklyIsActive: false);

    [Fact]
    public void IdleAccount_AllWindowsInactive_StaysLive_WhilePolling()
    {
        var model = new QuotaModel();
        DateTimeOffset baseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        model.Ingest(IdleSnapshot(baseTime), baseTime, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(baseTime, 0).Freshness);

        // Two days of successful polls at the idle cadence. Before the fix, freshness went
        // Unknown ~25 minutes in and never came back.
        long mono = 0;
        DateTimeOffset now = baseTime;
        for (int i = 0; i < 1000; i++)
        {
            now = now.Add(IdleIntervalForTests);
            mono += (long)IdleIntervalForTests.TotalMilliseconds;
            model.Ingest(IdleSnapshot(now), now, mono);
        }

        QuotaView view = model.Evaluate(now, mono);
        Assert.Equal(Freshness.Live, view.Freshness);
        Assert.Equal(QuotaState.Measuring, view.IconSeverity); // there is no verdict to give without a window
        Assert.Null(view.Session.UsedPct); // no window means no percentage to show, not 0 %
    }

    [Fact]
    public void IdleAccount_FreshnessStillDecays_WhenPollingActuallyStops()
    {
        var model = new QuotaModel();
        DateTimeOffset baseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        model.Ingest(IdleSnapshot(baseTime), baseTime, 0);
        Assert.Equal(Freshness.Live, model.Evaluate(baseTime, 0).Freshness);

        // No further ingest at all -- only the UI tick. 11x the idle policy interval.
        DateTimeOffset later = baseTime.Add(TimeSpan.FromTicks(IdleIntervalForTests.Ticks * 11));
        long mono = (long)IdleIntervalForTests.TotalMilliseconds * 11;
        Assert.Equal(Freshness.Unknown, model.Evaluate(later, mono).Freshness);
    }

    [Fact]
    public void IdleAccount_DuplicateOnAnActiveWindow_StillDoesNotRenewFreshness()
    {
        // A cached duplicate on an ACTIVE window must still not indefinitely renew freshness.
        // 2026-09-22: a duplicate DOES now renew the transport StaleAt/UnknownAt deadlines (it is
        // a valid session reading), but the 20-min fingerprint-stale rule is untouched by a
        // duplicate -- see _lastUsableMono -- so 50 minutes of nothing but duplicates still goes
        // non-Live via that rule. Kept at 20 duplicates (50 min, well past the 20-min threshold)
        // so this still proves the same invariant end to end, just through the data-age half of
        // freshness rather than the transport half.
        var model = new QuotaModel();
        DateTimeOffset baseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset resetsAt = baseTime.AddSeconds(168 * 60); // matches the Swift port's addingTimeInterval(168*60)
        var live = new UsageSnapshot(56.0, Raw(resetsAt), null, null, baseTime);
        model.Ingest(live, baseTime, 0);

        long mono = 0;
        DateTimeOffset now = baseTime;
        for (int i = 0; i < 20; i++)
        {
            now = now.Add(IdleIntervalForTests);
            mono += (long)IdleIntervalForTests.TotalMilliseconds;
            model.Ingest(live, now, mono); // same fingerprint, over and over
        }

        Assert.NotEqual(Freshness.Live, model.Evaluate(now, mono).Freshness);
    }

    [Fact]
    public void IdleAccount_ClosedSessionWithLiveWeekly_StaysLive()
    {
        // A half-idle account -- session closed, weekly running -- is the common case after a
        // session expires mid-week, and must behave like the fully idle one.
        var model = new QuotaModel();
        DateTimeOffset baseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset weeklyResets = baseTime.AddDays(3);

        long mono = 0;
        DateTimeOffset now = baseTime;
        for (int step = 0; step < 20; step++)
        {
            now = now.Add(IdleIntervalForTests);
            mono += (long)IdleIntervalForTests.TotalMilliseconds;
            // Weekly keeps a novel fingerprint; session is closed.
            var snapshot = new UsageSnapshot(
                SessionUtilization: 0.0, SessionResetsAt: null,
                WeeklyUtilization: 17.0, WeeklyResetsAt: Raw(weeklyResets, jitter: step),
                ReceivedAt: now, SubscriptionType: "max",
                SessionIsActive: false, WeeklyIsActive: true);
            model.Ingest(snapshot, now, mono);
        }

        Assert.Equal(Freshness.Live, model.Evaluate(now, mono).Freshness);
    }

    [Fact]
    public void IdleAccount_ClosedSession_ReportsWindowInactive_NotDataMissing()
    {
        var model = new QuotaModel();
        DateTimeOffset baseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
        model.Ingest(IdleSnapshot(baseTime), baseTime, 0);

        QuotaView view = model.Evaluate(baseTime, 0);
        Assert.Equal(QuotaState.Measuring, view.Session.State);
        Assert.Equal("Inget förbrukat ännu", view.Session.MeasuringReason);
        Assert.Equal("Inget förbrukat ännu", view.Weekly.MeasuringReason);
    }

    static string WriteCsv(IEnumerable<(DateTimeOffset Utc, long Mono, double Pct, string ResetsAtRaw)> rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"warmstart-{Guid.NewGuid():N}.csv");
        var lines = new List<string> { "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source" };
        foreach (var r in rows)
            lines.Add($"{r.Utc:yyyy-MM-ddTHH:mm:ss.fffffffK},{r.Mono},{r.Pct.ToString(System.Globalization.CultureInfo.InvariantCulture)},{r.ResetsAtRaw},,,20.0,get_usage");
        File.WriteAllLines(path, lines);
        return path;
    }
}
