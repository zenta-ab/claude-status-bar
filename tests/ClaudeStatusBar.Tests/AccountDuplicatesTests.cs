using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Model/AccountDuplicates.cs -- docs/multi-account.md "Duplicate accounts": pure grouping by
/// AccountIdentity.StateKey, kept first in config order, every later account in a group marked a
/// duplicate of it.
/// </summary>
public class AccountDuplicatesTests
{
    [Fact]
    public void TwoAccounts_SameStateKey_SecondIsMarkedDuplicateOfTheFirst()
    {
        var keys = new string?[] { "aaaaaaaa_bbbbbbbb", "aaaaaaaa_bbbbbbbb" };

        IReadOnlyList<AccountDuplicates.Result> results = AccountDuplicates.Detect(keys);

        Assert.False(results[0].IsDuplicate);
        Assert.Null(results[0].DuplicateOfIndex);
        Assert.True(results[1].IsDuplicate);
        Assert.Equal(0, results[1].DuplicateOfIndex);
    }

    /// <summary>
    /// The task's own live scenario, and docs/multi-account.md's "Identity guard": the SAME
    /// person can hold a Team seat and a personal Max seat at once -- same accountUuid, different
    /// organizationUuid -- and that pair must never be collapsed into one duplicate.
    /// </summary>
    [Fact]
    public void SameAccountUuid_DifferentOrganizationUuid_AreNotDuplicates()
    {
        var keys = new string?[] { "11111111_22222222", "11111111_33333333" };

        IReadOnlyList<AccountDuplicates.Result> results = AccountDuplicates.Detect(keys);

        Assert.False(results[0].IsDuplicate);
        Assert.False(results[1].IsDuplicate);
    }

    [Fact]
    public void ThreeAccounts_TwoShareAKey_OnlyTheLaterOfThatPairIsMarked()
    {
        var keys = new string?[] { "key-a", "key-b", "key-a" };

        IReadOnlyList<AccountDuplicates.Result> results = AccountDuplicates.Detect(keys);

        Assert.False(results[0].IsDuplicate);
        Assert.False(results[1].IsDuplicate);
        Assert.True(results[2].IsDuplicate);
        Assert.Equal(0, results[2].DuplicateOfIndex);
    }

    /// <summary>A null key means "identity not known yet" -- it must never be treated as equal to another unknown, or to anything else.</summary>
    [Fact]
    public void UnknownIdentity_NeverCountsAsADuplicate_EvenAgainstAnotherUnknown()
    {
        var keys = new string?[] { null, null, "key-a" };

        IReadOnlyList<AccountDuplicates.Result> results = AccountDuplicates.Detect(keys);

        Assert.False(results[0].IsDuplicate);
        Assert.False(results[1].IsDuplicate);
        Assert.False(results[2].IsDuplicate);
    }

    /// <summary>
    /// docs/multi-account.md: "When the identities diverge again ... both are tracked again."
    /// Detect itself is stateless -- re-running it with the second account's key changed (e.g.
    /// the user logged a different account into its directory) simply stops finding a
    /// collision, exactly as if the two accounts had never matched.
    /// </summary>
    [Fact]
    public void IdentitiesDiverge_ReRunning_NeitherIsADuplicateAnyMore()
    {
        var beforeKeys = new string?[] { "shared-key", "shared-key" };
        IReadOnlyList<AccountDuplicates.Result> before = AccountDuplicates.Detect(beforeKeys);
        Assert.True(before[1].IsDuplicate);

        var afterKeys = new string?[] { "shared-key", "a-new-different-key" };
        IReadOnlyList<AccountDuplicates.Result> after = AccountDuplicates.Detect(afterKeys);

        Assert.False(after[0].IsDuplicate);
        Assert.False(after[1].IsDuplicate);
    }

    [Fact]
    public void SingleAccount_NeverADuplicate()
    {
        IReadOnlyList<AccountDuplicates.Result> results = AccountDuplicates.Detect(new string?[] { "only-key" });
        Assert.False(results[0].IsDuplicate);
    }
}
