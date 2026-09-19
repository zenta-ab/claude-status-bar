import Foundation
import Testing

@testable import ClaudeQuotaCore

/// Replays `tests/fixtures/rollover-2026-09-11.csv` — the *same file* the C# `RolloverReplayTests`
/// reads — through `QuotaModel`'s live ingest/evaluate path. It is a verbatim 11:10–11:26 UTC
/// slice covering the first observed session rollover: the server keeps replying with the OLD
/// window (`resets_at 11:20:00.712790`, 60 %) for several minutes after the NEW window
/// (`resets_at ~16:20:00`, 0 %) was first observed, alternating old/new — a real replica-lag
/// pattern, not a synthetic one.
@Suite("Rollover replay")
struct RolloverReplayTests {

    // ---- Test A: warm app across the reset — ingest every row in order, as the poll loop does ----

    @Test("warm across the reset: rollover accepted, old replicas never win back, gate lifts")
    func warmAcrossReset() throws {
        let rows = Fixtures.rows(Fixtures.rollover)
        #expect(rows.count > 30, "expected the fixture's full 11:10–11:26 slice, got \(rows.count)")

        let rolloverAcceptedAt = utc("2026-09-11T11:20:33.9659745+00:00")
        let stillGatedAt = utc("2026-09-11T11:24:10.5576429+00:00")   // <5 min after the rollover

        let model = QuotaModel()
        var viewAtRollover: QuotaView?
        var gatedView: QuotaView?
        var finalView: QuotaView?

        for row in rows {
            model.ingest(.fromRow(row), utcNow: row.utcIso, monoMs: row.monoMs)
            // Evaluate at this row's own (utcNow, monoMs) — matching the live poll loop, and
            // keeping the clock-discontinuity check's monotonic anchor consistent throughout.
            let view = model.evaluate(utcNow: row.utcIso, monoMs: row.monoMs)
            finalView = view

            if row.utcIso == rolloverAcceptedAt { viewAtRollover = view }
            if row.utcIso == stillGatedAt { gatedView = view }

            if row.utcIso > rolloverAcceptedAt {
                // The old window's replica rows (60 %, resets_at 11:20:00.712790) keep arriving
                // for several minutes; none may ever win back.
                #expect(view.session.usedPct != 60.0)
                // The reset that just happened was already accepted — the view must never fall
                // back into "awaiting a new window" once it has one.
                #expect(view.session.measuringReason != "Nytt fönster väntas")
            }
        }

        let atRollover = try #require(viewAtRollover)
        #expect(atRollover.session.state == .measuring)
        #expect(atRollover.session.measuringReason == "Nytt fönster, mäter takt…")
        #expect(atRollover.session.usedPct == 0.0, "envelope was carried over from the old window's 60")

        // Still well inside the 5-minute post-rollover gate (11:24:10 is <4 min later).
        let gated = try #require(gatedView)
        #expect(gated.session.state == .measuring)
        #expect(gated.session.measuringReason == "Nytt fönster, mäter takt…")

        // The fixture's last row (11:26:10) lands ~5.6 min after the rollover — past the gate.
        // Real usage stayed at 0 % the whole captured window, so the model honestly still cannot
        // reach a Safe/Tight verdict (too little elapsed time AND too little usage) — but the
        // ROLLOVER-specific reason must be gone, proving the gate itself lifted.
        let final = try #require(finalView)
        #expect(final.session.state == .measuring)
        #expect(final.session.measuringReason != "Nytt fönster, mäter takt…")
        #expect(final.session.usedPct == 0.0)
        #expect(try #require(final.session.resetsAt).utcHour == 16, "did not stay on the new window's key")

        // Checking that the rollover reason disappears does not prove the committed Measuring
        // state can actually LIFT to a verdict — the fixture's own data stays pinned at 0 %,
        // which alone would keep it Measuring for an unrelated reason. Extend the trace past the
        // 10-minute mark with real (>= 3 %) usage, interleaved with duplicates and timer-only
        // evaluations (no poll at all), and prove a genuine verdict is reached.
        let lastRow = try #require(rows.last)
        let newWindowResetsAt = try #require(QuotaTimeUtil.parseResetsAt(lastRow.sessionResetsAtRaw))

        var extUtc = lastRow.utcIso
        var extMono = lastRow.monoMs
        for k in 1...5 {
            extUtc = extUtc.addingTimeInterval(150)          // 2.5 min
            extMono += Int64(150 * 1000)
            // A distinct fingerprint each time, a few microseconds apart — well inside the
            // ±120 s jitter tolerance, so it is the same window, just a novel reply.
            let fingerprint = wireFormat(newWindowResetsAt.addingTimeInterval(Double(k) * 0.000017))
            let pct = 2.0 + Double(k)   // crosses P >= 3 at k = 1 and keeps rising

            let snapshot = UsageSnapshot.make(session: pct, sessionResets: fingerprint,
                                              weekly: lastRow.weeklyPct, weeklyResets: lastRow.weeklyResetsAtRaw,
                                              observedAt: extUtc)
            model.ingest(snapshot, utcNow: extUtc, monoMs: extMono)
            // A duplicate reply right after, and a timer-only evaluation with no poll at all:
            // neither must block the eventual verdict.
            model.ingest(snapshot, utcNow: extUtc.addingTimeInterval(5), monoMs: extMono + 5000)
            _ = model.evaluate(utcNow: extUtc.addingTimeInterval(10), monoMs: extMono + 10_000)
        }

        #expect(extUtc.timeIntervalSince(rolloverAcceptedAt) > 600,
                "test setup sanity: must actually be past the 10-minute rollover gate")
        let extended = model.evaluate(utcNow: extUtc.addingTimeInterval(10), monoMs: extMono + 10_000)
        #expect(try #require(extended.session.usedPct) >= 3.0)
        #expect(extended.session.state != .measuring)
        // Decision 12: only non-nil once a real verdict is committed.
        #expect(extended.session.ratePctPerMin != nil)
    }

    // ---- Test B: cold start straddling the reset — the first live sample is already the old
    // window, past its own deadline ----

    @Test("cold start straddling the reset: awaiting reset, then rollover, then the old row ignored")
    func coldStartStraddlingReset() throws {
        let rows = Fixtures.rows(Fixtures.rollover)

        // Old window, session = 60, its own reset (11:20:00.712790) already passed.
        let firstSample = try #require(rows.first { $0.utcIso == utc("2026-09-11T11:20:41.3991006+00:00") })
        // New window, session = 0.
        let rolloverRow = try #require(rows.first { $0.utcIso == utc("2026-09-11T11:22:40.7981607+00:00") })
        // Old window replica, once more.
        let oldAgainRow = try #require(rows.first { $0.utcIso == utc("2026-09-11T11:23:10.5613122+00:00") })

        let model = QuotaModel()

        // The FIRST sample this cold app ever sees is a cached reply for a window whose deadline
        // has already passed — no rollover has been observed yet to compare against, so this must
        // read as "awaiting reset", not as a 60 %-used verdict.
        model.ingest(.fromRow(firstSample), utcNow: firstSample.utcIso, monoMs: firstSample.monoMs)
        let afterFirst = model.evaluate(utcNow: firstSample.utcIso, monoMs: firstSample.monoMs)
        #expect(afterFirst.session.state == .measuring)
        #expect(afterFirst.session.measuringReason == "Nytt fönster väntas")

        // The new window's first sample: a genuine, plausible rollover (now is well past the old
        // deadline), accepted and clearing the stale 60 % envelope.
        model.ingest(.fromRow(rolloverRow), utcNow: rolloverRow.utcIso, monoMs: rolloverRow.monoMs)
        let afterRollover = model.evaluate(utcNow: rolloverRow.utcIso, monoMs: rolloverRow.monoMs)
        #expect(afterRollover.session.state == .measuring)
        #expect(afterRollover.session.measuringReason == "Nytt fönster, mäter takt…")
        #expect(afterRollover.session.usedPct == 0.0)
        #expect(try #require(afterRollover.session.resetsAt).utcHour == 16)

        // The old window's replica shows up once more: ignored outright, not treated as a second
        // rollover back to the old window.
        model.ingest(.fromRow(oldAgainRow), utcNow: oldAgainRow.utcIso, monoMs: oldAgainRow.monoMs)
        let afterOldAgain = model.evaluate(utcNow: oldAgainRow.utcIso, monoMs: oldAgainRow.monoMs)
        #expect(afterOldAgain.session.usedPct == 0.0, "the old row's 60 % was applied")
        #expect(try #require(afterOldAgain.session.resetsAt).utcHour == 16)
        #expect(afterOldAgain.session.measuringReason != "Nytt fönster väntas")
    }
}
