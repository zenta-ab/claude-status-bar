import Foundation
import Testing

@testable import ClaudeQuotaCore

/// The Swift half of M2's acceptance criterion: replay `tests/fixtures/window-shape-2026-09-11.csv`
/// — the *same file* the C# `GoldenReplayTests` reads — through the exact ingest path a live poll
/// uses, and reach the same verdicts.
///
/// That file contains real replica regressions (15→14, 19→18, both with a fresh and with a
/// previously-seen fingerprint), a real one-second `resets_at` jitter (2026-09-09, decision 1's
/// motivating case), and a stale, unrelated older window (a different boot) that a genuine
/// rollover must still separate from the current one.
@Suite("Golden replay")
struct GoldenReplayTests {

    @Test("the real fixture replays cleanly: no rollover, regressions ignored, a sensible verdict")
    func realFixtureReplaysCleanly() throws {
        let allRows = Fixtures.rows(Fixtures.windowShape)
        #expect(allRows.count > 50, "expected the golden fixture to have many rows, got \(allRows.count)")

        // "Now" = the time of the last poll in the file; its resets_at defines the current key.
        let lastRow = try #require(allRows.last)
        let now = lastRow.utcIso
        let sessionResetsAt = try #require(QuotaTimeUtil.parseResetsAt(lastRow.sessionResetsAtRaw))
        let sessionKey = QuotaTimeUtil.truncateToSecond(sessionResetsAt)

        let matched = CsvReplay.filterForWindow(allRows, kind: .session, windowKey: sessionKey,
                                                windowMinutes: QuotaWindows.sessionMinutes, now: now)

        // The fixture also carries an older, unrelated window (a different boot): the filter must
        // have actually excluded those rows, not passed everything through.
        #expect(matched.count < allRows.count)
        #expect(matched.count > 50)
        for row in matched {
            let ymd = row.utcIso.utcYMD
            #expect(ymd.year == 2026 && ymd.month == 9 && ymd.day == 11)
        }

        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        var previousEnvelope = 0.0
        for row in matched {
            // Some rows are legitimate cached duplicates (rejected); must never throw.
            tracker.ingest(pct: row.pct, resetsAtRaw: row.resetsAtRaw, utcNow: row.utcIso, monoMs: row.monoMs)

            // The monotone-envelope invariant PER OBSERVATION, not just once at the end: a broad
            // final-value check alone cannot catch an intermediate dip that a later, higher
            // sample happens to paper over.
            #expect(tracker.envelopeP >= previousEnvelope,
                    "envelope dipped from \(previousEnvelope) to \(tracker.envelopeP)")
            previousEnvelope = tracker.envelopeP
        }

        // No rollover during replay (by construction, every matched row shares one key).
        #expect(tracker.rolloverAt == nil)
        let windowKey = try #require(tracker.windowKey)
        #expect(QuotaTimeUtil.truncateToSecond(windowKey) == sessionKey)

        // Regressions ignored: the envelope equals the true max, not the last value.
        let expectedMax = try #require(matched.map(\.pct).max())
        #expect(tracker.envelopeP == expectedMax)

        // A percentage below the final maximum does not by itself establish a regression, so
        // confirm an actual adjacent non-increasing pair exists in the SOURCE data.
        var foundGenuineRegression = false
        for index in 1..<matched.count where matched[index].pct < matched[index - 1].pct {
            foundGenuineRegression = true
            break
        }
        #expect(foundGenuineRegression,
                "expected the known real replica regressions (15→14, 19→18) in the source data")

        let snap = tracker.computeSnapshot(utcNow: now, monoMs: lastRow.monoMs, isWeekly: false)
        let forecast = try #require(snap.forecast)

        // Stronger than "positive and Safe-or-Tight": with ~20 % used and hours to reset the
        // model's own algebra requires depletion to land AFTER the reset, i.e. zero shortfall —
        // which alone rules out DryEarly (its only other trigger, rem <= 3, plainly does not
        // hold at ~20 % either).
        #expect(forecast.ratePctPerMin > 0.0 && forecast.ratePctPerMin < 5.0,
                "expected a small positive burn rate, got \(forecast.ratePctPerMin)")
        #expect(forecast.shortfallMinutes == 0.0)
        #expect(forecast.remainingPct > 50.0)

        let state = RawStateClassifier.classify(usedPct: tracker.envelopeP, forecast: forecast,
                                                windowMinutes: QuotaWindows.sessionMinutes,
                                                refused: snap.refused)
        #expect(state == .safe || state == .tight,
                "expected a plausible verdict for ~\(tracker.envelopeP) % used with hours to reset, got \(state)")
    }

    /// Decision 1's exact motivating case, from the real fixture: `resets_at` moves
    /// `19:10:00.218248` → `19:09:59.450402` (~0.77 s EARLIER) between the first two 2026-09-09
    /// polls, while usage rises 85 % → 94 %. Under the old truncate-to-the-second key comparison
    /// this looked like a regressed window key and the 94 % sample was silently dropped,
    /// permanently retaining 85 % while the server had already reported 94 %.
    @Test("the 2026-09-09 jitter rows keep the higher sample instead of dropping it")
    func jitterRowsKeepTheHigherSample() throws {
        let allRows = Fixtures.rows(Fixtures.windowShape)
        let sept9 = allRows
            .filter { let d = $0.utcIso.utcYMD; return d.year == 2026 && d.month == 9 && d.day == 9 }
            .sorted { $0.utcIso < $1.utcIso }
        #expect(sept9.count >= 2, "expected the fixture to carry the 2026-09-09 jitter rows")

        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        for row in sept9 {
            let pct = try #require(row.sessionPct)
            #expect(row.sessionResetsAtRaw != nil)
            tracker.ingest(pct: pct, resetsAtRaw: row.sessionResetsAtRaw, utcNow: row.utcIso, monoMs: row.monoMs)
        }

        #expect(tracker.envelopeP == 94.0, "the higher sample was dropped")
        #expect(tracker.rolloverAt == nil, "the ~0.77 s jitter was mistaken for a rollover")
    }

    /// The main replay pre-filters to one window key, which hides the very case the fixture
    /// demonstrates. This replays the ENTIRE fixture, unfiltered, through one tracker in
    /// chronological order, and lets the tracker's own decision-1 logic — not external key
    /// matching — tell same-window jitter (2026-09-09) apart from a genuine, much later window
    /// (2026-09-11, a different boot two days on).
    @Test("the full fixture separates jitter from the one genuine rollover")
    func fullFixtureSeparatesJitterFromRollover() {
        let chronological = Fixtures.rows(Fixtures.windowShape).sorted { $0.utcIso < $1.utcIso }

        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        var rolloverEvents = 0
        var lastSeenRolloverAt: Date?

        for row in chronological {
            guard let pct = row.sessionPct, row.sessionResetsAtRaw != nil else { continue }
            tracker.ingest(pct: pct, resetsAtRaw: row.sessionResetsAtRaw, utcNow: row.utcIso, monoMs: row.monoMs)
            if tracker.rolloverAt != lastSeenRolloverAt {
                rolloverEvents += 1
                lastSeenRolloverAt = tracker.rolloverAt
            }
        }

        // Exactly one genuine rollover (2026-09-09's window → 2026-09-11's): the within-09-09
        // jitter must not have counted as a second one.
        #expect(rolloverEvents == 1, "expected exactly one rollover, saw \(rolloverEvents)")
        #expect(tracker.envelopeP > 0)
    }

    /// Cross-implementation sanity on the shared file itself: if the CSV reader disagrees with
    /// the C# one about how many rows are well-formed, every verdict downstream is comparing
    /// different data and the whole acceptance criterion is void.
    @Test("the shared fixture parses to the row counts the C# suite sees")
    func fixtureParsesIdentically() {
        let rows = Fixtures.rows(Fixtures.windowShape)
        // window-shape-2026-09-11.csv is 128 lines: 1 header + 127 data rows, all well-formed.
        #expect(rows.count == 127, "expected 127 parsed rows, got \(rows.count)")

        let rollover = Fixtures.rows(Fixtures.rollover)
        // rollover-2026-09-11.csv is 37 lines: 1 header + 36 data rows.
        #expect(rollover.count == 36, "expected 36 parsed rows, got \(rollover.count)")
    }
}
