import Foundation

/// Which state colour a status-box verdict (or window section) renders in; the UI maps this to
/// the palette.
public enum PanelColorRole: Sendable { case safe, tight, crit, dead, measuring, unknown }

/// Line 1 is the verdict, bold, in `role`'s colour. Line 2 always states **when** (a reset
/// clock + countdown, or — DryEarly — a shortfall before that reset). `line3` is the Stale
/// freshness note, shown in addition to whatever the verdict already says. `secondaryLine` is the
/// one extra line for the window that is NOT driving the box, when that window is itself
/// Tight/DryEarly/Spent (docs/panel-v2.md, item 2).
public struct StatusBoxText: Sendable, Equatable {
    public let line1: String
    public let line2: String
    public let line3: String?
    public let secondaryLine: String?
    public let role: PanelColorRole

    public init(line1: String, line2: String, line3: String?, secondaryLine: String?, role: PanelColorRole) {
        self.line1 = line1
        self.line2 = line2
        self.line3 = line3
        self.secondaryLine = secondaryLine
        self.role = role
    }

}

/// Everything one window's section (docs/panel-v2.md, item 3) shows in text. Bar fractions are
/// drawn straight from `WindowView` by the panel — this is text only.
public struct WindowSectionText: Sendable, Equatable {
    public let title: String
    public let resetHeader: String
    public let tidLabel: String
    public let kvotLabel: String

    public init(title: String, resetHeader: String, tidLabel: String, kvotLabel: String) {
        self.title = title
        self.resetHeader = resetHeader
        self.tidLabel = tidLabel
        self.kvotLabel = kvotLabel
    }

}

public struct PanelTextResult: Sendable, Equatable {
    public let statusBox: StatusBoxText
    public let session: WindowSectionText
    public let weekly: WindowSectionText

    public init(statusBox: StatusBoxText, session: WindowSectionText, weekly: WindowSectionText) {
        self.statusBox = statusBox
        self.session = session
        self.weekly = weekly
    }

}

/// One row of the panel's "other accounts" list (docs/multi-account.md, "Display").
public struct OtherAccountRow: Sendable, Equatable {
    public let accountIndex: Int
    public let label: String
    public let line: String
    public let role: PanelColorRole

    public init(accountIndex: Int, label: String, line: String, role: PanelColorRole) {
        self.accountIndex = accountIndex
        self.label = label
        self.line = line
        self.role = role
    }

}

/// The panel-v2 "answer first" text layer (docs/panel-v2.md): a pure function of
/// (QuotaView, now, timeZone) → every string the panel shows, no drawing. Kept separate from the
/// view so the exact wording is testable without a graphics context. Every time value flows
/// through `TimeText` — nothing here does its own minute arithmetic for display.
///
/// Ported from `src/Windows/Ui/PanelText.cs`.
public enum PanelText {
    // `WindowView.measuringReason` is a plain string, sourced from `QuotaModel.reasonText` —
    // these two literals must keep matching that switch verbatim; it is the only way to
    // recognise a reason worth surfacing by name instead of folding it into the generic
    // "X % använt · återställs …" line.
    static let awaitingResetText = "Nytt fönster väntas"
    static let tooEarlyInWeekText = "För tidigt i veckan — väntar på ett helt dygn"
    static let windowInactiveText = "Inget förbrukat ännu"

    public static func compose(_ view: QuotaView, now: Date, timeZone: TimeZone) -> PanelTextResult {
        PanelTextResult(
            statusBox: composeStatusBox(view, now: now, timeZone: timeZone),
            session: composeSection("AKTUELL SESSION", kind: .session, window: view.session, now: now, timeZone: timeZone),
            weekly: composeSection("VECKA", kind: .weekly, window: view.weekly, now: now, timeZone: timeZone))
    }

    /// One compact "other accounts" row: label, verdict colour, one short line — "räcker till
    /// reset" / "slut 15:01" / "mäter takt", never a full status-box sentence. Same driver rule
    /// as the status box (more severe window, session wins an exact tie).
    public static func composeOtherAccountRow(
        accountIndex: Int, label: String, view: QuotaView, now: Date, timeZone: TimeZone
    ) -> OtherAccountRow {
        if view.freshness == .unknown {
            return OtherAccountRow(accountIndex: accountIndex, label: label,
                                   line: "går inte att läsa", role: .unknown)
        }
        if let blockedUntil = view.blockedUntil {
            return OtherAccountRow(accountIndex: accountIndex, label: label,
                                   line: "slut · öppnar \(TimeText.clockOnly(blockedUntil, timeZone: timeZone))",
                                   role: .dead)
        }

        let driver = view.session.state.rawValue >= view.weekly.state.rawValue ? view.session : view.weekly
        var line: String
        var role: PanelColorRole
        switch driver.state {
        case .measuring: (line, role) = ("mäter takt", .measuring)
        case .tight: (line, role) = ("tajt — räcker precis", .tight)
        case .dryEarly:
            if let dep = driver.depletesAt {
                (line, role) = ("slut \(TimeText.clockOnly(dep, timeZone: timeZone))", .crit)
            } else {
                (line, role) = ("nästan slut", .crit)
            }
        case .spent:
            if let resets = driver.resetsAt {
                (line, role) = ("slut · öppnar \(TimeText.clockOnly(resets, timeZone: timeZone))", .dead)
            } else {
                (line, role) = ("slut", .dead)
            }
        case .safe: (line, role) = ("räcker till reset", .safe)
        }

        if view.freshness == .stale { line += " · kan vara inaktuell" }
        return OtherAccountRow(accountIndex: accountIndex, label: label, line: line, role: role)
    }

    // ---- status box (docs/panel-v2.md, item 2) ----

    static func composeStatusBox(_ view: QuotaView, now: Date, timeZone: TimeZone) -> StatusBoxText {
        if view.freshness == .unknown {
            let line2 = view.lastPollAt.map {
                "Senast avläst \(TimeText.pointInTime($0, now: now, timeZone: timeZone)) · försöker igen"
            } ?? "Väntar på första avläsningen… · försöker igen"
            return StatusBoxText(line1: "Kan inte läsa kvoten", line2: line2, line3: nil,
                                 secondaryLine: nil, role: .unknown)
        }

        var staleLine3: String?
        if view.freshness == .stale, let changed = view.lastChangedAt {
            staleLine3 = "Datan kan vara inaktuell — senast ändrad för "
                + TimeText.duration(now.timeIntervalSince(changed)) + " sedan"
        }

        if let blockedUntil = view.blockedUntil {
            let weeklyDrives = view.weekly.state == .spent
                && (view.session.state != .spent
                    || (view.weekly.resetsAt ?? .distantPast) >= (view.session.resetsAt ?? .distantPast))
            let word = weeklyDrives ? "Veckokvoten" : "Kvoten"
            return StatusBoxText(
                line1: "\(word) är slut",
                line2: "Öppnar igen \(TimeText.reset(blockedUntil, now: now, timeZone: timeZone, forceWeekday: weeklyDrives))",
                line3: staleLine3,
                secondaryLine: secondaryLine(view, drivingIsSession: !weeklyDrives, now: now, timeZone: timeZone),
                role: .dead)
        }

        if view.iconSeverity == .measuring {
            let measuring = composeMeasuring(view, now: now, timeZone: timeZone)
            return StatusBoxText(line1: measuring.line1, line2: measuring.line2, line3: staleLine3,
                                 secondaryLine: measuring.secondaryLine, role: measuring.role)
        }

        let sessionDrives = view.session.state.rawValue >= view.weekly.state.rawValue
        let driver = sessionDrives ? view.session : view.weekly
        let kvotWordLower = sessionDrives ? "kvoten" : "veckokvoten"
        let kvotWordCap = sessionDrives ? "Kvoten" : "Veckokvoten"
        let subject = sessionDrives ? "Sessionen" : "Veckan"
        let driverIsWeekly = !sessionDrives

        let verdict: StatusBoxText
        switch driver.state {
        case .dryEarly:
            verdict = composeDryEarly(driver, kvotWordCap: kvotWordCap, driverIsWeekly: driverIsWeekly,
                                      now: now, timeZone: timeZone)
        case .tight:
            verdict = composeSafeOrTight(line1: "Tajt — \(kvotWordLower) räcker precis", subject: subject,
                                         driver: driver, driverIsWeekly: driverIsWeekly, role: .tight,
                                         now: now, timeZone: timeZone)
        default:
            verdict = composeSafeOrTight(line1: "✓ \(kvotWordCap) räcker till reset", subject: subject,
                                         driver: driver, driverIsWeekly: driverIsWeekly, role: .safe,
                                         now: now, timeZone: timeZone)
        }

        return StatusBoxText(line1: verdict.line1, line2: verdict.line2, line3: staleLine3,
                             secondaryLine: secondaryLine(view, drivingIsSession: sessionDrives,
                                                          now: now, timeZone: timeZone),
                             role: verdict.role)
    }

    static func composeSafeOrTight(line1: String, subject: String, driver: WindowView,
                                   driverIsWeekly: Bool, role: PanelColorRole,
                                   now: Date, timeZone: TimeZone) -> StatusBoxText {
        let projected = driver.projectedPctAtReset ?? driver.usedPct ?? 0
        let resetText = driver.resetsAt.map {
            TimeText.reset($0, now: now, timeZone: timeZone, forceWeekday: driverIsWeekly)
        } ?? "okänt"
        return StatusBoxText(
            line1: line1,
            line2: "\(subject) landar på ~\(TimeText.percent(projected)) % · återställs \(resetText)",
            line3: nil, secondaryLine: nil, role: role)
    }

    static func composeDryEarly(_ driver: WindowView, kvotWordCap: String, driverIsWeekly: Bool,
                                now: Date, timeZone: TimeZone) -> StatusBoxText {
        // "Running out always states both when and how long until" — today AND any later day:
        // TimeText.depletion carries the "(om …)" duration regardless of which.
        let depletionText = driver.depletesAt.map { TimeText.depletion($0, now: now, timeZone: timeZone) } ?? "snart"
        let resetClock = driver.resetsAt.map { resets -> String in
            driverIsWeekly
                ? "\(TimeText.weekday(resets, timeZone: timeZone)) kl \(TimeText.clockOnly(resets, timeZone: timeZone))"
                : "kl \(TimeText.clockOnly(resets, timeZone: timeZone))"
        } ?? "okänt"

        return StatusBoxText(
            line1: "⚠ \(kvotWordCap) tar slut \(depletionText)",
            line2: "\(TimeText.duration(driver.shortfallMinutes * 60)) före reset \(resetClock), om du fortsätter i samma takt",
            line3: nil, secondaryLine: nil, role: .crit)
    }

    /// Both windows are Measuring (the only way the overall severity IS Measuring). A window
    /// carrying one of the two "special" reasons is surfaced by name; otherwise the row falls
    /// back to the generic "X % använt · återställs …" form.
    static func composeMeasuring(_ view: QuotaView, now: Date, timeZone: TimeZone) -> StatusBoxText {
        let line1 = "Mäter takt…"
        let specials = [awaitingResetText, tooEarlyInWeekText]
        let weeklySpecial = view.weekly.measuringReason.map(specials.contains) ?? false
        let sessionSpecial = view.session.measuringReason.map(specials.contains) ?? false

        let driver: WindowView
        let isWeekly: Bool
        if weeklySpecial { (driver, isWeekly) = (view.weekly, true) }
        else if sessionSpecial { (driver, isWeekly) = (view.session, false) }
        else { (driver, isWeekly) = (view.session, false) }   // no special reason: session is the default

        if driver.measuringReason == awaitingResetText {
            // The window's own reset has already passed and no new one has been confirmed yet —
            // there is no sane future clock to count down to, so the reason stands alone rather
            // than pairing it with a stale reset time.
            return StatusBoxText(line1: line1, line2: awaitingResetText, line3: nil,
                                 secondaryLine: nil, role: .measuring)
        }

        let pctPart = driver.usedPct.map { "\(TimeText.percent($0)) % använt" } ?? "Hämtar…"
        let resetText = driver.resetsAt.map {
            TimeText.reset($0, now: now, timeZone: timeZone, forceWeekday: isWeekly)
        } ?? "okänt"
        let line2 = driver.measuringReason == tooEarlyInWeekText
            ? "\(pctPart) · \(tooEarlyInWeekText) · återställs \(resetText)"
            : "\(pctPart) · återställs \(resetText)"

        return StatusBoxText(line1: line1, line2: line2, line3: nil, secondaryLine: nil, role: .measuring)
    }

    /// The one extra line for whichever window is NOT driving the box, only when it is itself
    /// Tight/DryEarly/Spent (docs/panel-v2.md, item 2).
    static func secondaryLine(_ view: QuotaView, drivingIsSession: Bool,
                              now: Date, timeZone: TimeZone) -> String? {
        let other = drivingIsSession ? view.weekly : view.session
        let otherIsWeekly = drivingIsSession
        let subject = otherIsWeekly ? "Veckokvoten" : "Sessionens kvot"

        switch other.state {
        case .tight:
            guard let resets = other.resetsAt else { return nil }
            return "Dessutom tajt: \(subject) räcker precis · återställs "
                + TimeText.reset(resets, now: now, timeZone: timeZone, forceWeekday: otherIsWeekly)
        case .dryEarly:
            guard let dep = other.depletesAt else { return nil }
            return "Dessutom: \(subject) tar slut \(TimeText.depletion(dep, now: now, timeZone: timeZone))"
        case .spent:
            guard let resets = other.resetsAt else { return nil }
            return "Dessutom: \(subject) är slut · öppnar "
                + TimeText.reset(resets, now: now, timeZone: timeZone, forceWeekday: otherIsWeekly)
        default:
            return nil
        }
    }

    // ---- window sections (docs/panel-v2.md, item 3) ----

    static func composeSection(_ title: String, kind: WindowKind, window: WindowView,
                               now: Date, timeZone: TimeZone) -> WindowSectionText {
        let isWeekly = kind == .weekly
        let awaitingReset = window.state == .measuring && window.measuringReason == awaitingResetText

        let resetHeader: String
        let tidLabel: String

        if awaitingReset {
            // The window's own reset has already passed and no new one has been confirmed yet —
            // there is nothing sane to count down to or measure elapsed time against. The Tid bar
            // still draws full and the Kvot bar draws empty to match.
            resetHeader = "väntar på nytt fönster"
            tidLabel = "fönstret är slut"
        } else {
            if let resets = window.resetsAt {
                let when = isWeekly
                    ? "\(TimeText.weekday(resets, timeZone: timeZone)) \(TimeText.clockOnly(resets, timeZone: timeZone))"
                    : "kl \(TimeText.clockOnly(resets, timeZone: timeZone))"
                resetHeader = "återställs \(TimeText.countdownFragment(resets.timeIntervalSince(now))) · \(when)"
            } else {
                resetHeader = "Väntar på data…"
            }

            let windowLengthText = isWeekly ? "veckan" : TimeText.duration(window.windowMinutes * 60)
            let elapsedFraction: Double
            if let resets = window.resetsAt {
                let raw = (window.windowMinutes - resets.timeIntervalSince(now) / 60.0) / window.windowMinutes
                elapsedFraction = min(max(raw, 0.0), 1.0)
            } else {
                elapsedFraction = 0
            }
            tidLabel = "\(TimeText.percent(elapsedFraction * 100)) % av \(windowLengthText) har gått"
        }

        return WindowSectionText(title: title, resetHeader: resetHeader, tidLabel: tidLabel,
                                 kvotLabel: buildKvotLabel(window, now: now, timeZone: timeZone))
    }

    static func buildKvotLabel(_ window: WindowView, now: Date, timeZone: TimeZone) -> String {
        let pct = window.usedPct.map { "\(TimeText.percent($0)) %" } ?? "–"

        if window.state == .measuring {
            // Gated on the reason text, the same two literals as the status box, not on state
            // alone — the generic "för tidigt för prognos" wording covers every other reason.
            if let reason = window.measuringReason,
               reason == awaitingResetText || reason == tooEarlyInWeekText || reason == windowInactiveText {
                return reason
            }
            return "\(pct) använt · för tidigt för prognos"
        }

        if window.state == .spent { return "100 % · slut" }

        if window.state == .dryEarly {
            // The Kvot-bar depletion wording differs from the status box's by design (panel-v2's
            // implementation notes): it drops "kl" before the weekday ("slut sön 10:44 (om …)"),
            // so it does not route through TimeText.depletion, which always keeps "kl". It shares
            // the same "nu" collapse for a depletion at or before now.
            let dep: String
            if let depletesAt = window.depletesAt {
                let span = depletesAt.timeIntervalSince(now)
                if span <= 0 {
                    dep = "nu"
                } else if TimeText.isToday(depletesAt, now: now, timeZone: timeZone) {
                    dep = "kl \(TimeText.clockOnly(depletesAt, timeZone: timeZone)) (om \(TimeText.duration(span)))"
                } else {
                    dep = "\(TimeText.weekday(depletesAt, timeZone: timeZone)) "
                        + "\(TimeText.clockOnly(depletesAt, timeZone: timeZone)) (om \(TimeText.duration(span)))"
                }
            } else {
                dep = "snart"
            }
            return "\(pct) använt → slut \(dep)"
        }

        // Safe / Tight
        let projected = window.projectedPctAtReset ?? window.usedPct ?? 0
        return "\(pct) använt → ~\(TimeText.percent(projected)) % vid reset"
    }
}
