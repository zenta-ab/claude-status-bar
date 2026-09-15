using System.Drawing;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Icons;

/// <summary>
/// Everything GaugeRenderer.RenderQuota needs, plus everything IconSlot needs to
/// decide whether a re-render is necessary -- a plain record struct gets
/// structural equality for free, so the params ARE the change-detection
/// fingerprint; there is no separate comparison to keep in sync.
///
/// Built once per QuotaView interpretation (docs/forecast-and-states.md, "Icon").
/// Every forecast-derived field (ForecastFrac) is gated on the window's committed
/// State, never on the nullability of the underlying forecast numbers -- a window
/// can carry non-null RatePctPerMin/ProjectedPctAtReset while still Measuring
/// (the model does not clear them), and the icon must never show a wedge for that.
/// </summary>
public readonly record struct QuotaIconParams(
    bool Outline,
    bool Exhausted,
    double RingFrac,
    double PieFrac,
    double ForecastFrac,
    double SessionForecastFrac,
    int RingArgb,
    int PieArgb,
    bool Stale,
    int Px,
    bool TaskbarDark)
{
    public static QuotaIconParams Build(QuotaView view, int px, bool taskbarDark, DateTimeOffset utcNow)
    {
        if (view.Freshness == Freshness.Unknown)
            return new QuotaIconParams(Outline: true, false, 0, 0, 0, 0, 0, 0, false, px, taskbarDark);

        bool stale = view.Freshness == Freshness.Stale;

        if (view.BlockedUntil is { } blockedUntil)
        {
            double countdown = Quantize(CountdownFrac(view, blockedUntil, utcNow));
            return new QuotaIconParams(
                Outline: false, Exhausted: true, RingFrac: 1.0, PieFrac: countdown, ForecastFrac: 0, SessionForecastFrac: countdown,
                RingArgb: Palette.Dead.ToArgb(), PieArgb: Palette.Dead.ToArgb(), Stale: stale, px, taskbarDark);
        }

        double ringFrac = Quantize((view.Weekly.UsedPct ?? 0.0) / 100.0);
        double pieFrac = Quantize((view.Session.UsedPct ?? 0.0) / 100.0);
        Color ringColor = Palette.ColorForState(view.Weekly.State);
        Color pieColor = Palette.ColorForState(view.Session.State);

        // Gated on State, never on ProjectedPctAtReset's nullability: Measuring must
        // never draw a wedge even if the model still carries a stale forecast number.
        double forecastFrac = view.Weekly.State != QuotaState.Measuring && view.Weekly.ProjectedPctAtReset is { } projected
            ? Quantize(Math.Min(1.0, projected / 100.0))
            : ringFrac;

        // Session pie forecast sector (product requirement, tray icon): same gating rule,
        // applied to the inner pie instead of the outer ring. Also withheld once Spent --
        // "projected past 100%" is meaningless once the window is already exhausted.
        double sessionForecastFrac = view.Session.State is not (QuotaState.Measuring or QuotaState.Spent)
            && view.Session.ProjectedPctAtReset is { } sessionProjected
            ? Quantize(Math.Min(1.0, sessionProjected / 100.0))
            : pieFrac;

        return new QuotaIconParams(
            Outline: false, Exhausted: false, ringFrac, pieFrac, forecastFrac, sessionForecastFrac,
            ringColor.ToArgb(), pieColor.ToArgb(), stale, px, taskbarDark);
    }

    static double CountdownFrac(QuotaView view, DateTimeOffset blockedUntil, DateTimeOffset utcNow)
    {
        double windowMinutes = QuotaWindows.WeeklyMinutes;
        if (view.Session.State == QuotaState.Spent && view.Session.ResetsAt == blockedUntil)
            windowMinutes = QuotaWindows.SessionMinutes;
        else if (view.Weekly.State == QuotaState.Spent && view.Weekly.ResetsAt == blockedUntil)
            windowMinutes = QuotaWindows.WeeklyMinutes;

        double remaining = (blockedUntil - utcNow).TotalMinutes;
        return Math.Clamp(remaining / windowMinutes, 0.0, 1.0);
    }

    static double Quantize(double frac) => Math.Round(Math.Clamp(frac, 0.0, 1.0) * 360.0) / 360.0;
}
