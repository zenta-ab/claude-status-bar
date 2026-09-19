import Foundation
import Testing

@testable import ClaudeQuotaCore

/// Fixtures are written by hand rather than recorded, for two reasons: a recording of this
/// machine's real reply would carry the account's plan and usage into a public repo, and the
/// case that matters most (a populated decoy key winning the substring search) does not occur
/// in any recording yet -- it is the failure this parser exists to prevent.
private func envelope(rateLimits: String, subscriptionType: String = "\"max\"") -> String {
    """
    {"type":"control_response","response":{"request_id":"1","subtype":"success","response":{
      "behaviors":null,"rate_limits":\(rateLimits),"rate_limits_available":true,
      "subscription_type":\(subscriptionType)}}}
    """
}

/// The shape observed on macOS 2026-09-18, decoys included, trimmed to the keys that matter.
private let realShapedRateLimits = """
{
  "five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
  "seven_day":{"utilization":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"},
  "seven_day_oauth_apps":null,"seven_day_opus":null,"seven_day_sonnet":null,
  "seven_day_cowork":null,"seven_day_omelette":null,"seven_day_breakdown":null,
  "limits":[
    {"group":"session","kind":"session","percent":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
    {"group":"weekly","kind":"weekly_all","percent":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"},
    {"group":"weekly","kind":"weekly_scoped","percent":0,"resets_at":"2026-09-20T06:00:00+00:00",
     "scope":{"model":{"display_name":"Some Model"}}}
  ]
}
"""

@Suite("UsageParser")
struct UsageParserTests {

    @Test("reads the real response shape via limits[], not the substring search")
    func readsViaLimitsArray() {
        let result = UsageParser.parse(jsonLine: envelope(rateLimits: realShapedRateLimits))
        let snapshot = try! #require(result.snapshot)
        #expect(snapshot.source == .limitsArray)
        #expect(snapshot.sessionUtilization == 76)
        #expect(snapshot.weeklyUtilization == 41)
        #expect(snapshot.subscriptionType == "max")
        #expect(result.error == nil)
    }

    @Test("keeps resets_at byte-for-byte: the microseconds are the fingerprint")
    func preservesFingerprint() {
        let result = UsageParser.parse(jsonLine: envelope(rateLimits: realShapedRateLimits))
        let snapshot = try! #require(result.snapshot)
        // docs/forecast-and-states.md, Ingest rule 2. Parsing to a Date and reformatting would
        // destroy exactly the digits the dedup rule reads.
        #expect(snapshot.sessionResetsAt == "2026-09-18T16:10:00.650307+00:00")
        #expect(snapshot.weeklyResetsAt == "2026-09-20T06:00:00.650340+00:00")
    }

    @Test("weekly_scoped is never mistaken for the all-models weekly ring")
    func ignoresScopedWeekly() {
        let result = UsageParser.parse(jsonLine: envelope(rateLimits: realShapedRateLimits))
        let snapshot = try! #require(result.snapshot)
        #expect(snapshot.weeklyUtilization == 41)   // not the scoped bucket's 0
    }

    /// The regression this parser was written for (docs/mac-port.md §3). A populated
    /// `seven_day_breakdown` carries no model qualifier, so the Windows "first unqualified
    /// match" rule can pick it. With `limits[]` present the answer must still be the real
    /// weekly bucket.
    @Test("a populated unqualified decoy key cannot win when limits[] is present")
    func decoyKeyLosesToLimitsArray() {
        let withDecoy = """
        {
          "seven_day_breakdown":{"utilization":3,"resets_at":"2026-09-19T00:00:00.000001+00:00"},
          "seven_day":{"utilization":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"},
          "five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
          "limits":[
            {"kind":"session","percent":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"},
            {"kind":"weekly_all","percent":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"}
          ]
        }
        """
        let snapshot = try! #require(UsageParser.parse(jsonLine: envelope(rateLimits: withDecoy)).snapshot)
        #expect(snapshot.source == .limitsArray)
        #expect(snapshot.weeklyUtilization == 41)
        #expect(snapshot.weeklyResetsAt == "2026-09-20T06:00:00.650340+00:00")
    }

    @Test("without limits[], the exact keys still beat a populated decoy")
    func exactKeysBeatDecoy() {
        let withDecoyNoLimits = """
        {
          "seven_day_breakdown":{"utilization":3,"resets_at":"2026-09-19T00:00:00.000001+00:00"},
          "seven_day":{"utilization":41,"resets_at":"2026-09-20T06:00:00.650340+00:00"},
          "five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.650307+00:00"}
        }
        """
        let snapshot = try! #require(UsageParser.parse(jsonLine: envelope(rateLimits: withDecoyNoLimits)).snapshot)
        #expect(snapshot.source == .exactKeys)
        #expect(snapshot.weeklyUtilization == 41)
    }

    @Test("a renamed session key degrades to the substring search rather than to nothing")
    func fallsBackToSubstringSearch() {
        let renamed = """
        {"five_hour_v2":{"utilization":12,"resets_at":"2026-09-18T16:10:00.1+00:00"},
         "seven_day_v2":{"utilization":7,"resets_at":"2026-09-20T06:00:00.1+00:00"}}
        """
        let result = UsageParser.parse(jsonLine: envelope(rateLimits: renamed))
        let snapshot = try! #require(result.snapshot)
        #expect(snapshot.source == .substringSearch)
        #expect(snapshot.sessionUtilization == 12)
        #expect(result.error != nil)   // the caller must be able to see it took the fragile path
    }

    /// docs/mac-port.md §2: an empty config directory answers this way. It must read as "no
    /// data", never as 0 % used, or the icon would confidently show a full quota to someone who
    /// is simply logged out.
    @Test("a logged-out config directory yields no snapshot, not 0 %")
    func loggedOutIsNotZero() {
        let line = """
        {"type":"control_response","response":{"request_id":"1","subtype":"success","response":{
          "behaviors":null,"rate_limits":null,"rate_limits_available":false,"subscription_type":null}}}
        """
        let result = UsageParser.parse(jsonLine: line)
        #expect(result.snapshot == nil)
        #expect(result.error?.contains("not logged in") == true)
    }

    @Test("utilization outside [0,100] or non-finite is rejected", arguments: [
        "-1", "101", "1e400",
    ])
    func rejectsOutOfRangeUtilization(_ value: String) {
        // Round-1 decision 9: guard at the boundary, so a garbled value can never poison the model.
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":\(value),"resets_at":"2026-09-18T16:10:00.1+00:00"}}
        """)
        #expect(UsageParser.parse(jsonLine: line).snapshot == nil)
    }

    @Test("a structurally surprising envelope is reported, never thrown")
    func handlesMalformedEnvelopes() {
        for line in [
            #"{"type":"control_response","response":null}"#,
            #"{"type":"control_response"}"#,
            #"{"type":"something_else","response":{}}"#,
            "not json at all",
            "{}",
        ] {
            let result = UsageParser.parse(jsonLine: line)
            #expect(result.snapshot == nil)
            #expect(result.error != nil)
        }
    }

    @Test("utilization arrives as int in some replies and float in others")
    func acceptsIntAndFloat() {
        for value in ["76", "76.0", "76.4"] {
            let line = envelope(rateLimits: """
            {"five_hour":{"utilization":\(value),"resets_at":"2026-09-18T16:10:00.1+00:00"}}
            """)
            let snapshot = try! #require(UsageParser.parse(jsonLine: line).snapshot)
            #expect(snapshot.sessionUtilization! >= 76)
        }
    }

    @Test("a missing weekly node leaves weekly nil rather than 0 %")
    func missingWeeklyIsNil() {
        // Decision 13: a window missing from an otherwise successful poll is "data saknas",
        // never silently 0 %.
        let line = envelope(rateLimits: """
        {"five_hour":{"utilization":76,"resets_at":"2026-09-18T16:10:00.1+00:00"}}
        """)
        let snapshot = try! #require(UsageParser.parse(jsonLine: line).snapshot)
        #expect(snapshot.weeklyUtilization == nil)
        #expect(snapshot.hasValidWeekly == false)
        #expect(snapshot.hasValidSession == true)
    }
}
