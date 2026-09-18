using ClaudeStatusBar.Config;

namespace ClaudeStatusBar.Model;

/// <summary>
/// Pure selection logic for docs/multi-account.md "Display": which accounts get a tray icon.
/// Kept free of AccountRuntime/NotifyIcon so it is directly unit-testable -- an account is
/// nothing more than "enabled?" plus its last QuotaView here. StatusBarApplicationContext.cs is
/// the only caller in the running app; demo mode's synthetic multi-account frames go through the
/// exact same path.
/// </summary>
public static class AccountDisplayPlan
{
    public readonly record struct Candidate(bool Enabled, QuotaView View);

    /// <summary>
    /// docs/multi-account.md "binding": one account, the one closest to being blocked --
    /// highest severity first, ties broken by the shorter time to depletion, then by config
    /// order (this list's position). Accounts with no real verdict yet (Measuring, or
    /// Freshness.Unknown) rank below every account that has one, UNLESS none do -- then the same
    /// severity/depletion/order rules just apply within that group instead. Disabled accounts
    /// are never eligible. Null only when every candidate is disabled.
    /// </summary>
    public static int? SelectBinding(IReadOnlyList<Candidate> accounts, DateTimeOffset now)
    {
        int best = -1;
        int bestTier = -1;
        int bestSeverity = -1;
        TimeSpan bestDepletion = TimeSpan.MaxValue;

        for (int i = 0; i < accounts.Count; i++)
        {
            Candidate c = accounts[i];
            if (!c.Enabled) continue;

            int tier = HasRealVerdict(c.View) ? 1 : 0;
            int severity = (int)c.View.IconSeverity;
            TimeSpan depletion = TimeToDepletion(c.View, now);

            if (best == -1 || IsBetter(tier, severity, depletion, bestTier, bestSeverity, bestDepletion))
            {
                best = i;
                bestTier = tier;
                bestSeverity = severity;
                bestDepletion = depletion;
            }
        }

        return best == -1 ? null : best;
    }

    /// <summary>docs/multi-account.md "perAccount": one icon per enabled account, in config order, capped at maxIcons.</summary>
    public static IReadOnlyList<int> SelectPerAccount(IReadOnlyList<Candidate> accounts, int maxIcons)
    {
        var result = new List<int>();
        for (int i = 0; i < accounts.Count && result.Count < maxIcons; i++)
            if (accounts[i].Enabled) result.Add(i);
        return result;
    }

    /// <summary>Which account indices (into `accounts`) should have a tray icon, for either display mode.</summary>
    public static IReadOnlyList<int> SelectIconAccounts(IReadOnlyList<Candidate> accounts, AccountDisplayMode mode, int maxIcons, DateTimeOffset now)
    {
        if (mode == AccountDisplayMode.Binding)
        {
            int? idx = SelectBinding(accounts, now);
            return idx is { } i ? new[] { i } : Array.Empty<int>();
        }
        return SelectPerAccount(accounts, maxIcons);
    }

    static bool HasRealVerdict(QuotaView view) =>
        view.Freshness != Freshness.Unknown && view.IconSeverity != QuotaState.Measuring;

    /// <summary>Higher tier wins; then higher severity; then strictly shorter depletion. An exact tie on all three keeps whichever was already best -- i.e. the earlier (lower) config-order index, since callers scan ascending.</summary>
    static bool IsBetter(int tier, int severity, TimeSpan depletion, int bestTier, int bestSeverity, TimeSpan bestDepletion)
    {
        if (tier != bestTier) return tier > bestTier;
        if (severity != bestSeverity) return severity > bestSeverity;
        return depletion < bestDepletion;
    }

    /// <summary>
    /// Same "driver" rule PanelText/IconSlot use for which window's numbers to quote (the more
    /// severe window, session wins an exact tie). Spent counts as zero (already happened);
    /// windows with no real depletion moment (Safe/Tight/Measuring, or DryEarly with no
    /// DepletesAt yet) rank last via TimeSpan.MaxValue -- they never win a depletion tie-break,
    /// only fall through to config order.
    /// </summary>
    static TimeSpan TimeToDepletion(QuotaView view, DateTimeOffset now)
    {
        WindowView driver = (int)view.Session.State >= (int)view.Weekly.State ? view.Session : view.Weekly;
        return driver.State switch
        {
            QuotaState.Spent => TimeSpan.Zero,
            QuotaState.DryEarly when driver.DepletesAt is { } d => d > now ? d - now : TimeSpan.Zero,
            _ => TimeSpan.MaxValue,
        };
    }
}
