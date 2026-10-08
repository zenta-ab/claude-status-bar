namespace ClaudeStatusBar.Model;

/// <summary>
/// Which account the panel shows, remembered by SLOT ID rather than list position
/// (docs/multi-account.md "Account set"): the account list is rebuilt on every add / remove /
/// re-login, so an index remembered across a rebuild would silently point at a different account.
/// Pure, so the rule is unit-tested without a window.
/// </summary>
public static class AccountFocus
{
    /// <summary>The position of `slot` in the current list, or null when it is not there (any more).</summary>
    public static int? IndexOfSlot(IReadOnlyList<string?> slots, string? slot)
    {
        if (slot is null) return null;
        for (int i = 0; i < slots.Count; i++)
            if (string.Equals(slots[i], slot, StringComparison.Ordinal)) return i;
        return null;
    }

    /// <summary>
    /// The panel's account for this render: the explicitly chosen slot if it still exists and is
    /// not a duplicate (a duplicate has no icon of its own to have been chosen through), else the
    /// display mode's default. KeptSlot is what the caller should remember for next time -- null
    /// when the explicit choice no longer applies.
    /// </summary>
    public static (int PanelIndex, string? KeptSlot) Resolve(
        IReadOnlyList<string?> slots, IReadOnlyList<bool> isDuplicate, string? explicitSlot, int defaultIndex)
    {
        if (IndexOfSlot(slots, explicitSlot) is { } idx && !isDuplicate[idx]) return (idx, explicitSlot);
        return (Math.Clamp(defaultIndex, 0, Math.Max(0, slots.Count - 1)), null);
    }
}
