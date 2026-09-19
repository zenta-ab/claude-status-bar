import Foundation

@testable import ClaudeQuotaCore

/// Locates `tests/fixtures/` **in the repository**, not a copy inside the test bundle.
///
/// This is deliberate and load-bearing. docs/mac-port.md: "What stops the two implementations
/// drifting is not shared code — it is shared evidence … the Swift test suite replays the **same
/// files** and must reach the same verdicts as the C# suite." Copying them as SwiftPM resources
/// would create a second copy that can silently diverge from the one the C# suite reads, which is
/// exactly the failure this arrangement exists to prevent.
enum Fixtures {
    /// `<repo>/tests/fixtures`, derived from this file's own location at compile time.
    static var directory: URL {
        // .../src/Mac/Tests/ClaudeQuotaCoreTests/Fixtures.swift → up 4 → <repo>
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()   // ClaudeQuotaCoreTests
            .deletingLastPathComponent()   // Tests
            .deletingLastPathComponent()   // Mac
            .deletingLastPathComponent()   // src
            .deletingLastPathComponent()   // <repo>
            .appendingPathComponent("tests/fixtures")
    }

    static var windowShape: String { directory.appendingPathComponent("window-shape-2026-09-11.csv").path }
    static var rollover: String { directory.appendingPathComponent("rollover-2026-09-11.csv").path }

    static func rows(_ path: String) -> [CsvReplay.RawRow] { CsvReplay.readRows(path: path) }
}

extension UsageSnapshot {
    /// Builds a snapshot from a replay row, the way the C# tests' `ToSnapshot` does.
    static func fromRow(_ row: CsvReplay.RawRow) -> UsageSnapshot {
        UsageSnapshot(
            sessionUtilization: row.sessionPct,
            sessionResetsAt: row.sessionResetsAtRaw,
            weeklyUtilization: row.weeklyPct,
            weeklyResetsAt: row.weeklyResetsAtRaw,
            subscriptionType: nil,
            sessionIsActive: row.sessionResetsAtRaw != nil,
            weeklyIsActive: row.weeklyResetsAtRaw != nil,
            observedAt: row.utcIso,
            source: .exactKeys)
    }

    /// A synthetic snapshot for the model tests.
    static func make(session: Double?, sessionResets: String?,
                     weekly: Double? = nil, weeklyResets: String? = nil,
                     observedAt: Date = Date()) -> UsageSnapshot {
        UsageSnapshot(
            sessionUtilization: session, sessionResetsAt: sessionResets,
            weeklyUtilization: weekly, weeklyResetsAt: weeklyResets,
            subscriptionType: nil,
            sessionIsActive: sessionResets != nil, weeklyIsActive: weeklyResets != nil,
            observedAt: observedAt, source: .exactKeys)
    }
}

/// `2026-09-11T11:20:33.9659745+00:00` → Date. Fails loudly rather than defaulting.
func utc(_ text: String) -> Date {
    guard let date = QuotaTimeUtil.parseResetsAt(text) else {
        fatalError("test fixture timestamp did not parse: \(text)")
    }
    return date
}

/// Formats a date back to the wire's 6-fraction-digit form, for synthetic fingerprints.
func wireFormat(_ date: Date) -> String {
    let formatter = DateFormatter()
    formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSSSSS"
    formatter.timeZone = TimeZone(secondsFromGMT: 0)
    formatter.locale = Locale(identifier: "en_US_POSIX")
    return formatter.string(from: date) + "+00:00"
}

extension Date {
    var utcHour: Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar.component(.hour, from: self)
    }
    var utcYMD: (year: Int, month: Int, day: Int) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let parts = calendar.dateComponents([.year, .month, .day], from: self)
        return (parts.year!, parts.month!, parts.day!)
    }
}
