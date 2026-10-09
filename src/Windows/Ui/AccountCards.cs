using System.Drawing;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Ui;

/// <summary>One thin bar of a card: "Session" / "Vecka", how full it is, and the text beside it ("56 % · reset 15:04").</summary>
public sealed record CardBar(string Caption, double Fraction, string Text, PanelColorRole Role);

/// <summary>
/// One compact account card of the combined panel (docs/multi-account.md "All accounts in one panel"):
/// label, the "plan · organisation" line, the short verdict in its colour (the status box's own first
/// line, so the wording is the same), the two bars, and a one-line note only when the account is not
/// Live. OfferRelogin adds the "Logga in igen" button (NeedsLogin).
/// </summary>
public sealed record AccountCard(
    string Slot, string Title, string? Subtitle,
    string Verdict, PanelColorRole VerdictRole,
    CardBar Session, CardBar Week,
    string? Note, PanelColorRole NoteRole, bool OfferRelogin);

/// <summary>What the card composer needs about one account (a snapshot taken from the runtime).</summary>
public sealed record CardInput(string Slot, string Title, string? Subtitle, QuotaView View);

/// <summary>
/// Pure composition of the combined panel's cards and their order -- no drawing, so the wording,
/// the day-aware times and the ordering are tested without a window.
/// </summary>
public static class AccountCards
{
    const string AwaitingResetText = "Nytt fönster väntas";
    const string WindowInactiveText = "Inget förbrukat ännu";

    public static AccountCard Compose(CardInput input, DateTimeOffset now, TimeZoneInfo tz)
    {
        QuotaView view = input.View;
        StatusBoxText box = PanelText.Compose(view, now, tz).StatusBox;

        (string? note, PanelColorRole noteRole) = NoteFor(view, now, tz);
        return new AccountCard(
            input.Slot, input.Title, input.Subtitle,
            box.Line1, box.Role,
            Bar("Session", view.Session, now, tz), Bar("Vecka", view.Weekly, now, tz),
            note, noteRole, OfferRelogin: view.NeedsLogin);
    }


    static CardBar Bar(string caption, WindowView w, DateTimeOffset now, TimeZoneInfo tz)
    {
        PanelColorRole role = w.State switch
        {
            QuotaState.Safe => PanelColorRole.Safe,
            QuotaState.Tight => PanelColorRole.Tight,
            QuotaState.DryEarly => PanelColorRole.Crit,
            QuotaState.Spent => PanelColorRole.Dead,
            _ => PanelColorRole.Measuring,
        };

        bool awaiting = w.State == QuotaState.Measuring && w.MeasuringReason == AwaitingResetText;
        if (awaiting) return new CardBar(caption, 0.0, "väntar på nytt fönster", role);
        if (w.UsedPct is not { } pct) return new CardBar(caption, 0.0, "–", role);

        double fraction = Math.Clamp(pct / 100.0, 0.0, 1.0);
        string pctText = $"{pct:F0} %";

        if (w.State == QuotaState.Spent && w.ResetsAt is { } reopens)
            return new CardBar(caption, 1.0, $"100 % · öppnar {TimeText.ClockWithDay(reopens, now, tz)}", role);
        if (w.ResetsAt is { } resets)
            return new CardBar(caption, fraction, $"{pctText} · reset {TimeText.ClockWithDay(resets, now, tz)}", role);
        return new CardBar(caption, fraction, w.MeasuringReason == WindowInactiveText ? $"{pctText} · inget förbrukat" : pctText, role);
    }

    /// <summary>The one-line state note: null while Live, otherwise why not.</summary>
    static (string? Note, PanelColorRole Role) NoteFor(QuotaView view, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (view.Loading) return ("Hämtar…", PanelColorRole.Measuring);
        if (view.NeedsLogin) return ("Inte inloggad", PanelColorRole.Crit);
        if (view.Freshness == Freshness.Unknown)
            return (view.LastSuccessAt is { } last
                ? $"Senast avläst {TimeText.PointInTime(last, now, tz)}"
                : "Väntar på första avläsningen…", PanelColorRole.Unknown);
        if (view.Freshness == Freshness.Stale)
            return (view.LastChangedAt is { } changed
                ? $"Inaktuell — senast ändrad för {TimeText.Duration(now - changed)} sedan"
                : "Inaktuell", PanelColorRole.Tight);
        return (null, PanelColorRole.Measuring);
    }

    /// <summary>
    /// The order of the cards: the accounts that have a tray icon, in the order their icons appear on
    /// screen left to right (the icon rectangles PanelAnchor reads); then the accounts without an icon,
    /// in config order. When ANY icon's rectangle cannot be read (an icon in the overflow flyout, the
    /// shell not answering), the icon group falls back to config order as a whole rather than mixing
    /// two orderings. Slots in `icons` that are not in `configOrder` are ignored.
    /// </summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<(string Slot, Rectangle? Rect)> icons, IReadOnlyList<string> configOrder)
    {
        var index = configOrder.Select((slot, i) => (slot, i)).ToDictionary(t => t.slot, t => t.i, StringComparer.Ordinal);
        var withIcon = icons.Where(i => index.ContainsKey(i.Slot)).ToList();

        IEnumerable<string> iconGroup = withIcon.Count > 0 && withIcon.All(i => i.Rect is not null)
            ? withIcon.OrderBy(i => i.Rect!.Value.Left).ThenBy(i => i.Rect!.Value.Top).ThenBy(i => index[i.Slot]).Select(i => i.Slot)
            : withIcon.OrderBy(i => index[i.Slot]).Select(i => i.Slot);

        var ordered = iconGroup.ToList();
        var seen = new HashSet<string>(ordered, StringComparer.Ordinal);
        ordered.AddRange(configOrder.Where(slot => !seen.Contains(slot)));
        return ordered;
    }
}
