using System.Globalization;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Replays real captured rows (tests/fixtures/rollover-2026-09-11.csv, a verbatim
/// 11:10-11:26 UTC slice of the machine's actual logs\window-shape.csv) through
/// QuotaModel's live Ingest/Evaluate path, covering the first observed session
/// rollover of 2026-09-11: the server keeps replying with the OLD window
/// (resets_at 11:20:00.712790, 60%) for several minutes after the NEW window
/// (resets_at ~16:20:00, 0%) was first observed, alternating old/new -- a real
/// "replica lag" pattern, not a synthetic one.
/// </summary>
public class RolloverReplayTests
{
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "rollover-2026-09-11.csv");

    static DateTimeOffset ParseUtc(string s) => DateTimeOffset.Parse(
        s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    static UsageSnapshot ToSnapshot(CsvReplay.RawRow row) =>
        new(row.SessionPct!.Value, row.SessionResetsAtRaw, row.WeeklyPct, row.WeeklyResetsAtRaw, row.UtcIso);

    // ---- Test A: warm app across the reset -- ingest every row in order, exactly as the live poll loop does ----

    [Fact]
    public void WarmAcrossReset_RolloverAcceptedAt112033_OldReplicasNeverWinBack_ReasonLiftsAfterFiveMinutes()
    {
        IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(FixturePath);
        Assert.True(rows.Count > 30, $"expected the fixture's full 11:10-11:26 slice, got {rows.Count}");

        DateTimeOffset rolloverAcceptedAt = ParseUtc("2026-09-11T11:20:33.9659745+00:00");
        DateTimeOffset stillGatedAt = ParseUtc("2026-09-11T11:24:10.5576429+00:00"); // <5 min after the rollover

        var model = new QuotaModel();
        QuotaView? viewAtRollover = null;
        QuotaView? gatedView = null;
        QuotaView? finalView = null;

        foreach (CsvReplay.RawRow row in rows)
        {
            model.Ingest(ToSnapshot(row), row.UtcIso, row.MonoMs);
            // Evaluate at this row's own (utcNow, monoMs) -- matching the live poll loop, and
            // keeping the clock-discontinuity check's monotonic anchor consistent throughout.
            QuotaView view = model.Evaluate(row.UtcIso, row.MonoMs);
            finalView = view;

            if (row.UtcIso == rolloverAcceptedAt) viewAtRollover = view;
            if (row.UtcIso == stillGatedAt) gatedView = view;

            if (row.UtcIso > rolloverAcceptedAt)
            {
                // The old window's replica rows (60%, resets_at 11:20:00.712790) keep
                // arriving for several minutes; none of them may ever win back.
                Assert.NotEqual(60.0, view.Session.UsedPct);
                // The reset that just happened was already accepted -- the view must
                // never fall back into "awaiting a new window" once it has one.
                Assert.NotEqual("Nytt fönster väntas", view.Session.MeasuringReason);
            }
        }

        Assert.NotNull(viewAtRollover);
        Assert.Equal(QuotaState.Measuring, viewAtRollover!.Session.State);
        Assert.Equal("Nytt fönster, mäter takt…", viewAtRollover.Session.MeasuringReason);
        Assert.Equal(0.0, viewAtRollover.Session.UsedPct); // envelope cleared, not carried over from the old window's 60

        // Still well inside the 5-minute post-rollover gate (11:24:10 is <4 min later).
        Assert.NotNull(gatedView);
        Assert.Equal(QuotaState.Measuring, gatedView!.Session.State);
        Assert.Equal("Nytt fönster, mäter takt…", gatedView.Session.MeasuringReason);

        // The fixture's last row (11:26:10) lands ~5.6 min after the rollover -- past the
        // 5-minute gate. Real usage stayed at 0% the whole captured window, so the model
        // honestly still can't reach a Safe/Tight verdict (too little elapsed time AND too
        // little usage) -- but the ROLLOVER-specific reason must be gone, proving the gate
        // itself lifted rather than staying stuck forever.
        Assert.NotNull(finalView);
        Assert.Equal(QuotaState.Measuring, finalView!.Session.State);
        Assert.NotEqual("Nytt fönster, mäter takt…", finalView.Session.MeasuringReason);
        Assert.Equal(0.0, finalView.Session.UsedPct); // still never rolled back to the old window's 60
        Assert.Equal(16, finalView.Session.ResetsAt!.Value.Hour); // stayed on the new window's key, not 11:20:00.712790

        // Test critique: checking that the rollover-specific reason text disappears does not
        // prove the committed Measuring state can actually LIFT to a verdict -- the fixture's
        // own real data stays pinned at 0% throughout, which alone would keep it Measuring for
        // an unrelated reason (too little usage). Extend the trace past the 10-minute mark with
        // real (>=3%) usage, interleaved with duplicates and timer-only evaluations (no poll at
        // all), and prove a genuine verdict is reached.
        CsvReplay.RawRow lastRow = rows[^1];
        DateTimeOffset.TryParse(lastRow.SessionResetsAtRaw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out DateTimeOffset newWindowResetsAt);

        DateTimeOffset extUtc = lastRow.UtcIso;
        long extMono = lastRow.MonoMs;
        for (int k = 1; k <= 5; k++)
        {
            extUtc = extUtc.AddMinutes(2.5);
            extMono += (long)TimeSpan.FromMinutes(2.5).TotalMilliseconds;
            string fp = newWindowResetsAt.AddTicks(k * 17).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffK", CultureInfo.InvariantCulture);
            double pct = 2.0 + k; // crosses the P>=3 line at k=1 and keeps rising: a genuine burn, not noise

            model.Ingest(ToSnapshot(new CsvReplay.RawRow(extUtc, extMono, pct, fp, lastRow.WeeklyPct, lastRow.WeeklyResetsAtRaw)), extUtc, extMono);
            // A duplicate reply right after, and a timer-only evaluation with no poll at all:
            // neither must block the eventual verdict.
            model.Ingest(ToSnapshot(new CsvReplay.RawRow(extUtc, extMono, pct, fp, lastRow.WeeklyPct, lastRow.WeeklyResetsAtRaw)), extUtc.AddSeconds(5), extMono + 5000);
            model.Evaluate(extUtc.AddSeconds(10), extMono + 10_000);
        }

        Assert.True(extUtc - rolloverAcceptedAt > TimeSpan.FromMinutes(10), "test setup sanity: must actually be past the 10-minute rollover gate");
        QuotaView extendedView = model.Evaluate(extUtc.AddSeconds(10), extMono + 10_000);
        Assert.True(extendedView.Session.UsedPct >= 3.0);
        Assert.NotEqual(QuotaState.Measuring, extendedView.Session.State);
        Assert.NotNull(extendedView.Session.RatePctPerMin); // decision 12: only non-null once a real verdict is committed
    }

    // ---- Test B: cold start straddling the reset -- the very first live sample is already the old window, past its own deadline ----

    [Fact]
    public void ColdStartStraddlingReset_FirstSampleIsAwaitingReset_ThenRolloverAccepted_LaterOldRowIgnored()
    {
        IReadOnlyList<CsvReplay.RawRow> rows = CsvReplay.ReadRows(FixturePath);

        CsvReplay.RawRow firstSample = rows.Single(r => r.UtcIso == ParseUtc("2026-09-11T11:20:41.3991006+00:00")); // old window, session=60, its own reset (11:20:00.712790) already passed
        CsvReplay.RawRow rolloverRow = rows.Single(r => r.UtcIso == ParseUtc("2026-09-11T11:22:40.7981607+00:00")); // new window, session=0
        CsvReplay.RawRow oldAgainRow = rows.Single(r => r.UtcIso == ParseUtc("2026-09-11T11:23:10.5613122+00:00")); // old window replica, once more

        var model = new QuotaModel();

        // The FIRST sample this cold app ever sees is a cached reply for a window whose
        // deadline has already passed -- no rollover has been observed yet to compare
        // against, so this must read as "awaiting reset", not as a 60%-used verdict.
        model.Ingest(ToSnapshot(firstSample), firstSample.UtcIso, firstSample.MonoMs);
        QuotaView afterFirst = model.Evaluate(firstSample.UtcIso, firstSample.MonoMs);
        Assert.Equal(QuotaState.Measuring, afterFirst.Session.State);
        Assert.Equal("Nytt fönster väntas", afterFirst.Session.MeasuringReason);

        // The new window's first sample arrives: a genuine, plausible rollover (now is well
        // past the old deadline), accepted and clearing the stale 60% envelope.
        model.Ingest(ToSnapshot(rolloverRow), rolloverRow.UtcIso, rolloverRow.MonoMs);
        QuotaView afterRollover = model.Evaluate(rolloverRow.UtcIso, rolloverRow.MonoMs);
        Assert.Equal(QuotaState.Measuring, afterRollover.Session.State);
        Assert.Equal("Nytt fönster, mäter takt…", afterRollover.Session.MeasuringReason); // a real rollover now, not awaiting-reset any more
        Assert.Equal(0.0, afterRollover.Session.UsedPct);
        Assert.Equal(16, afterRollover.Session.ResetsAt!.Value.Hour); // tracks the new window's deadline

        // The old window's replica shows up once more: it must be ignored outright, not
        // treated as a second rollover back to the old window.
        model.Ingest(ToSnapshot(oldAgainRow), oldAgainRow.UtcIso, oldAgainRow.MonoMs);
        QuotaView afterOldAgain = model.Evaluate(oldAgainRow.UtcIso, oldAgainRow.MonoMs);
        Assert.Equal(0.0, afterOldAgain.Session.UsedPct); // the old row's 60% never applied
        Assert.Equal(16, afterOldAgain.Session.ResetsAt!.Value.Hour); // still the new window's key
        Assert.NotEqual("Nytt fönster väntas", afterOldAgain.Session.MeasuringReason); // did not revert to awaiting-reset
    }
}
