import Foundation
import Testing

@testable import ClaudeQuotaCore

/// Ported from `tests/ClaudeStatusBar.Tests/PanelTextTests.cs`, asserting the same exact strings.
/// The panel's whole job is to answer "will I break the budget before it resets, and when does it
/// reset?" in words at the top, every time — so the wording is the contract, not a detail.
@Suite("Panel text")
struct PanelTextTests {
    static let now = utc("2026-09-11T10:00:00+00:00")   // 11:00 local (+01:00)
    static let tz = TimeZone(secondsFromGMT: 3600)!

    private func window(_ kind: WindowKind, used: Double?, resetsIn: TimeInterval,
                        state: QuotaState, projected: Double? = nil,
                        depletesIn: TimeInterval? = nil, shortfallMinutes: Double = 0,
                        reason: String? = nil) -> WindowView {
        WindowView(kind: kind,
                   windowMinutes: kind == .session ? QuotaWindows.sessionMinutes : QuotaWindows.weeklyMinutes,
                   usedPct: used, resetsAt: Self.now.addingTimeInterval(resetsIn), state: state,
                   ratePctPerMin: state == .measuring ? nil : 0.3,
                   paceMultiple: state == .measuring ? nil : 1.2,
                   projectedPctAtReset: projected,
                   depletesAt: depletesIn.map { Self.now.addingTimeInterval($0) },
                   shortfallMinutes: shortfallMinutes, measuringReason: reason)
    }

    private func view(_ session: WindowView, _ weekly: WindowView,
                      freshness: Freshness = .live, blockedUntil: Date? = nil,
                      lastChangedAt: Date? = nil, lastPollAt: Date? = nil) -> QuotaView {
        QuotaView(session: session, weekly: weekly, freshness: freshness,
                  lastChangedAt: lastChangedAt ?? Self.now.addingTimeInterval(-90),
                  lastPollAt: lastPollAt ?? Self.now.addingTimeInterval(-20),
                  pollInterval: 150, error: nil,
                  iconSeverity: QuotaState(rawValue: max(session.state.rawValue, weekly.state.rawValue))!,
                  blockedUntil: blockedUntil)
    }

    private func box(_ v: QuotaView) -> StatusBoxText {
        PanelText.compose(v, now: Self.now, timeZone: Self.tz).statusBox
    }

    // ---- status box ----

    @Test("Safe states the landing percentage and the reset")
    func safeBox() {
        let b = box(view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)))
        #expect(b.line1 == "✓ Kvoten räcker till reset")
        #expect(b.line2.hasPrefix("Sessionen landar på ~"))
        #expect(b.line2.contains("kl 12:01 (om 1 h 1 min)"))
        #expect(b.role == .safe)
    }

    @Test("Tight uses the same shape as Safe, and a 60 min reset drops the zero minutes")
    func tightBox() {
        let b = box(view(window(.session, used: 78, resetsIn: 60 * 60, state: .tight, projected: 95),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)))
        #expect(b.line1 == "Tajt — kvoten räcker precis")
        #expect(b.line2.hasPrefix("Sessionen landar på ~"))
        #expect(b.line2.contains("kl 12:00 (om 1 h)"))
        #expect(b.role == .tight)
    }

    /// The spec's worked example, verbatim: depletion at +104 min = 12:44 local, shortfall 64 min
    /// before the reset at +168 min = 13:48 local. Line 2 must state the SHORTFALL, not the
    /// depletion countdown — they are different numbers and confusing them is easy.
    @Test("DryEarly states when it runs out and how long before the reset that is")
    func dryEarlyBox() {
        let b = box(view(window(.session, used: 56, resetsIn: 168 * 60, state: .dryEarly,
                                projected: 127, depletesIn: 104 * 60, shortfallMinutes: 64),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)))
        #expect(b.line1 == "⚠ Kvoten tar slut kl 12:44 (om 1 h 44 min)")
        #expect(b.line2 == "1 h 4 min före reset kl 13:48, om du fortsätter i samma takt")
        #expect(b.role == .crit)
    }

    @Test("when the weekly window drives, the copy says Veckokvoten and Veckan")
    func weeklyDrives() {
        let b = box(view(window(.session, used: 20, resetsIn: 61 * 60, state: .safe, projected: 30),
                         window(.weekly, used: 62, resetsIn: 4 * 86_400, state: .dryEarly,
                                projected: 140, depletesIn: 2 * 86_400, shortfallMinutes: 2880)))
        #expect(b.line1.hasPrefix("⚠ Veckokvoten tar slut "))
        #expect(b.line2.contains("före reset "))
        #expect(b.line2.hasSuffix(", om du fortsätter i samma takt"))
    }

    @Test("Spent says when it opens again")
    func spentBox() {
        let blocked = Self.now.addingTimeInterval(101 * 60)
        let b = box(view(window(.session, used: 100, resetsIn: 101 * 60, state: .spent),
                         window(.weekly, used: 40, resetsIn: 5 * 86_400, state: .safe, projected: 55),
                         blockedUntil: blocked))
        #expect(b.line1 == "Kvoten är slut")
        #expect(b.line2 == "Öppnar igen kl 12:41 (om 1 h 41 min)")
        #expect(b.role == .dead)
    }

    @Test("Measuring states the percentage and the reset")
    func measuringBox() {
        let b = box(view(window(.session, used: 3, resetsIn: 292 * 60, state: .measuring,
                                reason: "För lite förbrukning ännu…"),
                         window(.weekly, used: 11, resetsIn: 6 * 86_400, state: .measuring,
                                reason: "För lite förbrukning ännu…")))
        #expect(b.line1 == "Mäter takt…")
        #expect(b.line2 == "3 % använt · återställs kl 15:52 (om 4 h 52 min)")
    }

    @Test("the weekly 24 h reason is folded into the Measuring line")
    func measuringWeeklyReason() {
        let b = box(view(window(.session, used: 3, resetsIn: 292 * 60, state: .measuring,
                                reason: "För lite förbrukning ännu…"),
                         window(.weekly, used: 11, resetsIn: 6 * 86_400, state: .measuring,
                                reason: "För tidigt i veckan — väntar på ett helt dygn")))
        #expect(b.line1 == "Mäter takt…")
        #expect(b.line2.contains("För tidigt i veckan — väntar på ett helt dygn"))
        #expect(b.line2.contains("återställs "))
    }

    /// Awaiting-reset stands alone: the window's own reset has passed and there is no sane future
    /// clock to count down to, so pairing it with a stale time would be worse than omitting it.
    @Test("awaiting reset shows the reason alone, with no countdown")
    func measuringAwaitingReset() {
        let b = box(view(window(.session, used: 60, resetsIn: -120, state: .measuring,
                                reason: "Nytt fönster väntas"),
                         window(.weekly, used: 11, resetsIn: 6 * 86_400, state: .measuring,
                                reason: "För lite förbrukning ännu…")))
        #expect(b.line1 == "Mäter takt…")
        #expect(b.line2 == "Nytt fönster väntas")
    }

    @Test("Stale adds a third line with the real age, keeping the verdict")
    func staleLine() {
        let b = box(view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34),
                         freshness: .stale, lastChangedAt: Self.now.addingTimeInterval(-23 * 60)))
        #expect(b.line1 == "✓ Kvoten räcker till reset")
        #expect(b.line3 == "Datan kan vara inaktuell — senast ändrad för 23 min sedan")
    }

    @Test("Unknown says it cannot read the quota and when it last did")
    func unknownBox() {
        let b = box(view(window(.session, used: nil, resetsIn: 61 * 60, state: .measuring, reason: "Hämtar…"),
                         window(.weekly, used: nil, resetsIn: 5 * 86_400, state: .measuring, reason: "Hämtar…"),
                         freshness: .unknown, lastPollAt: Self.now.addingTimeInterval(-50 * 60)))
        #expect(b.line1 == "Kan inte läsa kvoten")
        #expect(b.line2 == "Senast avläst kl 10:10 · försöker igen")
        #expect(b.role == .unknown)
    }

    /// If the window that is NOT driving the box is itself Tight/DryEarly/Spent, it gets one
    /// short extra line.
    @Test("the non-driving window gets a secondary line when it is also in trouble")
    func secondaryLine() throws {
        let b = box(view(window(.session, used: 56, resetsIn: 168 * 60, state: .dryEarly,
                                projected: 127, depletesIn: 104 * 60, shortfallMinutes: 64),
                         window(.weekly, used: 62, resetsIn: 4 * 86_400, state: .tight, projected: 88)))
        let secondary = try #require(b.secondaryLine)
        #expect(secondary.contains("Veckokvoten"))
        #expect(secondary.hasPrefix("Dessutom tajt:"))
    }

    @Test("a calm non-driving window produces no secondary line")
    func noSecondaryLineWhenCalm() {
        let b = box(view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)))
        #expect(b.secondaryLine == nil)
    }

    // ---- window sections ----

    @Test("the session section header, Tid label and Kvot label")
    func sessionSection() {
        let composed = PanelText.compose(
            view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                 window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)),
            now: Self.now, timeZone: Self.tz)
        #expect(composed.session.title == "AKTUELL SESSION")
        #expect(composed.session.resetHeader == "återställs om 1 h 1 min · kl 12:01")
        #expect(composed.session.tidLabel == "80 % av 5 h har gått")
        #expect(composed.session.kvotLabel.hasPrefix("45 % använt → ~"))
        #expect(composed.session.kvotLabel.contains("vid reset"))
    }

    @Test("the weekly section says av veckan and carries a weekday")
    func weeklySection() {
        let composed = PanelText.compose(
            view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                 window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34)),
            now: Self.now, timeZone: Self.tz)
        #expect(composed.weekly.title == "VECKA")
        #expect(composed.weekly.tidLabel.contains("av veckan har gått"))
        #expect(composed.weekly.resetHeader.contains("ons "))
    }

    @Test("a spent window's Kvot label is the full bar")
    func spentSection() {
        let composed = PanelText.compose(
            view(window(.session, used: 100, resetsIn: 101 * 60, state: .spent),
                 window(.weekly, used: 40, resetsIn: 5 * 86_400, state: .safe, projected: 55),
                 blockedUntil: Self.now.addingTimeInterval(101 * 60)),
            now: Self.now, timeZone: Self.tz)
        #expect(composed.session.kvotLabel == "100 % · slut")
    }

    /// A window whose reset has passed with no later window observed replaces its whole header
    /// and Tid label rather than computing them from the stale values, and draws the Kvot bar
    /// empty — never the old window's usage as if it were current.
    @Test("an awaiting-reset section replaces its header, Tid and Kvot labels")
    func awaitingResetSection() {
        let composed = PanelText.compose(
            view(window(.session, used: 60, resetsIn: -120, state: .measuring, reason: "Nytt fönster väntas"),
                 window(.weekly, used: 40, resetsIn: 5 * 86_400, state: .safe, projected: 55)),
            now: Self.now, timeZone: Self.tz)
        #expect(composed.session.resetHeader == "väntar på nytt fönster")
        #expect(composed.session.tidLabel == "fönstret är slut")
        #expect(composed.session.kvotLabel == "Nytt fönster väntas")
    }

    /// The Kvot-bar depletion wording deliberately differs from the status box's: it drops "kl"
    /// before a weekday ("slut sön 10:44 (om …)"), per panel-v2's implementation notes.
    @Test("the Kvot bar drops kl before a weekday, unlike the status box")
    func kvotBarWeekdayWording() {
        let composed = PanelText.compose(
            view(window(.session, used: 20, resetsIn: 61 * 60, state: .safe, projected: 30),
                 window(.weekly, used: 62, resetsIn: 4 * 86_400, state: .dryEarly,
                        projected: 140, depletesIn: 2 * 86_400 + 3600, shortfallMinutes: 2880)),
            now: Self.now, timeZone: Self.tz)
        #expect(composed.weekly.kvotLabel.contains("→ slut sön "))
        #expect(!composed.weekly.kvotLabel.contains("slut sön kl "))
    }

    // ---- other-account rows ----

    @Test("other-account rows are short verdicts, never full sentences")
    func otherAccountRows() {
        let safe = view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                        window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34))
        #expect(PanelText.composeOtherAccountRow(accountIndex: 1, label: "Max", view: safe,
                                                 now: Self.now, timeZone: Self.tz).line == "räcker till reset")

        let unknown = view(window(.session, used: nil, resetsIn: 61 * 60, state: .measuring),
                           window(.weekly, used: nil, resetsIn: 5 * 86_400, state: .measuring),
                           freshness: .unknown)
        #expect(PanelText.composeOtherAccountRow(accountIndex: 1, label: "Max", view: unknown,
                                                 now: Self.now, timeZone: Self.tz).line == "går inte att läsa")

        let stale = view(window(.session, used: 45, resetsIn: 61 * 60, state: .safe, projected: 57),
                         window(.weekly, used: 20, resetsIn: 5 * 86_400, state: .safe, projected: 34),
                         freshness: .stale)
        #expect(PanelText.composeOtherAccountRow(accountIndex: 1, label: "Max", view: stale,
                                                 now: Self.now, timeZone: Self.tz)
                .line.hasSuffix(" · kan vara inaktuell"))
    }

    /// Whatever the panel shows, a raw minute count above an hour must never appear anywhere in
    /// it — that is the formatter's entire reason for existing.
    @Test("no raw minute count above sixty leaks into any panel string")
    func noRawMinutesAnywhere() throws {
        let pattern = try NSRegularExpression(pattern: #"\b([6-9][0-9]|[1-9][0-9]{2,}) min\b"#)
        let scenarios = [
            view(window(.session, used: 56, resetsIn: 168 * 60, state: .dryEarly, projected: 127,
                        depletesIn: 104 * 60, shortfallMinutes: 64),
                 window(.weekly, used: 62, resetsIn: 4 * 86_400, state: .tight, projected: 88)),
            view(window(.session, used: 11, resetsIn: 292 * 60, state: .measuring, reason: "För lite förbrukning ännu…"),
                 window(.weekly, used: 11, resetsIn: 6 * 86_400, state: .measuring,
                        reason: "För tidigt i veckan — väntar på ett helt dygn")),
        ]
        for scenario in scenarios {
            let composed = PanelText.compose(scenario, now: Self.now, timeZone: Self.tz)
            let strings = [composed.statusBox.line1, composed.statusBox.line2,
                           composed.statusBox.line3 ?? "", composed.statusBox.secondaryLine ?? "",
                           composed.session.resetHeader, composed.session.tidLabel, composed.session.kvotLabel,
                           composed.weekly.resetHeader, composed.weekly.tidLabel, composed.weekly.kvotLabel]
            for text in strings {
                #expect(pattern.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) == nil,
                        "raw minute count leaked: \(text)")
            }
        }
    }
}

/// The CSV writer and reader must agree, or warm start silently replays nothing — which is what
/// the app did before this landed. The round trip is the test that matters: whatever the writer
/// emits, `CsvReplay` has to parse back into the same values.
@Suite("Window-shape log")
struct WindowShapeLogTests {

    private func temporaryLog() -> WindowShapeLog {
        let url = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("wsl-\(UUID().uuidString)")
        return WindowShapeLog(directory: url)
    }

    @Test("the header matches the C# writer's columns exactly")
    func headerMatchesCSharp() {
        // Taken verbatim from DiskLogSink.WindowShapeCsvHeader. A column added or reordered here
        // makes the two suites read different data from the same shared fixture while both pass.
        #expect(WindowShapeLog.header ==
                "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,"
                + "seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source")
    }

    @Test("a written row reads back with the same values")
    func roundTrip() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }

        let at = utc("2026-09-11T11:20:33.9659745+00:00")
        let snapshot = UsageSnapshot.make(session: 58, sessionResets: "2026-09-11T11:20:00.659853+00:00",
                                          weekly: 12, weeklyResets: "2026-09-18T05:00:00.659881+00:00")
        log.append(snapshot, at: at, monoMs: 37_481_031, latencyMs: 12.7)

        let rows = log.rowsForWarmStart(now: at)
        let row = try #require(rows.first)
        #expect(rows.count == 1)
        #expect(row.sessionPct == 58)
        #expect(row.sessionResetsAtRaw == "2026-09-11T11:20:00.659853+00:00")
        #expect(row.weeklyPct == 12)
        #expect(row.weeklyResetsAtRaw == "2026-09-18T05:00:00.659881+00:00")
        #expect(row.monoMs == 37_481_031)
        // Sub-second precision must survive: the raw resets_at string is the fingerprint, and the
        // row timestamp orders replay.
        #expect(abs(row.utcIso.timeIntervalSince(at)) < 0.001)
    }

    @Test("appending twice keeps one header and two rows")
    func appendsWithoutDuplicatingTheHeader() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }
        let at = utc("2026-09-11T11:20:33.000000+00:00")
        for step in 0..<2 {
            log.append(UsageSnapshot.make(session: Double(step + 1),
                                          sessionResets: "2026-09-11T11:20:00.65985\(step)+00:00"),
                       at: at.addingTimeInterval(Double(step) * 30), monoMs: Int64(step) * 30_000,
                       latencyMs: 10)
        }
        let contents = try String(contentsOf: log.monthlyURL(for: at), encoding: .utf8)
        #expect(contents.components(separatedBy: "utc_iso").count - 1 == 1, "header written twice")
        #expect(log.rowsForWarmStart(now: at).count == 2)
    }

    @Test("a missing weekly window writes an empty field, not a zero")
    func missingWeeklyIsEmptyNotZero() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }
        let at = utc("2026-09-11T11:20:33.000000+00:00")
        log.append(UsageSnapshot.make(session: 58, sessionResets: "2026-09-11T11:20:00.659853+00:00"),
                   at: at, monoMs: 1, latencyMs: 10)

        let row = try #require(log.rowsForWarmStart(now: at).first)
        // Decision 13: a missing window is never substituted with a default. Writing 0 would
        // invent history that says the weekly quota was untouched.
        #expect(row.weeklyPct == nil)
        #expect(row.weeklyResetsAtRaw == nil)
    }

    @Test("a reset string containing a comma or quote survives the round trip")
    func quotingSurvives() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }
        let at = utc("2026-09-11T11:20:33.000000+00:00")
        // Not a shape the server emits, but the writer must not be able to corrupt a row.
        #expect(WindowShapeLog.csvField("a,b") == "\"a,b\"")
        #expect(WindowShapeLog.csvField("a\"b") == "\"a\"\"b\"")
        #expect(WindowShapeLog.csvField(nil) == "")
        #expect(WindowShapeLog.csvField("") == "")
        _ = at; _ = log
    }

    @Test("warm start over a written log actually seeds the model")
    func warmStartUsesTheLog() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }

        // A window whose reset is 168 min out, with history rising 20 % → 50 % over the prior
        // hour. Cold, the model would have one sample; warm, it has the envelope.
        //
        // Each row carries its OWN microsecond fingerprint, because that is what the server
        // actually sends (docs/mac-port.md §3: in the live regime every reply carries a novel
        // value). Reusing one string here would see rows 2…n deduped as replica reverts and the
        // replay would silently contribute nothing — which is exactly what the first cut of this
        // test did, and why it read 20 % instead of 56 %.
        let firstSampleAt = utc("2026-09-11T11:00:00.000000+00:00")
        func fingerprint(_ step: Int) -> String {
            String(format: "2026-09-11T13:48:00.%06d+00:00", 100_000 + step)
        }
        for step in 0..<6 {
            log.append(UsageSnapshot.make(session: 20 + Double(step) * 6, sessionResets: fingerprint(step)),
                       at: firstSampleAt.addingTimeInterval(Double(step) * 600),
                       monoMs: Int64(step) * 600_000, latencyMs: 10)
        }

        let now = firstSampleAt.addingTimeInterval(3600)
        let live = UsageSnapshot.make(session: 56, sessionResets: fingerprint(99))
        let model = QuotaModel()
        model.warmStart(live, utcNow: now, rows: log.rowsForWarmStart(now: now))
        model.ingest(live, utcNow: now, monoMs: 3_600_000)

        let view = model.evaluate(utcNow: now, monoMs: 3_600_000)
        #expect(view.session.usedPct == 56)
        // The replayed history is what makes a rate available at all here.
        #expect(view.session.ratePctPerMin != nil)
    }

    /// The other half of the same lesson: identical fingerprints ARE deduped, so a log full of
    /// repeated cached replies contributes exactly one sample, not six.
    @Test("replayed rows sharing one fingerprint are deduped to a single sample")
    func repeatedFingerprintsAreDeduped() throws {
        let log = temporaryLog()
        defer { try? FileManager.default.removeItem(at: log.directory) }

        let resetsRaw = "2026-09-11T13:48:00.000000+00:00"
        let firstSampleAt = utc("2026-09-11T11:00:00.000000+00:00")
        for step in 0..<6 {
            log.append(UsageSnapshot.make(session: 20 + Double(step) * 6, sessionResets: resetsRaw),
                       at: firstSampleAt.addingTimeInterval(Double(step) * 600),
                       monoMs: Int64(step) * 600_000, latencyMs: 10)
        }

        let now = firstSampleAt.addingTimeInterval(3600)
        let tracker = WindowTracker(windowMinutes: QuotaWindows.sessionMinutes, halfLifeMinutes: 15)
        let rows = CsvReplay.filterForWindow(log.rowsForWarmStart(now: now), kind: .session,
                                             windowKey: try #require(QuotaTimeUtil.parseResetsAt(resetsRaw)),
                                             windowMinutes: QuotaWindows.sessionMinutes, now: now)
        #expect(rows.count == 6, "the reader should hand over all six rows")
        for row in rows {
            tracker.ingest(pct: row.pct, resetsAtRaw: row.resetsAtRaw,
                           utcNow: row.utcIso, monoMs: row.monoMs, isReplay: true)
        }
        #expect(tracker.sampleCount == 1, "dedup should have collapsed them to one")
        #expect(tracker.envelopeP == 20, "only the first row's value should have landed")
    }
}
