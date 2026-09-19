import Foundation

/// Reads the window-shape CSV for `QuotaModel`'s warm start (docs/forecast-and-states.md,
/// "Ingest" rule 6). Pure file IO + filtering — it never applies fingerprint dedup, the monotone
/// envelope or the estimator itself; those stay `WindowTracker.ingest`'s job so a replayed row
/// and a live poll go through identical logic. Never throws: a missing or corrupt file just
/// yields no warm-start data, since a cold start is always a valid (if slower) start.
///
/// Ported from `src/Windows/Data/CsvReplay.cs`.
public enum CsvReplay {

    /// One CSV row, columns as the Windows `ClaudeCliChannel.WindowShapeCsvHeader` writes them.
    public struct RawRow: Sendable, Equatable {
        public let utcIso: Date
        public let monoMs: Int64
        public let sessionPct: Double?
        public let sessionResetsAtRaw: String?
        public let weeklyPct: Double?
        public let weeklyResetsAtRaw: String?
    }

    /// One row belonging to a specific current window, ready to feed into `WindowTracker.ingest`.
    public struct MatchedRow: Sendable, Equatable {
        public let utcIso: Date
        public let monoMs: Int64
        public let pct: Double
        public let resetsAtRaw: String
    }

    public static func readRows(path: String) -> [RawRow] {
        guard let contents = try? String(contentsOfFile: path, encoding: .utf8) else { return [] }
        return parse(contents)
    }

    /// Split out from file IO so tests can feed content directly.
    public static func parse(_ contents: String) -> [RawRow] {
        var rows: [RawRow] = []
        var isHeader = true
        contents.enumerateLines { line, _ in
            if isHeader { isHeader = false; return }          // header
            if let row = parseRow(line) { rows.append(row) }  // malformed rows are silently skipped
        }
        return rows
    }

    /// Rows for one window: matching `resets_at` within the same ±120 s jitter tolerance a live
    /// poll would use for "same window" (round-2 decision 6 — not exact truncated-second
    /// equality, so a row a live poll would have accepted is never excluded on a sub-second
    /// technicality), bounded to the window's own start (decision 8: NOT "now minus
    /// windowMinutes" — the inferred start is `windowKey − windowMinutes`), with rows later than
    /// the live observation's own UTC rejected outright (round-2 decision 5: the old "now + 60 s"
    /// allowance let a near-future row install a corrupted envelope that then made the genuine
    /// live sample look out of order and get rejected — there is no safe tolerance here, only
    /// "now" itself), utilization finite and in [0, 100] (decision 9), and `windowKey` itself
    /// bounds-checked (decision 10).
    ///
    /// Sorted oldest first, then by `monoMs` for deterministic ordering when two rows share a
    /// timestamp (decision 8), so replay reconstructs history in the order it happened.
    public static func filterForWindow(
        _ rows: [RawRow], kind: WindowKind, windowKey: Date, windowMinutes: Double, now: Date
    ) -> [MatchedRow] {
        guard QuotaTimeUtil.isPlausibleResetsAt(windowKey, now: now) else { return [] }

        let earliestAllowed = windowKey.addingTimeInterval(-windowMinutes * 60)
        let latestAllowed = now

        var matches: [MatchedRow] = []
        for row in rows {
            let pct = kind == .session ? row.sessionPct : row.weeklyPct
            let raw = kind == .session ? row.sessionResetsAtRaw : row.weeklyResetsAtRaw
            guard let pct, let raw else { continue }
            guard QuotaTimeUtil.isValidPct(pct) else { continue }
            guard let resetsAt = QuotaTimeUtil.parseResetsAt(raw) else { continue }
            guard abs(resetsAt.timeIntervalSince(windowKey)) <= WindowTracker.jitterToleranceSeconds else { continue }
            guard row.utcIso >= earliestAllowed else { continue }
            guard row.utcIso <= latestAllowed else { continue }
            matches.append(MatchedRow(utcIso: row.utcIso, monoMs: row.monoMs, pct: pct, resetsAtRaw: raw))
        }

        return matches.sorted {
            $0.utcIso == $1.utcIso ? $0.monoMs < $1.monoMs : $0.utcIso < $1.utcIso
        }
    }

    static func parseRow(_ line: String) -> RawRow? {
        guard let fields = splitCsvLine(line), fields.count >= 8 else { return nil }
        guard let utcIso = QuotaTimeUtil.parseResetsAt(fields[0]),
              let monoMs = Int64(fields[1]) else { return nil }

        return RawRow(
            utcIso: utcIso,
            monoMs: monoMs,
            sessionPct: parsePercentage(fields[2]),
            sessionResetsAtRaw: fields[3].isEmpty ? nil : fields[3],
            weeklyPct: parsePercentage(fields[4]),
            weeklyResetsAtRaw: fields[5].isEmpty ? nil : fields[5])
    }

    /// Decision 9: reject non-finite/out-of-range utilization at the CSV boundary too —
    /// `Double("NaN")` and `Double("Infinity")` both succeed. A rejected field becomes nil,
    /// i.e. normal "field absent" handling downstream, not a row-mutating failure.
    static func parsePercentage(_ field: String) -> Double? {
        guard let value = Double(field), QuotaTimeUtil.isValidPct(value) else { return nil }
        return value
    }

    /// Minimal quote-aware CSV split. Returns nil on an unterminated quote (malformed row).
    static func splitCsvLine(_ line: String) -> [String]? {
        var fields: [String] = []
        var current = ""
        var inQuotes = false
        var iterator = Array(line)
        var index = 0

        while index < iterator.count {
            let character = iterator[index]
            if inQuotes {
                if character == "\"" {
                    if index + 1 < iterator.count, iterator[index + 1] == "\"" {
                        current.append("\""); index += 1
                    } else {
                        inQuotes = false
                    }
                } else {
                    current.append(character)
                }
            } else if character == "\"" {
                inQuotes = true
            } else if character == "," {
                fields.append(current)
                current = ""
            } else {
                current.append(character)
            }
            index += 1
        }
        if inQuotes { return nil }
        fields.append(current)
        return fields
    }
}
