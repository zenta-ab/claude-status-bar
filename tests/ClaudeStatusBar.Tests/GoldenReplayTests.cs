using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Replays the real captured log (tests/fixtures/window-shape-2026-09-11.csv,
/// a verbatim copy of the machine's actual logs\window-shape.csv -- see
/// docs/forecast-and-states.md's evidence section) through the exact ingest
/// path a live poll uses. That file contains real replica regressions
/// (15->14, 19->18, both with a fresh and with a previously-seen fingerprint),
/// a real one-second resets_at jitter (2026-09-09, decision 1's motivating
/// case), and a stale, unrelated older window (also 2026-09-09, a different
/// boot/session) that a genuine rollover must still separate from the current
/// one.
/// </summary>
public class GoldenReplayTests
{
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "window-shape-2026-09-11.csv");

    [Fact]
    public void RealFixture_ReplaysCleanly_NoRollover_RegressionsIgnored_PositiveRate_SensibleState()
    {
        IReadOnlyList<CsvReplay.RawRow> allRows = CsvReplay.ReadRows(FixturePath);
        Assert.True(allRows.Count > 50, $"expected the golden fixture to have many rows, got {allRows.Count}");

        // "Now" = the time of the last poll in the file; its resets_at defines the current window key.
        CsvReplay.RawRow lastRow = allRows[^1];
        DateTimeOffset now = lastRow.UtcIso;
        Assert.NotNull(lastRow.SessionResetsAtRaw);
        QuotaTimeUtil.TryParseResetsAt(lastRow.SessionResetsAtRaw, out DateTimeOffset sessionResetsAt);
        DateTimeOffset sessionKey = QuotaTimeUtil.TruncateToSecond(sessionResetsAt);

        IReadOnlyList<CsvReplay.MatchedRow> matched = CsvReplay.FilterForWindow(
            allRows, WindowKind.Session, sessionKey, QuotaWindows.SessionMinutes, now);

        // The fixture also carries an older, unrelated window (a different boot):
        // the filter must have actually excluded those rows, not passed everything through.
        Assert.True(matched.Count < allRows.Count);
        Assert.True(matched.Count > 50);
        Assert.All(matched, row => Assert.True(row.UtcIso.Year == 2026 && row.UtcIso.Month == 9 && row.UtcIso.Day == 11));

        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        double previousEnvelope = 0.0;
        foreach (CsvReplay.MatchedRow row in matched)
        {
            tracker.Ingest(row.Pct, row.ResetsAtRaw, row.UtcIso, row.MonoMs); // some rows are legitimate cached duplicates (rejected); must never throw

            // Test critique: assert the monotone-envelope invariant PER OBSERVATION, not just
            // once at the end -- a broad final-value check alone cannot catch an intermediate
            // dip that a later, higher sample happens to paper over.
            Assert.True(tracker.EnvelopeP >= previousEnvelope, $"envelope dipped from {previousEnvelope} to {tracker.EnvelopeP} at {row.UtcIso:O}");
            previousEnvelope = tracker.EnvelopeP;
        }

        // No rollover was ever detected during replay (by construction, every matched row
        // already shares the one target window key). WindowKey now tracks the freshest raw
        // deadline (decision 1), not a truncated-to-the-second value, so compare truncated.
        Assert.Null(tracker.RolloverAt);
        Assert.NotNull(tracker.WindowKey);
        Assert.Equal(sessionKey, QuotaTimeUtil.TruncateToSecond(tracker.WindowKey!.Value));

        // Regressions ignored: the envelope equals the true max, not the last value. Review
        // critique: "a percentage below the final maximum does not establish a regression;
        // an entirely increasing sequence passes." Find an actual adjacent, non-increasing
        // pair in the SOURCE data (a genuine regression), and confirm the envelope did not
        // dip there.
        double expectedMax = matched.Max(r => r.Pct);
        Assert.Equal(expectedMax, tracker.EnvelopeP);

        bool foundGenuineRegression = false;
        for (int i = 1; i < matched.Count; i++)
        {
            if (matched[i].Pct < matched[i - 1].Pct)
            {
                foundGenuineRegression = true;
                break;
            }
        }
        Assert.True(foundGenuineRegression, "expected the known real replica regressions (e.g. 15->14, 19->18) to be present in the source data");

        WindowSnapshot snap = tracker.ComputeSnapshot(now, lastRow.MonoMs, isWeekly: false);
        Assert.True(snap.Forecast.HasValue);
        ForecastResult f = snap.Forecast!.Value;

        // Stronger than "positive and Safe-or-Tight" (review critique of the old assertion,
        // which is a weak plausibility check, not an independently justified oracle): with
        // ~20% used and hours left to reset, the model's own algebra requires depletion to
        // land AFTER the reset, i.e. zero shortfall -- which alone rules out DryEarly (its
        // only other trigger, rem <= 3, plainly does not hold at ~20% used either).
        Assert.True(f.RatePctPerMin is > 0.0 and < 5.0, $"expected a small positive burn rate for this fixture, got {f.RatePctPerMin}");
        Assert.Equal(0.0, f.ShortfallMinutes);
        Assert.True(f.RemainingPct > 50.0);

        QuotaState state = RawStateClassifier.Classify(tracker.EnvelopeP, f, QuotaWindows.SessionMinutes, snap.Refused);
        Assert.True(state is QuotaState.Safe or QuotaState.Tight, $"expected a plausible verdict for ~{tracker.EnvelopeP}% used with hours to reset, got {state}");
    }

    /// <summary>
    /// Decision 1's exact motivating case, from the real fixture: resets_at moves
    /// 19:10:00.218248 -&gt; 19:09:59.450402 (~0.77s EARLIER) between the first two
    /// 2026-09-09 polls, while usage rises 85% -&gt; 94%. Under the old truncate-to-
    /// the-second key comparison this looked like a regressed window key and the
    /// 94% sample was silently dropped, permanently retaining 85% while the server
    /// had already reported 94%.
    /// </summary>
    [Fact]
    public void Fixture_2026_09_09_JitterRows_KeepsTheHigherSample_NotDropped()
    {
        IReadOnlyList<CsvReplay.RawRow> allRows = CsvReplay.ReadRows(FixturePath);
        List<CsvReplay.RawRow> sept9Rows = allRows
            .Where(r => r.UtcIso.Year == 2026 && r.UtcIso.Month == 9 && r.UtcIso.Day == 9)
            .OrderBy(r => r.UtcIso)
            .ToList();
        Assert.True(sept9Rows.Count >= 2, "expected the fixture to carry the 2026-09-09 jitter rows");

        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        foreach (CsvReplay.RawRow row in sept9Rows)
        {
            Assert.NotNull(row.SessionResetsAtRaw);
            Assert.NotNull(row.SessionPct);
            tracker.Ingest(row.SessionPct!.Value, row.SessionResetsAtRaw, row.UtcIso, row.MonoMs); // must never throw
        }

        Assert.Equal(94.0, tracker.EnvelopeP); // kept, not dropped
        Assert.Null(tracker.RolloverAt); // the ~0.77s jitter must never have been mistaken for a rollover
    }

    /// <summary>
    /// Review critique: the main golden-replay test "cannot discover reset jitter" because
    /// it pre-filters to one exact window key before replay -- exactly hiding the case the
    /// fixture demonstrates. This replays the ENTIRE fixture, unfiltered, through one
    /// tracker in chronological order, and lets the tracker's own decision-1 logic (not
    /// external key-matching) tell same-window jitter (2026-09-09) apart from a genuine,
    /// much-later window (2026-09-11, a different boot two days on).
    /// </summary>
    [Fact]
    public void FullFixture_ReplayedContinuously_WithoutPreFiltering_SeparatesJitterFromTheGenuineRollover()
    {
        IReadOnlyList<CsvReplay.RawRow> allRows = CsvReplay.ReadRows(FixturePath);
        List<CsvReplay.RawRow> chronological = allRows.OrderBy(r => r.UtcIso).ToList();

        var tracker = new WindowTracker(QuotaWindows.SessionMinutes, halfLifeMinutes: 15);
        int rolloverEvents = 0;
        DateTimeOffset? lastSeenRolloverAt = null;

        foreach (CsvReplay.RawRow row in chronological)
        {
            if (row.SessionPct is null || row.SessionResetsAtRaw is null) continue;
            tracker.Ingest(row.SessionPct.Value, row.SessionResetsAtRaw, row.UtcIso, row.MonoMs); // must never throw
            if (tracker.RolloverAt != lastSeenRolloverAt)
            {
                rolloverEvents++;
                lastSeenRolloverAt = tracker.RolloverAt;
            }
        }

        // Exactly one genuine rollover (2026-09-09's window -> 2026-09-11's): the within-
        // 2026-09-09 jitter must not have counted as a second one.
        Assert.Equal(1, rolloverEvents);
        Assert.True(tracker.EnvelopeP > 0);
    }
}
