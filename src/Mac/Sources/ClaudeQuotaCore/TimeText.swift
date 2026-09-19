import Foundation

/// The single shared time formatter for panel-v2 (docs/panel-v2.md, "Time text"). Every place the
/// app shows a duration or a point in time — the status box, the per-window sections, the footer,
/// the freshness line, the menu-bar tooltip — goes through this, so a raw minute count above an
/// hour ("6975 min") can never leak out again.
///
/// Pure functions of (value, now, timeZone) so tests are deterministic regardless of the
/// machine's real clock or timezone. Ported from `src/Windows/Ui/TimeText.cs`.
public enum TimeText {
    /// Index 0…6 = Sunday…Saturday, matching .NET's `DayOfWeek`.
    static let weekdayAbbrev = ["sön", "mån", "tis", "ons", "tor", "fre", "lör"]

    /// Rounds half away from zero, like .NET's `ToString("F0")`.
    ///
    /// `String(format: "%.0f", …)` rounds half to **even**, so 0.5 formats as "0" in C and "1" in
    /// C#. Left alone, every panel percentage sitting exactly on .5 would read one point apart
    /// between the two implementations — a silent, permanent divergence in the copy the shared
    /// spec is supposed to keep identical.
    public static func percent(_ value: Double) -> String {
        guard value.isFinite else { return "–" }
        let rounded = (value).rounded(.toNearestOrAwayFromZero)
        return String(format: "%.0f", rounded)
    }

    /// A span of time: under 1 min → "44 s"; under 2 min → "1 min 30 s"; under 1 h → "44 min";
    /// under 24 h → "20 h 15 min" (drops "0 min"); 24 h and up → "4 d 20 h" (drops minutes).
    /// Negative spans clamp to zero.
    public static func duration(_ seconds: TimeInterval) -> String {
        // Real subtraction can leave sub-second jitter; round once, up front.
        let total = max(0.0, seconds).rounded()
        let whole = Int(total)

        if whole < 60 { return "\(whole) s" }
        if whole < 120 { return "1 min \(whole % 60) s" }
        if whole < 3600 { return "\(whole / 60) min" }
        if whole < 86_400 {
            let hours = whole / 3600
            let minutes = (whole % 3600) / 60
            return minutes == 0 ? "\(hours) h" : "\(hours) h \(minutes) min"
        }
        return "\(whole / 86_400) d \((whole % 86_400) / 3600) h"
    }

    /// "kl 12:36" today; "i morgon kl 10:44" tomorrow; "sön kl 10:44" later. Calendar-date
    /// comparison in `timeZone`, not elapsed time, so crossing midnight is handled correctly.
    public static func pointInTime(_ t: Date, now: Date, timeZone: TimeZone) -> String {
        let clock = clockOnly(t, timeZone: timeZone)
        switch dayDiff(t, now: now, timeZone: timeZone) {
        case 0: return "kl \(clock)"
        case 1: return "i morgon kl \(clock)"
        default: return "\(weekday(t, timeZone: timeZone)) kl \(clock)"
        }
    }

    /// Calendar-date difference between `t` and `now` in `timeZone` — 0 today, 1 tomorrow, and
    /// negative when `t` falls before `now`'s date.
    public static func dayDiff(_ t: Date, now: Date, timeZone: TimeZone) -> Int {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let a = calendar.startOfDay(for: t)
        let b = calendar.startOfDay(for: now)
        return calendar.dateComponents([.day], from: b, to: a).day ?? 0
    }

    public static func isToday(_ t: Date, now: Date, timeZone: TimeZone) -> Bool {
        dayDiff(t, now: now, timeZone: timeZone) == 0
    }

    /// Just the weekday abbreviation, e.g. "sön" — for the compositions (Kvot-bar depletion
    /// label, weekly section header) that spell the weekday differently from `pointInTime`'s
    /// "kl"-prefixed form.
    public static func weekday(_ t: Date, timeZone: TimeZone) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        // Foundation's .weekday is 1…7 with 1 = Sunday; the table is 0-based.
        return weekdayAbbrev[calendar.component(.weekday, from: t) - 1]
    }

    public static func clockOnly(_ t: Date, timeZone: TimeZone) -> String {
        format(t, timeZone: timeZone, pattern: "HH:mm")
    }

    /// Precise timestamp with seconds, for the footer's "senast avläst" line — the one place a
    /// raw clock reading, rather than a rounded point in time, is wanted.
    public static func clockWithSeconds(_ t: Date, timeZone: TimeZone) -> String {
        format(t, timeZone: timeZone, pattern: "HH:mm:ss")
    }

    private static func format(_ t: Date, timeZone: TimeZone, pattern: String) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = pattern
        return formatter.string(from: t)
    }

    /// "A reset always states both when and how long until": "kl 13:20 (om 37 min)" /
    /// "fre kl 07:00 (om 6 d 18 h)". `forceWeekday` is set for the weekly window, which always
    /// carries its weekday even when the reset falls today or tomorrow.
    ///
    /// A reset at or before now — never a real future countdown, but a race between the clock and
    /// the next poll, or a window genuinely awaiting its next observation — collapses to "nu"
    /// rather than a stale clock time paired with "(om 0 s)" or a negative duration.
    public static func reset(_ resetsAt: Date, now: Date, timeZone: TimeZone, forceWeekday: Bool) -> String {
        let span = resetsAt.timeIntervalSince(now)
        if span <= 0 { return "nu" }
        let clock = forceWeekday
            ? "\(weekday(resetsAt, timeZone: timeZone)) kl \(clockOnly(resetsAt, timeZone: timeZone))"
            : pointInTime(resetsAt, now: now, timeZone: timeZone)
        return "\(clock) (om \(duration(span)))"
    }

    /// The "om <duration>" fragment for compositions that build their own reset-header shape
    /// rather than going through `reset`/`depletion`: "nu" when the span is zero or negative,
    /// never "om 0 s" or a negative duration.
    public static func countdownFragment(_ seconds: TimeInterval) -> String {
        seconds <= 0 ? "nu" : "om \(duration(seconds))"
    }

    /// "Running out always states both when and how long until", regardless of day: "kl 15:01
    /// (om 1 h 43 min)" today, "sön kl 12:09 (om 1 d 22 h)" later — the depletion-specific
    /// sibling of `reset`. Never today/tomorrow-aware like `pointInTime`; only today vs. a later
    /// day, per the doc's two literal examples. Same "nu" collapse as `reset`.
    public static func depletion(_ dep: Date, now: Date, timeZone: TimeZone) -> String {
        let span = dep.timeIntervalSince(now)
        if span <= 0 { return "nu" }
        let clock = isToday(dep, now: now, timeZone: timeZone)
            ? "kl \(clockOnly(dep, timeZone: timeZone))"
            : "\(weekday(dep, timeZone: timeZone)) kl \(clockOnly(dep, timeZone: timeZone))"
        return "\(clock) (om \(duration(span)))"
    }
}
