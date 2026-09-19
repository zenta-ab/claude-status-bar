import Foundation

/// Parses a `control_response` envelope into a `UsageSnapshot`.
///
/// The Windows parser (`src/Windows/Data/UsageParser.cs`) never hardcodes a path: it
/// depth-first-searches the tree for a key *containing* `five_hour` / `seven_day`, because the
/// container's shape is undocumented and has moved before. Phase 1 (docs/mac-port.md §3) found
/// that this is now one server-side key reorder away from reading the wrong bucket:
///
/// ```
///  0 five_hour   1 seven_day   2 seven_day_oauth_apps   3 seven_day_opus   4 seven_day_sonnet
///  … 7 limits    8 seven_day_cowork   9 seven_day_omelette   … 22 seven_day_breakdown
/// ```
///
/// `seven_day_oauth_apps`, `seven_day_cowork`, `seven_day_omelette` and `seven_day_breakdown`
/// carry no `opus`/`sonnet`/`haiku` qualifier, so "first unqualified match" is saved only by wire
/// order. Reading the wrong bucket is exactly a "confident wrong number".
///
/// Strategy, after the 2026-09-19 adversarial review:
///
///  1. **`limits[]` by `kind`** — self-describing; `session` / `weekly_all` matched `five_hour` /
///     `seven_day` bit-for-bit in every observation, microseconds included.
///  2. **The exact keys** `five_hour` and `seven_day`.
///  3. **Cross-check.** When both representations are present and usable they must agree. They
///     are two encodings of one number, so a disagreement means the response is in a state this
///     parser does not understand — during a server migration, say. That returns *no snapshot*
///     with an explanatory error, because the project's rule is that silence beats a wrong
///     verdict (docs/forecast-and-states.md, "Refusal → Measuring").
///  4. **The substring search**, last, and only when it is **unambiguous**. If several unqualified
///     `seven_day*` candidates are populated there is no principled way to choose between them --
///     `JSONSerialization` does not even preserve wire order -- so it reports unavailable rather
///     than picking one and attaching a caveat nobody downstream is obliged to read.
public enum UsageParser {
    public struct ParseResult: Sendable {
        public let snapshot: UsageSnapshot?
        public let error: String?
    }

    /// `controlResponse` is the decoded root envelope: `{"type":"control_response","response":{…}}`.
    public static func parse(controlResponse root: Any, now: Date = Date()) -> ParseResult {
        guard let rootObject = root as? [String: Any],
              let outer = rootObject["response"] as? [String: Any] else {
            return ParseResult(snapshot: nil, error: "control_response had no 'response' object")
        }
        // The payload is nested one level further: response.response holds rate_limits.
        let payload = (outer["response"] as? [String: Any]) ?? outer
        let subscriptionType = findSubscriptionType(payload)

        guard let rateLimits = payload["rate_limits"] as? [String: Any] else {
            // A logged-out or empty config dir answers `rate_limits: null` with
            // `rate_limits_available: false` (docs/mac-port.md §2). That is a legitimate,
            // expected state -- not a parse failure -- and must read as "no data", never as 0 %.
            let available = boolean(payload["rate_limits_available"])
            return ParseResult(
                snapshot: nil,
                error: available == false
                    ? "no rate_limits: this config directory is not logged in"
                    : "no rate_limits object in the response")
        }

        let fromLimits = readLimitsArray(rateLimits)
        let fromExact = readExactKeys(rateLimits)

        if let fromLimits, let fromExact, let disagreement = disagreement(fromLimits, fromExact) {
            return ParseResult(
                snapshot: nil,
                error: "limits[] and the exact keys disagree (\(disagreement)); refusing to guess")
        }

        // Prefer limits[] but fill anything it omits from the exact keys. They have already been
        // cross-checked, so this cannot import a contradicting value -- and dropping a deadline
        // that one encoding carries would make a live window look closed, which costs the
        // forecast its clock.
        if var reading = fromLimits {
            if let fromExact { reading = reading.completed(from: fromExact) }
            return ParseResult(snapshot: reading.snapshot(subscriptionType: subscriptionType, now: now),
                               error: nil)
        }
        if let fromExact {
            return ParseResult(snapshot: fromExact.snapshot(subscriptionType: subscriptionType, now: now),
                               error: nil)
        }

        switch readSubstringSearch(payload) {
        case .found(let reading):
            return ParseResult(
                snapshot: reading.snapshot(subscriptionType: subscriptionType, now: now),
                error: "read via the substring fallback: neither limits[] nor the exact keys were usable")
        case .ambiguous(let keys):
            return ParseResult(
                snapshot: nil,
                error: "ambiguous weekly buckets (\(keys.sorted().joined(separator: ", "))); refusing to guess")
        case .none:
            return ParseResult(snapshot: nil, error: "no usable five_hour/session node in the response")
        }
    }

    public static func parse(jsonLine: String, now: Date = Date()) -> ParseResult {
        guard let data = jsonLine.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) else {
            return ParseResult(snapshot: nil, error: "line is not valid JSON")
        }
        return parse(controlResponse: object, now: now)
    }

    // MARK: - One window's worth of readings

    struct Reading {
        var sessionPct: Double
        /// Optional: an expired 5 h window reports `resets_at: null` with `is_active: false`,
        /// which is a real state ("nothing running"), not a malformed reply.
        var sessionResetsAt: String?
        var sessionIsActive: Bool
        var weeklyPct: Double?
        var weeklyResetsAt: String?
        var weeklyIsActive: Bool
        var source: UsageSnapshot.Source

        /// Fills gaps from another reading of the same response. A reset present in either
        /// encoding means the window is open, whatever the other one omitted.
        func completed(from other: Reading) -> Reading {
            var merged = self
            if merged.sessionResetsAt == nil, let resets = other.sessionResetsAt {
                merged.sessionResetsAt = resets
                merged.sessionIsActive = true
            }
            if merged.weeklyPct == nil { merged.weeklyPct = other.weeklyPct }
            if merged.weeklyResetsAt == nil, let resets = other.weeklyResetsAt {
                merged.weeklyResetsAt = resets
                merged.weeklyIsActive = true
            }
            return merged
        }

        func snapshot(subscriptionType: String?, now: Date) -> UsageSnapshot {
            UsageSnapshot(
                sessionUtilization: sessionPct,
                sessionResetsAt: sessionResetsAt,
                weeklyUtilization: weeklyPct,
                weeklyResetsAt: weeklyResetsAt,
                subscriptionType: subscriptionType,
                sessionIsActive: sessionIsActive,
                weeklyIsActive: weeklyIsActive,
                observedAt: now,
                source: source)
        }
    }

    /// Two encodings of one number. A difference of more than half a point, or a different reset
    /// fingerprint, means they are not describing the same thing.
    static func disagreement(_ a: Reading, _ b: Reading) -> String? {
        if abs(a.sessionPct - b.sessionPct) > 0.5 {
            return "session \(a.sessionPct) vs \(b.sessionPct)"
        }
        // Only comparable when both carry one; an inactive window has none on either side.
        if let ar = a.sessionResetsAt, let br = b.sessionResetsAt, ar != br {
            return "session resets_at \(ar) vs \(br)"
        }
        if let aw = a.weeklyPct, let bw = b.weeklyPct, abs(aw - bw) > 0.5 {
            return "weekly \(aw) vs \(bw)"
        }
        if let ar = a.weeklyResetsAt, let br = b.weeklyResetsAt, ar != br {
            return "weekly resets_at \(ar) vs \(br)"
        }
        return nil
    }

    // MARK: - Strategy 1: the self-describing limits[] array

    static func readLimitsArray(_ rateLimits: [String: Any]) -> Reading? {
        guard let limits = rateLimits["limits"] as? [[String: Any]] else { return nil }
        var session: [String: Any]?
        var weekly: [String: Any]?
        // A duplicate `kind` means the response is malformed or ambiguous; taking the first and
        // ignoring the rest would be a silent choice between two different numbers.
        for entry in limits {
            switch entry["kind"] as? String {
            case "session":
                if session != nil { return nil }
                session = entry
            case "weekly_all":
                if weekly != nil { return nil }
                weekly = entry
            default: break   // weekly_scoped is a per-model bucket, never the "all models" ring
            }
        }
        guard let session, let sessionPct = validPercentage(session["percent"]) else { return nil }
        let sessionResets = resetString(session["resets_at"])
        // `is_active` is the server's own word for it; fall back to "has a deadline" when absent.
        let sessionActive = boolean(session["is_active"]) ?? (sessionResets != nil)

        // Weekly is validated independently: an invalid weekly must not invalidate a good
        // session reading, but it must not travel inside the snapshot either.
        var weeklyPct: Double?
        var weeklyResets: String?
        var weeklyActive = false
        if let weekly, let pct = validPercentage(weekly["percent"]) {
            weeklyPct = pct
            weeklyResets = resetString(weekly["resets_at"])
            weeklyActive = boolean(weekly["is_active"]) ?? (weeklyResets != nil)
        }
        return Reading(sessionPct: sessionPct, sessionResetsAt: sessionResets,
                       sessionIsActive: sessionActive,
                       weeklyPct: weeklyPct, weeklyResetsAt: weeklyResets,
                       weeklyIsActive: weeklyActive, source: .limitsArray)
    }

    // MARK: - Strategy 2: the exact keys

    static func readExactKeys(_ rateLimits: [String: Any]) -> Reading? {
        guard let fiveHour = rateLimits["five_hour"] as? [String: Any],
              let sessionPct = validPercentage(fiveHour["utilization"]) else { return nil }
        let sessionResets = resetString(fiveHour["resets_at"])

        var weeklyPct: Double?
        var weeklyResets: String?
        if let sevenDay = rateLimits["seven_day"] as? [String: Any],
           let pct = validPercentage(sevenDay["utilization"]) {
            weeklyPct = pct
            weeklyResets = resetString(sevenDay["resets_at"])
        }
        return Reading(sessionPct: sessionPct, sessionResetsAt: sessionResets,
                       sessionIsActive: sessionResets != nil,
                       weeklyPct: weeklyPct, weeklyResetsAt: weeklyResets,
                       weeklyIsActive: weeklyResets != nil, source: .exactKeys)
    }

    // MARK: - Strategy 3: the substring search, and only when unambiguous

    enum SubstringOutcome {
        case found(Reading)
        case ambiguous(Set<String>)
        case none
    }

    static func readSubstringSearch(_ payload: [String: Any]) -> SubstringOutcome {
        let sessionMatches = collect(payload, containing: "five_hour")
            .filter { $0.value is [String: Any] }
        guard sessionMatches.count == 1,
              let fiveHour = sessionMatches[0].value as? [String: Any],
              let sessionPct = validPercentage(fiveHour["utilization"]) else {
            if sessionMatches.count > 1 {
                return .ambiguous(Set(sessionMatches.map(\.key)))
            }
            return .none
        }
        let sessionResets = resetString(fiveHour["resets_at"])

        let qualifiers = ["opus", "sonnet", "haiku"]
        let weeklyCandidates = collect(payload, containing: "seven_day").filter { match in
            guard match.value is [String: Any] else { return false }
            let key = match.key.lowercased()
            return !qualifiers.contains { key.contains($0) }
        }
        // `JSONSerialization` returns an unordered Dictionary, so the Windows parser's
        // "first hit in wire order" tie-break cannot be reproduced. With more than one live
        // candidate the honest answer is that we do not know which is the all-models bucket.
        if weeklyCandidates.count > 1 {
            return .ambiguous(Set(weeklyCandidates.map(\.key)))
        }
        var weeklyPct: Double?
        var weeklyResets: String?
        if let weekly = weeklyCandidates.first?.value as? [String: Any],
           let pct = validPercentage(weekly["utilization"]) {
            weeklyPct = pct
            weeklyResets = resetString(weekly["resets_at"])
        }
        return .found(Reading(sessionPct: sessionPct, sessionResetsAt: sessionResets,
                              sessionIsActive: sessionResets != nil,
                              weeklyPct: weeklyPct, weeklyResetsAt: weeklyResets,
                              weeklyIsActive: weeklyResets != nil,
                              source: .substringSearch))
    }

    // MARK: - Helpers

    static func findSubscriptionType(_ payload: [String: Any]) -> String? {
        if let direct = payload["subscription_type"] as? String { return direct }
        return collect(payload, containing: "subscription_type")
            .compactMap { $0.value as? String }.first
    }

    /// Preorder depth-first walk; collects every value whose owning key contains `needle`.
    /// Unbounded recursion is not a concern here: `JSONSerialization` rejects deeply nested
    /// input before this ever sees it (verified — a 5 000-deep array fails to decode).
    static func collect(_ value: Any, containing needle: String) -> [(key: String, value: Any)] {
        var results: [(key: String, value: Any)] = []
        func walk(_ node: Any) {
            if let object = node as? [String: Any] {
                for (key, child) in object {
                    if key.lowercased().contains(needle) { results.append((key, child)) }
                    walk(child)
                }
            } else if let array = node as? [Any] {
                for item in array { walk(item) }
            }
        }
        walk(value)
        return results
    }

    /// The same field arrives as a JSON int in some responses and a JSON float in others.
    ///
    /// JSON booleans must NOT pass: `JSONSerialization` bridges `true` to an `NSNumber` whose
    /// `doubleValue` is `1.0`, so `{"percent": true}` would otherwise render as "1 % used" --
    /// a confident wrong number from a malformed reply. Verified against Foundation, not assumed.
    static func number(_ value: Any?) -> Double? {
        guard let value else { return nil }
        if let number = value as? NSNumber {
            if CFGetTypeID(number) == CFBooleanGetTypeID() { return nil }
            return number.doubleValue
        }
        if let d = value as? Double { return d }
        if let i = value as? Int { return Double(i) }
        return nil
    }

    static func boolean(_ value: Any?) -> Bool? {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }

    /// docs/forecast-and-states.md, round-1 decision 9: utilization must be finite and in
    /// [0, 100] at the boundary, or the window is treated as missing rather than as 0 %.
    static func validPercentage(_ value: Any?) -> Double? {
        guard let parsed = number(value), UsageSnapshot.isValidPercentage(parsed) else { return nil }
        return parsed
    }

    /// Kept as the **raw string** from the wire: the exact byte sequence is the snapshot
    /// fingerprint the model dedups on. It is bounds-checked as a timestamp but never
    /// reformatted -- reformatting would destroy the microsecond digits that carry the signal.
    static func resetString(_ value: Any?) -> String? {
        guard let text = value as? String, !text.isEmpty, text.count <= 64 else { return nil }
        return text
    }
}
