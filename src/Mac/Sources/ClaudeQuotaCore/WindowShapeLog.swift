import Foundation

/// Appends one row per successful poll to `window-shape-YYYY-MM.csv`, and reads it back for
/// `QuotaModel.warmStart`. The macOS counterpart of the CSV half of
/// `src/Windows/Data/DiskLogSink.cs`.
///
/// This exists so a restart does not begin cold. The model's estimator needs history to have a
/// rate at all; without it, every launch spends the first minutes refusing to give a verdict,
/// and after a rollover it starts from nothing again.
///
/// **Columns are the C# writer's, byte for byte**, because `CsvReplay` — and the shared fixtures
/// in `tests/fixtures/` — are the seam that keeps the two implementations honest. A column added
/// or reordered here would make the Swift suite and the C# suite read different data from the
/// same file while both still passing.
///
/// Writes are best-effort and never throw: a missing directory, a full disk or a locked file
/// costs a warm start, which is a slower start, not a wrong one.
public struct WindowShapeLog: Sendable {
    public static let header = "utc_iso,mono_ms,five_hour_pct,five_hour_resets_at_raw,"
        + "seven_day_pct,seven_day_resets_at_raw,poll_latency_ms,source"

    /// Six months, matching the Windows retention.
    public static let retentionMonths = 6

    public let directory: URL

    public init(directory: URL) {
        self.directory = directory
    }

    /// `logs/<stateKey>/` — per identity, never shared. `AccountIdentity.stateKey` is the
    /// account+organisation pair: one person's Team seat and personal Max seat must not pour
    /// their contradictory percentages into one file (docs/multi-account.md, "Identity guard").
    public static func forAccount(stateKey: String?) -> WindowShapeLog {
        var directory = ChildProcessSpec.applicationSupportDirectory().appendingPathComponent("logs")
        if let stateKey, !stateKey.isEmpty {
            directory = directory.appendingPathComponent(stateKey)
        }
        return WindowShapeLog(directory: directory)
    }

    public static func monthlyFileName(for date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM"
        return "window-shape-\(formatter.string(from: date)).csv"
    }

    public func monthlyURL(for date: Date) -> URL {
        directory.appendingPathComponent(Self.monthlyFileName(for: date))
    }

    // MARK: - Writing

    public func append(_ snapshot: UsageSnapshot, at: Date, monoMs: Int64, latencyMs: Double) {
        guard let sessionPct = snapshot.sessionUtilization else { return }
        let row = [
            Self.isoTimestamp(at),
            String(monoMs),
            Self.number(sessionPct),
            Self.csvField(snapshot.sessionResetsAt),
            snapshot.weeklyUtilization.map(Self.number) ?? "",
            Self.csvField(snapshot.weeklyResetsAt),
            Self.number(latencyMs),
            "get_usage",
        ].joined(separator: ",")

        let url = monthlyURL(for: at)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        if !FileManager.default.fileExists(atPath: url.path) {
            try? Data((Self.header + "\n").utf8).write(to: url)
        }
        guard let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        _ = try? handle.seekToEnd()
        try? handle.write(contentsOf: Data((row + "\n").utf8))
    }

    // MARK: - Reading

    /// The current month's file plus the previous month's, because a window — up to 7 days for
    /// the weekly quota — can straddle a month boundary and warm start must still see the rows
    /// from just before it. `CsvReplay.filterForWindow` re-sorts and re-validates everything, so
    /// the concatenation order does not matter.
    public func rowsForWarmStart(now: Date) -> [CsvReplay.RawRow] {
        var rows = CsvReplay.readRows(path: monthlyURL(for: now).path)
        let previousMonth = Calendar(identifier: .gregorian).date(byAdding: .month, value: -1, to: now)
        if let previousMonth {
            let previousURL = monthlyURL(for: previousMonth)
            if previousURL != monthlyURL(for: now) {
                rows.append(contentsOf: CsvReplay.readRows(path: previousURL.path))
            }
        }
        return rows
    }

    /// Deletes month files older than the retention window. Best-effort.
    public func prune(now: Date = Date()) {
        guard let cutoff = Calendar(identifier: .gregorian)
            .date(byAdding: .month, value: -Self.retentionMonths, to: now) else { return }
        guard let entries = try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        for entry in entries where entry.lastPathComponent.hasPrefix("window-shape-") {
            guard let modified = try? entry.resourceValues(forKeys: [.contentModificationDateKey])
                .contentModificationDate, modified < cutoff else { continue }
            try? FileManager.default.removeItem(at: entry)
        }
    }

    // MARK: - Formatting

    /// Round-trip ISO 8601 with 7 fractional digits, matching .NET's `"O"` format — which is
    /// what produced the fixtures both suites replay.
    static func isoTimestamp(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        let whole = formatter.string(from: date)
        let fractional = date.timeIntervalSince1970 - date.timeIntervalSince1970.rounded(.down)
        let digits = Int((fractional * 10_000_000).rounded())
        return whole + String(format: ".%07d", min(digits, 9_999_999)) + "+00:00"
    }

    /// Invariant-culture number formatting: a decimal comma would make the row unparseable by
    /// the very reader that has to read it back.
    static func number(_ value: Double) -> String {
        if value == value.rounded() && abs(value) < 1e15 {
            return String(Int64(value))
        }
        return String(format: "%.4f", value)
    }

    static func csvField(_ raw: String?) -> String {
        guard let raw, !raw.isEmpty else { return "" }
        if raw.contains(where: { $0 == "," || $0 == "\"" || $0 == "\n" || $0 == "\r" }) {
            return "\"" + raw.replacingOccurrences(of: "\"", with: "\"\"") + "\""
        }
        return raw
    }
}
