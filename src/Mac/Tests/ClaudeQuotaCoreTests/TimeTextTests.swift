import Foundation
import Testing

@testable import ClaudeQuotaCore

/// Ported from `tests/ClaudeStatusBar.Tests/TimeTextTests.cs`, asserting the same exact strings.
/// A fixed +01:00 offset stands in for local (Swedish) time — deterministic regardless of the
/// machine running the tests, and DST-agnostic since panel-v2 does not need it.
@Suite("Time text")
struct TimeTextTests {
    static let tz = TimeZone(secondsFromGMT: 3600)!

    // ---- Duration band boundaries (docs/panel-v2.md, "Time text") ----

    @Test("every duration band boundary", arguments: [
        (0, "0 s"), (59, "59 s"), (90, "1 min 30 s"), (119, "1 min 59 s"), (120, "2 min"),
        (59 * 60, "59 min"), (60 * 60, "1 h"), (23 * 3600 + 59 * 60, "23 h 59 min"),
        (24 * 3600, "1 d 0 h"), (6 * 86_400 + 18 * 3600, "6 d 18 h"),
    ])
    func durationBands(_ seconds: Int, _ expected: String) {
        #expect(TimeText.duration(TimeInterval(seconds)) == expected)
    }

    @Test("a negative span clamps to zero")
    func negativeClamps() {
        #expect(TimeText.duration(-30) == "0 s")
    }

    /// The whole point of panel-v2's formatter: "6975 min" must never happen.
    @Test("never emits a raw minute count at or above sixty")
    func neverRawMinutesAboveSixty() throws {
        let pattern = try NSRegularExpression(pattern: #"\b([6-9][0-9]|[1-9][0-9]{2,}) min\b"#)
        for minutes in [60, 90, 200, 1440, 10_080] {
            let text = TimeText.duration(TimeInterval(minutes * 60))
            let range = NSRange(text.startIndex..., in: text)
            #expect(pattern.firstMatch(in: text, range: range) == nil, "leaked a raw minute count: \(text)")
        }
    }

    // ---- Points in time, including crossing midnight ----

    @Test("the same local day is clock only")
    func sameDay() {
        let now = utc("2026-09-11T10:00:00+00:00")    // 11:00 local
        let t = utc("2026-09-11T11:36:00+00:00")      // 12:36 local
        #expect(TimeText.pointInTime(t, now: now, timeZone: Self.tz) == "kl 12:36")
    }

    @Test("the next local day says i morgon")
    func nextDay() {
        let now = utc("2026-09-11T10:00:00+00:00")
        let t = utc("2026-09-12T09:44:00+00:00")      // tomorrow 10:44 local
        #expect(TimeText.pointInTime(t, now: now, timeZone: Self.tz) == "i morgon kl 10:44")
    }

    @Test("further out uses the weekday abbreviation")
    func furtherOut() {
        // 2026-09-11 is a Friday; +3 local days lands on Monday.
        let now = utc("2026-09-11T10:00:00+00:00")
        let t = utc("2026-09-14T09:44:00+00:00")
        #expect(TimeText.pointInTime(t, now: now, timeZone: Self.tz) == "mån kl 10:44")
    }

    /// now = 23:50 local; target = 00:10 local the next calendar day — only 20 minutes away in
    /// wall-clock terms, but a genuine calendar-date crossing.
    @Test("crossing midnight forwards is i morgon, not 20 minutes")
    func crossingMidnightForward() {
        let now = utc("2026-09-11T22:50:00+00:00")    // 23:50 local
        let t = utc("2026-09-11T23:10:00+00:00")      // 00:10 local, next day
        #expect(TimeText.pointInTime(t, now: now, timeZone: Self.tz) == "i morgon kl 00:10")
    }

    /// Guards that dayDiff is calendar-date based, not elapsed-time based: a target 20 minutes
    /// earlier on the previous date is day −1.
    @Test("crossing midnight backwards is not tomorrow")
    func crossingMidnightBackward() {
        let now = utc("2026-09-11T23:10:00+00:00")    // 00:10 local, next day
        let t = utc("2026-09-11T22:50:00+00:00")      // 23:50 local, previous day
        #expect(TimeText.dayDiff(t, now: now, timeZone: Self.tz) == -1)
    }

    // ---- Reset and depletion: both when AND how long until ----

    @Test("a reset states the clock time and the countdown")
    func resetStatesBoth() {
        let now = utc("2026-09-11T10:00:00+00:00")    // 11:00 local
        let resetsAt = now.addingTimeInterval(61 * 60)
        #expect(TimeText.reset(resetsAt, now: now, timeZone: Self.tz, forceWeekday: false)
                == "kl 12:01 (om 1 h 1 min)")
    }

    @Test("a weekly reset always carries its weekday")
    func weeklyForcesWeekday() {
        let now = utc("2026-09-11T10:00:00+00:00")    // Friday 11:00 local
        let resetsAt = now.addingTimeInterval(37 * 60)
        #expect(TimeText.reset(resetsAt, now: now, timeZone: Self.tz, forceWeekday: true)
                == "fre kl 11:37 (om 37 min)")
    }

    /// A reset at or before now collapses to "nu" — never a stale clock time paired with
    /// "(om 0 s)" or a negative duration.
    @Test("a reset at or before now collapses to nu", arguments: [0.0, -1.0, -3600.0])
    func resetCollapsesToNu(_ offset: Double) {
        let now = utc("2026-09-11T10:00:00+00:00")
        #expect(TimeText.reset(now.addingTimeInterval(offset), now: now, timeZone: Self.tz, forceWeekday: false) == "nu")
        #expect(TimeText.countdownFragment(offset) == "nu")
    }

    @Test("depletion today states the clock time and the countdown")
    func depletionToday() {
        let now = utc("2026-09-11T10:00:00+00:00")    // 11:00 local
        let dep = now.addingTimeInterval(103 * 60)    // 12:43 local
        #expect(TimeText.depletion(dep, now: now, timeZone: Self.tz) == "kl 12:43 (om 1 h 43 min)")
    }

    /// Depletion on a later day uses the weekday, and still carries the countdown.
    @Test("depletion on a later day uses the weekday and still counts down")
    func depletionLaterDay() throws {
        let now = utc("2026-09-11T10:00:00+00:00")
        let dep = now.addingTimeInterval(3 * 86_400)
        let text = TimeText.depletion(dep, now: now, timeZone: Self.tz)
        let pattern = try NSRegularExpression(pattern: #"^(sön|mån|tis|ons|tor|fre|lör) kl \d{2}:\d{2} \(om .+\)$"#)
        #expect(pattern.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)) != nil,
                "unexpected shape: \(text)")
    }

    @Test("depletion at or before now collapses to nu")
    func depletionCollapsesToNu() {
        let now = utc("2026-09-11T10:00:00+00:00")
        #expect(TimeText.depletion(now, now: now, timeZone: Self.tz) == "nu")
        #expect(TimeText.depletion(now.addingTimeInterval(-60), now: now, timeZone: Self.tz) == "nu")
    }

    // ---- Percentage rounding: the cross-implementation trap ----

    /// C#'s `ToString("F0")` rounds half **away from zero**; C's `%.0f` rounds half to **even**.
    /// Left alone, every panel percentage sitting exactly on .5 would read one point apart
    /// between the two implementations — a silent, permanent divergence in the shared copy.
    @Test("percentages round half away from zero, matching .NET", arguments: [
        (0.5, "1"), (1.5, "2"), (2.5, "3"), (56.5, "57"), (0.4, "0"), (0.6, "1"), (99.5, "100"),
    ])
    func percentRoundsHalfAway(_ value: Double, _ expected: String) {
        #expect(TimeText.percent(value) == expected)
    }

    @Test("a non-finite percentage never renders as a number")
    func percentGuardsNonFinite() {
        #expect(TimeText.percent(.nan) == "–")
        #expect(TimeText.percent(.infinity) == "–")
    }

    @Test("weekday abbreviations cover the whole week")
    func weekdayTable() {
        // 2026-09-13 is a Sunday.
        let sunday = utc("2026-09-13T12:00:00+00:00")
        let expected = ["sön", "mån", "tis", "ons", "tor", "fre", "lör"]
        for (offset, want) in expected.enumerated() {
            let day = sunday.addingTimeInterval(Double(offset) * 86_400)
            #expect(TimeText.weekday(day, timeZone: Self.tz) == want)
        }
    }
}
