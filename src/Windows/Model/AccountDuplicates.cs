namespace ClaudeStatusBar.Model;

/// <summary>
/// Pure duplicate-account detection (docs/multi-account.md "Duplicate accounts"): two enabled
/// accounts are the SAME login when their AccountIdentity.StateKey (accountUuid+organizationUuid,
/// never accountUuid alone -- see that type's doc comment) is equal. Kept free of AccountRuntime/
/// NotifyIcon so the grouping rule itself is directly unit-testable, the same way
/// Model/AccountDisplayPlan.cs is -- StatusBarApplicationContext.RefreshDuplicates is the only
/// caller in the running app, applying this result to each AccountRuntime.SetDuplicate.
///
/// The live bug this exists to fix: a follower entry (configDir null, "whatever the user is
/// logged into") and a pinned entry can resolve to the same identity -- most commonly because the
/// user logged the SAME account into both, or logged their terminal into an account already
/// pinned to its own config directory. Before this, that produced two identical tray icons, two
/// identical panel rows, and a second claude.exe child polling the exact same account for no
/// reason -- silently, nothing told the user anything was wrong.
/// </summary>
public static class AccountDuplicates
{
    /// <summary>One account's duplicate verdict: whether it is a duplicate, and if so, of which EARLIER account (by index into the same list this was computed from).</summary>
    public readonly record struct Result(bool IsDuplicate, int? DuplicateOfIndex);

    /// <summary>
    /// For each account (by its position in `stateKeys`, i.e. config order), whether it
    /// duplicates an earlier account with the same StateKey. Only the FIRST account in each
    /// group of equal keys is kept (docs/multi-account.md: "keep polling and showing only the
    /// FIRST account in config order"); every later one in that group is a duplicate of it.
    ///
    /// A null key (identity not known yet, or never logged in) is never treated as a duplicate of
    /// anything and never causes a later account to be marked a duplicate of it -- "not known yet"
    /// is not the same identity as anything, including another "not known yet".
    /// </summary>
    public static IReadOnlyList<Result> Detect(IReadOnlyList<string?> stateKeys)
    {
        var results = new Result[stateKeys.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < stateKeys.Count; i++)
        {
            string? key = stateKeys[i];
            if (key is null) { results[i] = new Result(false, null); continue; }

            results[i] = seen.TryGetValue(key, out int firstIndex)
                ? new Result(true, firstIndex)
                : new Result(false, null);
            if (!results[i].IsDuplicate) seen[key] = i;
        }
        return results;
    }
}
