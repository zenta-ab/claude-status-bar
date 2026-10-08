using ClaudeStatusBar.Data;

namespace ClaudeStatusBar.Model;

public enum LoginOutcome
{
    /// <summary>A new account: mark the slot active and append it.</summary>
    Adopt,
    /// <summary>A re-login of the same account: the entry now points at the new slot, the old slot is retired.</summary>
    Swap,
    /// <summary>The login did not take (not logged in, or not a claude.ai login): retire the new slot.</summary>
    RejectNotLoggedIn,
    /// <summary>Add flow, but this identity is already tracked: retire the new slot.</summary>
    RejectDuplicate,
    /// <summary>Re-login flow, but the user logged in as somebody else: nothing changes for the account, the new slot is retired.</summary>
    RejectDifferentAccount,
    /// <summary>Re-login flow, but the account's own identity cannot be read, so "same account" cannot be confirmed: nothing changes, the new slot is retired and deleted.</summary>
    RejectUnverifiable,
}

/// <summary>An account the app already tracks, as the login decision needs it: its slot, the label shown to the user, and its identity (null when unreadable).</summary>
public sealed record TrackedAccount(string Slot, string Label, AccountIdentity? Identity);

/// <summary>
/// What to do with a freshly logged-in slot. MatchedLabel names the tracked account the identity
/// equals (Duplicate / DifferentAccount); NewLabel is the label for the identity that logged in,
/// from the AccountLabel ladder -- never the raw personal-organisation name.
/// </summary>
public sealed record LoginDecision(LoginOutcome Outcome, string? MatchedLabel, string NewLabel);

/// <summary>
/// Pure decision for docs/multi-account.md "Login flow". Identity comes ONLY from the new slot's
/// .claude.json (AccountIdentity.ReadFrom) -- `auth status` is consulted just for "did a claude.ai
/// login happen at all", and its raw output (which holds the email) never reaches here.
///
///   Add:      new identity -> Adopt; already tracked -> RejectDuplicate.
///   Re-login: the identity is the same account -> Swap; a different tracked account, or one that
///             is not tracked -> RejectDifferentAccount (nothing changes for the account).
///             If the account's own identity is unreadable it cannot be confirmed that this is the same
///             account, so nothing changes either (RejectUnverifiable): the user can remove it and add it again.
/// </summary>
public static class LoginFlowDecision
{
    public static LoginDecision Decide(
        AuthStatus? status,
        AccountIdentity? newIdentity,
        IReadOnlyList<TrackedAccount> tracked,
        string? reloginSlot)
    {
        if (status is not { IsClaudeAiLogin: true } || newIdentity is null)
            return new LoginDecision(LoginOutcome.RejectNotLoggedIn, null, "");

        string newLabel = AccountLabel.Resolve(new AccountLabelInput(null, newIdentity, null), tracked.Count);
        TrackedAccount? same = tracked.FirstOrDefault(t => t.Identity is { } id && id.StateKey == newIdentity.StateKey);
        TrackedAccount? target = reloginSlot is null ? null : tracked.FirstOrDefault(t => t.Slot == reloginSlot);

        if (target is null) // add flow (or the account to re-login vanished meanwhile: treat as a plain add)
            return same is null
                ? new LoginDecision(LoginOutcome.Adopt, null, newLabel)
                : new LoginDecision(LoginOutcome.RejectDuplicate, same.Label, newLabel);

        if (same is not null)
            return same.Slot == target.Slot
                ? new LoginDecision(LoginOutcome.Swap, same.Label, newLabel)
                : new LoginDecision(LoginOutcome.RejectDifferentAccount, same.Label, newLabel);

        return target.Identity is null
            ? new LoginDecision(LoginOutcome.RejectUnverifiable, target.Label, newLabel)
            : new LoginDecision(LoginOutcome.RejectDifferentAccount, null, newLabel);
    }
}
