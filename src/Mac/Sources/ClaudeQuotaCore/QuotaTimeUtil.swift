import Foundation

/// Small time helpers shared by the ingest pipeline (`WindowTracker`) and the CSV warm-start
/// reader (`CsvReplay`), so both parse and key `resets_at` identically. Ported from
/// `src/Windows/Model/QuotaTimeUtil.cs`; see docs/forecast-and-states.md, "Ingest".
public enum QuotaTimeUtil {

    /// Parses a raw `resets_at` wire string, e.g. `2026-09-11T11:20:00.524090+00:00`.
    ///
    /// Deliberately hand-rolled rather than `ISO8601DateFormatter`: the protocol writes **6**
    /// fractional digits, the CSV fixtures carry **7**, and `ISO8601DateFormatter` is strict
    /// about which it will accept. Tolerating any digit count — including none — is the whole
    /// requirement, and the C# side gets it from `DateTimeStyles.RoundtripKind`.
    public static func parseResetsAt(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        return Self.parseISO8601(raw)
    }

    /// `yyyy-MM-ddTHH:mm:ss[.fraction][Z|±HH:MM|±HHMM]`. Returns nil on anything else.
    static func parseISO8601(_ text: String) -> Date? {
        let scalars = Array(text.utf8)
        func digits(_ start: Int, _ count: Int) -> Int? {
            guard start + count <= scalars.count else { return nil }
            var value = 0
            for index in start..<(start + count) {
                let byte = scalars[index]
                guard byte >= 48, byte <= 57 else { return nil }
                value = value * 10 + Int(byte - 48)
            }
            return value
        }
        guard scalars.count >= 19,
              let year = digits(0, 4), scalars[4] == UInt8(ascii: "-"),
              let month = digits(5, 2), scalars[7] == UInt8(ascii: "-"),
              let day = digits(8, 2),
              scalars[10] == UInt8(ascii: "T") || scalars[10] == UInt8(ascii: " "),
              let hour = digits(11, 2), scalars[13] == UInt8(ascii: ":"),
              let minute = digits(14, 2), scalars[16] == UInt8(ascii: ":"),
              let second = digits(17, 2) else { return nil }

        var index = 19
        var fraction = 0.0
        if index < scalars.count, scalars[index] == UInt8(ascii: ".") {
            index += 1
            var scale = 0.1
            var sawDigit = false
            while index < scalars.count, scalars[index] >= 48, scalars[index] <= 57 {
                fraction += Double(scalars[index] - 48) * scale
                scale /= 10
                index += 1
                sawDigit = true
            }
            guard sawDigit else { return nil }
        }

        // Offset. Absent means UTC (C#'s AssumeUniversal).
        var offsetSeconds = 0
        if index < scalars.count {
            let marker = scalars[index]
            if marker == UInt8(ascii: "Z") || marker == UInt8(ascii: "z") {
                index += 1
            } else if marker == UInt8(ascii: "+") || marker == UInt8(ascii: "-") {
                let sign = marker == UInt8(ascii: "+") ? 1 : -1
                index += 1
                guard let offsetHours = digits(index, 2) else { return nil }
                index += 2
                if index < scalars.count, scalars[index] == UInt8(ascii: ":") { index += 1 }
                let offsetMinutes = digits(index, 2) ?? 0
                if digits(index, 2) != nil { index += 2 }
                offsetSeconds = sign * (offsetHours * 3600 + offsetMinutes * 60)
            }
        }
        guard index == scalars.count else { return nil }

        var components = DateComponents()
        components.year = year; components.month = month; components.day = day
        components.hour = hour; components.minute = minute; components.second = second
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        guard let base = calendar.date(from: components) else { return nil }
        return base.addingTimeInterval(fraction - Double(offsetSeconds))
    }

    /// Window key: `resets_at` truncated to the second. The live tracker no longer uses exact
    /// truncated-second equality for rollover detection (review decision 1) — this remains for
    /// tests and any caller that still wants the coarse key.
    public static func truncateToSecond(_ value: Date) -> Date {
        Date(timeIntervalSince1970: (value.timeIntervalSince1970).rounded(.down))
    }

    /// Decision 9: utilization must be finite and in [0, 100] at both the CSV and tracker
    /// boundaries.
    public static func isValidPct(_ pct: Double) -> Bool {
        pct.isFinite && pct >= 0 && pct <= 100
    }

    /// Guards a derived-minutes offset before it reaches date arithmetic (decision 9:
    /// "derived timestamps are guarded"). Returns nil for NaN/infinite/out-of-range.
    public static func safeAddMinutes(_ now: Date, _ minutes: Double) -> Date? {
        guard minutes.isFinite else { return nil }
        let seconds = minutes * 60
        let result = now.timeIntervalSinceReferenceDate + seconds
        guard result.isFinite, abs(result) < 1e12 else { return nil }
        return Date(timeIntervalSinceReferenceDate: result)
    }

    /// Decision 10 (round 2): `resets_at` is rejected outside this window around "now" —
    /// generous enough for any real session (5 h) or weekly (7 d) deadline, but never large
    /// enough to let a garbled or adversarial value (a year-9999 timestamp still parses)
    /// reach downstream date arithmetic that could overflow.
    public static func isPlausibleResetsAt(_ resetsAt: Date, now: Date) -> Bool {
        resetsAt >= now.addingTimeInterval(-86_400) && resetsAt <= now.addingTimeInterval(8 * 86_400)
    }

    /// Guards a date + interval addition (decision 10: "guard all derived deadline arithmetic").
    public static func safeAdd(_ date: Date, _ interval: TimeInterval) -> Date? {
        guard interval.isFinite else { return nil }
        let result = date.timeIntervalSinceReferenceDate + interval
        guard result.isFinite, abs(result) < 1e12 else { return nil }
        return Date(timeIntervalSinceReferenceDate: result)
    }
}
