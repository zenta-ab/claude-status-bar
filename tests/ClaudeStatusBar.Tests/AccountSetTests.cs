using ClaudeStatusBar.Config;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// The account set is rebuilt on every add / remove / re-login (docs/multi-account.md "Account
/// set"), so everything that must survive a rebuild is remembered by slot id: panel focus
/// (Model/AccountFocus) and which icons exist (Model/AccountDisplayPlan.PlanTray, including the
/// zero-accounts state, docs/multi-account.md "Zero accounts").
/// </summary>
public class AccountSetTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

    static QuotaView LiveView() => new(
        WindowView.Empty(WindowKind.Session, QuotaWindows.SessionMinutes),
        WindowView.Empty(WindowKind.Weekly, QuotaWindows.WeeklyMinutes),
        Freshness.Live, Now, Now, TimeSpan.FromSeconds(30), null, QuotaState.Safe, null);

    // ---- focus by slot id ----

    [Fact]
    public void Focus_FollowsTheSlot_WhenAnAccountIsInsertedAheadOfIt()
    {
        string?[] before = { "a1b2c3d4", "e5f6a7b8" };
        (int idxBefore, string? kept) = AccountFocus.Resolve(before, new[] { false, false }, "e5f6a7b8", defaultIndex: 0);
        Assert.Equal(1, idxBefore);

        // A new account lands first and shifts everything by one: the same slot, a different index.
        string?[] after = { "11112222", "a1b2c3d4", "e5f6a7b8" };
        (int idxAfter, string? keptAfter) = AccountFocus.Resolve(after, new[] { false, false, false }, kept, defaultIndex: 0);

        Assert.Equal(2, idxAfter);
        Assert.Equal("e5f6a7b8", after[idxAfter]);
        Assert.Equal("e5f6a7b8", keptAfter);
    }

    [Fact]
    public void Focus_FollowsTheSlot_WhenAnAccountAheadOfItIsRemoved()
    {
        string?[] after = { "e5f6a7b8" };

        (int idx, string? kept) = AccountFocus.Resolve(after, new[] { false }, "e5f6a7b8", defaultIndex: 0);

        Assert.Equal(0, idx);
        Assert.Equal("e5f6a7b8", kept);
    }

    [Fact]
    public void Focus_FollowsTheSlot_WhenAReloginSwapsTheEntryToANewSlot_OnceTheCallerRepointsIt()
    {
        // StatusBarApplicationContext.SwapSlotAsync repoints the remembered slot to the new one.
        string?[] after = { "a1b2c3d4", "99999999" };

        (int idx, _) = AccountFocus.Resolve(after, new[] { false, false }, "99999999", defaultIndex: 0);

        Assert.Equal(1, idx);
    }

    [Fact]
    public void Focus_FallsBackToTheDefault_AndForgets_WhenTheFocusedSlotIsGone()
    {
        string?[] slots = { "a1b2c3d4", "e5f6a7b8" };

        (int idx, string? kept) = AccountFocus.Resolve(slots, new[] { false, false }, "00000000", defaultIndex: 1);

        Assert.Equal(1, idx);
        Assert.Null(kept);
    }

    [Fact]
    public void Focus_NeverPinsTheDuplicate_FallsBackToTheDefault()
    {
        string?[] slots = { "a1b2c3d4", "e5f6a7b8" };

        (int idx, string? kept) = AccountFocus.Resolve(slots, new[] { false, true }, "e5f6a7b8", defaultIndex: 0);

        Assert.Equal(0, idx);
        Assert.Null(kept);
    }

    [Fact]
    public void Focus_WithNothingChosen_UsesTheDefault()
    {
        (int idx, string? kept) = AccountFocus.Resolve(new string?[] { "a1b2c3d4", "e5f6a7b8" }, new[] { false, false }, null, defaultIndex: 1);

        Assert.Equal(1, idx);
        Assert.Null(kept);
    }

    [Fact]
    public void IndexOfSlot_IsExactAndCaseSensitive()
    {
        string?[] slots = { "a1b2c3d4", "4" };

        Assert.Equal(1, AccountFocus.IndexOfSlot(slots, "4"));
        Assert.Null(AccountFocus.IndexOfSlot(slots, "A1B2C3D4"));
        Assert.Null(AccountFocus.IndexOfSlot(slots, null));
    }

    // ---- zero accounts ----

    [Fact]
    public void PlanTray_NoAccounts_IsExactlyOneZeroAccountsIcon()
    {
        AccountDisplayPlan.TrayPlan plan = AccountDisplayPlan.PlanTray(
            Array.Empty<AccountDisplayPlan.Candidate>(), AccountDisplayMode.PerAccount, 3, Now);

        Assert.True(plan.ZeroAccountsIcon);
        Assert.Empty(plan.AccountIcons);
    }

    [Fact]
    public void PlanTray_NoAccounts_IsTheSameInBindingMode()
    {
        AccountDisplayPlan.TrayPlan plan = AccountDisplayPlan.PlanTray(
            Array.Empty<AccountDisplayPlan.Candidate>(), AccountDisplayMode.Binding, 3, Now);

        Assert.True(plan.ZeroAccountsIcon);
        Assert.Empty(plan.AccountIcons);
    }

    [Fact]
    public void PlanTray_OnlyIneligibleAccounts_StillShowsTheZeroAccountsIcon_SoExitStaysReachable()
    {
        var candidates = new[] { new AccountDisplayPlan.Candidate(Enabled: false, LiveView()) };

        AccountDisplayPlan.TrayPlan plan = AccountDisplayPlan.PlanTray(candidates, AccountDisplayMode.PerAccount, 3, Now);

        Assert.True(plan.ZeroAccountsIcon);
        Assert.Empty(plan.AccountIcons);
    }

    [Fact]
    public void PlanTray_WithAccounts_IsTheNormalSelection_AndNoZeroAccountsIcon()
    {
        var candidates = Enumerable.Range(0, 5).Select(_ => new AccountDisplayPlan.Candidate(true, LiveView())).ToList();

        AccountDisplayPlan.TrayPlan plan = AccountDisplayPlan.PlanTray(candidates, AccountDisplayMode.PerAccount, 3, Now);

        Assert.False(plan.ZeroAccountsIcon);
        Assert.Equal(new[] { 0, 1, 2 }, plan.AccountIcons);
    }

    [Fact]
    public void ZeroAccountsTooltip_SaysToLogIn_AndFitsTheTrayTooltipCap()
    {
        Assert.Equal("Logga in för att visa kvoten", LoginText.ZeroAccountsTooltip);
        Assert.True(LoginText.ZeroAccountsTooltip.Length <= 63);
        Assert.True(LoginText.NeedsLoginTooltip.Length <= 63);
    }

    [Fact]
    public void ZeroAccountsIcon_IsTheGreyRingWithAPadlock()
    {
        // The zero-accounts icon renders QuotaView.Initial flagged NeedsLogin: the grey ring with a padlock (not the "!").
        QuotaView zero = QuotaView.Initial with { NeedsLogin = true };
        Assert.Equal(Freshness.Unknown, zero.Freshness);
        Assert.True(QuotaIconParams.Build(zero, 16, true, Now).Padlock);
    }
}
