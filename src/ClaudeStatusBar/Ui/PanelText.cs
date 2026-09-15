using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>Which state colour a status-box verdict (or window section) should render in -- PanelForm maps this to Icons.Palette.</summary>
public enum PanelColorRole { Safe, Tight, Crit, Dead, Measuring, Unknown }

/// <summary>
/// Line 1 is the verdict, bold, in Role's colour. Line 2 always states when (a
/// reset clock+countdown, or -- DryEarly -- a shortfall before that reset).
/// Line3 is the Stale freshness note, shown in addition to whatever the verdict
/// already says. SecondaryLine is the one extra line for the window that is NOT
/// driving the box, when that window is itself Tight/DryEarly/Spent (docs/panel-v2.md, item 2).
/// </summary>
public sealed record StatusBoxText(string Line1, string Line2, string? Line3, string? SecondaryLine, PanelColorRole Role);

/// <summary>Everything one window's section (docs/panel-v2.md, item 3) shows in text. Bar fractions are drawn straight from WindowView by PanelForm -- this is text only.</summary>
public sealed record WindowSectionText(string Title, string ResetHeader, string TidLabel, string KvotLabel);

public sealed record PanelTextResult(StatusBoxText StatusBox, WindowSectionText Session, WindowSectionText Weekly);

/// <summary>
/// The panel-v2 "answer first" text layer (docs/panel-v2.md): pure function of
/// (QuotaView, now, tz) -> every string the panel shows, no drawing. Kept separate
/// from PanelForm so the exact wording is testable without a Graphics context.
/// Every time value flows through TimeText -- nothing here does its own minute
/// arithmetic for display.
/// </summary>
public static class PanelText
{
    // WindowView.MeasuringReason is a plain string (not an enum), sourced from
    // QuotaModel.ReasonText/DemoQuotaSource.ReasonText -- these two literals must
    // keep matching those switches verbatim; it's the only way to recognize a
    // reason worth surfacing by name instead of folding it into the generic
    // "X % använt · återställs ..." line.
    const string AwaitingResetText = "Nytt fönster väntas";
    const string TooEarlyInWeekText = "För tidigt i veckan — väntar på ett helt dygn";

    public static PanelTextResult Compose(QuotaView view, DateTimeOffset now, TimeZoneInfo tz) => new(
        ComposeStatusBox(view, now, tz),
        ComposeSection("AKTUELL SESSION", WindowKind.Session, view.Session, now, tz),
        ComposeSection("VECKA", WindowKind.Weekly, view.Weekly, now, tz));

    // ---- status box (docs/panel-v2.md, item 2) ----

    static StatusBoxText ComposeStatusBox(QuotaView view, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (view.Freshness == Freshness.Unknown)
        {
            string line2 = view.LastPollAt is { } lastPoll
                ? $"Senast avläst {TimeText.PointInTime(lastPoll, now, tz)} · försöker igen"
                : "Väntar på första avläsningen… · försöker igen";
            return new StatusBoxText("Kan inte läsa kvoten", line2, null, null, PanelColorRole.Unknown);
        }

        string? staleLine3 = view.Freshness == Freshness.Stale && view.LastChangedAt is { } changed
            ? $"Datan kan vara inaktuell — senast ändrad för {TimeText.Duration(now - changed)} sedan"
            : null;

        if (view.BlockedUntil is { } blockedUntil)
        {
            bool weeklyDrives = view.Weekly.State == QuotaState.Spent
                && (view.Session.State != QuotaState.Spent || view.Weekly.ResetsAt >= view.Session.ResetsAt);
            string word = weeklyDrives ? "Veckokvoten" : "Kvoten";
            string line1 = $"{word} är slut";
            string line2 = $"Öppnar igen {TimeText.Reset(blockedUntil, now, tz, forceWeekday: weeklyDrives)}";
            string? secondary = SecondaryLine(view, drivingIsSession: !weeklyDrives, now, tz);
            return new StatusBoxText(line1, line2, staleLine3, secondary, PanelColorRole.Dead);
        }

        if (view.IconSeverity == QuotaState.Measuring)
        {
            StatusBoxText measuring = ComposeMeasuring(view, now, tz);
            return measuring with { Line3 = staleLine3 };
        }

        bool sessionDrives = (int)view.Session.State >= (int)view.Weekly.State;
        WindowView driver = sessionDrives ? view.Session : view.Weekly;
        string kvotWordLower = sessionDrives ? "kvoten" : "veckokvoten";
        string kvotWordCap = sessionDrives ? "Kvoten" : "Veckokvoten";
        string subject = sessionDrives ? "Sessionen" : "Veckan";
        bool driverIsWeekly = !sessionDrives;

        StatusBoxText verdict = driver.State switch
        {
            QuotaState.DryEarly => ComposeDryEarly(driver, kvotWordCap, driverIsWeekly, now, tz),
            QuotaState.Tight => ComposeSafeOrTight($"Tajt — {kvotWordLower} räcker precis", subject, driver, driverIsWeekly, PanelColorRole.Tight, now, tz),
            _ => ComposeSafeOrTight($"✓ {kvotWordCap} räcker till reset", subject, driver, driverIsWeekly, PanelColorRole.Safe, now, tz),
        };

        string? secondaryLine = SecondaryLine(view, sessionDrives, now, tz);
        return verdict with { Line3 = staleLine3, SecondaryLine = secondaryLine };
    }

    static StatusBoxText ComposeSafeOrTight(string line1, string subject, WindowView driver, bool driverIsWeekly, PanelColorRole role, DateTimeOffset now, TimeZoneInfo tz)
    {
        double proj = driver.ProjectedPctAtReset ?? driver.UsedPct ?? 0.0;
        string resetText = driver.ResetsAt is { } r ? TimeText.Reset(r, now, tz, driverIsWeekly) : "okänt";
        string line2 = $"{subject} landar på ~{proj:F0} % · återställs {resetText}";
        return new StatusBoxText(line1, line2, null, null, role);
    }

    static StatusBoxText ComposeDryEarly(WindowView driver, string kvotWordCap, bool driverIsWeekly, DateTimeOffset now, TimeZoneInfo tz)
    {
        // "Running out always states both when and how long until" -- today AND any
        // later day (docs/panel-v2.md, "Time text"): TimeText.Depletion carries the
        // "(om ...)" duration regardless of which.
        string depletionText = driver.DepletesAt is { } dep ? TimeText.Depletion(dep, now, tz) : "snart";
        string line1 = $"⚠ {kvotWordCap} tar slut {depletionText}";

        string resetClock = driver.ResetsAt is { } r
            ? (driverIsWeekly ? $"{TimeText.Weekday(r, tz)} kl {TimeText.ClockOnly(r, tz)}" : $"kl {TimeText.ClockOnly(r, tz)}")
            : "okänt";
        string line2 = $"{TimeText.Duration(driver.Shortfall)} före reset {resetClock}, om du fortsätter i samma takt";

        return new StatusBoxText(line1, line2, null, null, PanelColorRole.Crit);
    }

    /// <summary>
    /// Both windows are Measuring (the only way the overall severity IS Measuring).
    /// A window carrying one of the two "special" reasons (awaiting the next window,
    /// or too early in the week) is surfaced by name; otherwise the row falls back to
    /// the generic "X % använt · återställs ..." form.
    /// </summary>
    static StatusBoxText ComposeMeasuring(QuotaView view, DateTimeOffset now, TimeZoneInfo tz)
    {
        const string line1 = "Mäter takt…";

        bool weeklySpecial = view.Weekly.MeasuringReason is AwaitingResetText or TooEarlyInWeekText;
        bool sessionSpecial = view.Session.MeasuringReason is AwaitingResetText or TooEarlyInWeekText;
        (WindowView driver, bool isWeekly) = weeklySpecial ? (view.Weekly, true)
            : sessionSpecial ? (view.Session, false)
            : (view.Session, false); // no special reason on either side: session is the default driver

        if (driver.MeasuringReason == AwaitingResetText)
        {
            // The window's own reset has already passed and no new one has been
            // confirmed yet -- there is no sane future clock to count down to, so
            // the reason stands alone rather than pairing it with a stale reset time.
            return new StatusBoxText(line1, driver.MeasuringReason, null, null, PanelColorRole.Measuring);
        }

        string pctPart = driver.UsedPct is { } u ? $"{u:F0} % använt" : "Hämtar…";
        string resetText = driver.ResetsAt is { } r ? TimeText.Reset(r, now, tz, forceWeekday: isWeekly) : "okänt";
        string line2 = driver.MeasuringReason == TooEarlyInWeekText
            ? $"{pctPart} · {driver.MeasuringReason} · återställs {resetText}"
            : $"{pctPart} · återställs {resetText}";

        return new StatusBoxText(line1, line2, null, null, PanelColorRole.Measuring);
    }

    /// <summary>The one extra line for whichever window is NOT driving the box, only when it is itself Tight/DryEarly/Spent (docs/panel-v2.md, item 2).</summary>
    static string? SecondaryLine(QuotaView view, bool drivingIsSession, DateTimeOffset now, TimeZoneInfo tz)
    {
        WindowView other = drivingIsSession ? view.Weekly : view.Session;
        bool otherIsWeekly = drivingIsSession;
        string subject = otherIsWeekly ? "Veckokvoten" : "Sessionens kvot";

        return other.State switch
        {
            QuotaState.Tight when other.ResetsAt is { } r =>
                $"Dessutom tajt: {subject} räcker precis · återställs {TimeText.Reset(r, now, tz, otherIsWeekly)}",
            QuotaState.DryEarly when other.DepletesAt is { } dep =>
                $"Dessutom: {subject} tar slut {TimeText.Depletion(dep, now, tz)}",
            QuotaState.Spent when other.ResetsAt is { } r =>
                $"Dessutom: {subject} är slut · öppnar {TimeText.Reset(r, now, tz, otherIsWeekly)}",
            _ => null,
        };
    }

    // ---- window sections (docs/panel-v2.md, item 3) ----

    static WindowSectionText ComposeSection(string title, WindowKind kind, WindowView w, DateTimeOffset now, TimeZoneInfo tz)
    {
        bool isWeekly = kind == WindowKind.Weekly;
        bool awaitingReset = w.State == QuotaState.Measuring && w.MeasuringReason == AwaitingResetText;

        string resetHeader;
        string tidLabel;

        if (awaitingReset)
        {
            // The window's own reset has already passed and no new one has been
            // confirmed yet -- there is nothing sane to count down to or measure
            // elapsed time against (item 2's "must never show a negative/zero
            // countdown, or the old window's usage as if current" rule). The Tid bar
            // still draws full and the Kvot bar draws empty to match (PanelForm).
            resetHeader = "väntar på nytt fönster";
            tidLabel = "fönstret är slut";
        }
        else
        {
            resetHeader = w.ResetsAt is { } r
                ? $"återställs {TimeText.CountdownFragment(r - now)} · {(isWeekly ? $"{TimeText.Weekday(r, tz)} {TimeText.ClockOnly(r, tz)}" : $"kl {TimeText.ClockOnly(r, tz)}")}"
                : "Väntar på data…";

            string windowLenText = isWeekly ? "veckan" : TimeText.Duration(TimeSpan.FromMinutes(w.WindowMinutes));
            double elapsedFrac = w.ResetsAt is { } r2
                ? Math.Clamp((w.WindowMinutes - (r2 - now).TotalMinutes) / w.WindowMinutes, 0.0, 1.0)
                : 0.0;
            tidLabel = $"{elapsedFrac * 100:F0} % av {windowLenText} har gått";
        }

        return new WindowSectionText(title, resetHeader, tidLabel, BuildKvotLabel(w, now, tz));
    }

    static string BuildKvotLabel(WindowView w, DateTimeOffset now, TimeZoneInfo tz)
    {
        string pct = w.UsedPct is { } u ? $"{u:F0} %" : "–";

        if (w.State == QuotaState.Measuring)
        {
            // Gated on the reason text, same two literals as the status box, not on
            // State alone -- the generic "för tidigt för prognos" wording covers
            // every other reason (rollover, too-little-usage, clock jump, no data).
            if (w.MeasuringReason is AwaitingResetText or TooEarlyInWeekText) return w.MeasuringReason;
            return $"{pct} använt · för tidigt för prognos";
        }

        if (w.State == QuotaState.Spent) return "100 % · slut";

        if (w.State == QuotaState.DryEarly)
        {
            // Kvot-bar depletion wording differs from the status box's by design (docs/
            // panel-v2.md's implementation notes): it drops "kl" before the weekday
            // ("slut sön 10:44 (om ...)"), so it doesn't route through TimeText.Depletion
            // (which always keeps "kl"). Still shares the same "nu" collapse for a
            // depletion at or before now.
            string dep = w.DepletesAt is { } d
                ? (d - now).TotalSeconds <= 0
                    ? "nu"
                    : (TimeText.IsToday(d, now, tz)
                        ? $"kl {TimeText.ClockOnly(d, tz)} (om {TimeText.Duration(d - now)})"
                        : $"{TimeText.Weekday(d, tz)} {TimeText.ClockOnly(d, tz)} (om {TimeText.Duration(d - now)})")
                : "snart";
            return $"{pct} använt → slut {dep}";
        }

        // Safe / Tight
        double proj = w.ProjectedPctAtReset ?? w.UsedPct ?? 0.0;
        return $"{pct} använt → ~{proj:F0} % vid reset";
    }
}
