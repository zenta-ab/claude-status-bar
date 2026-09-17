using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// AccountKeyedState owns the identity-keyed half of one account's runtime state (the
/// QuotaModel + its paired per-account DiskLogSink) and is the direct fix for today's live bug:
/// switching Claude Code's login mid-session used to mix two accounts' percentages into one
/// forecast because nothing was keyed by accountUuid alone -- which identifies the PERSON, not
/// the plan (docs/multi-account.md "Identity guard"; AccountIdentity's doc comment). The key is
/// AccountIdentity.StateKey, the (accountUuid, organizationUuid) pair; these tests exercise
/// AccountKeyedState with plain opaque key strings (it doesn't care about their shape) plus one
/// end-to-end test built from real StateKey values.
/// </summary>
public class AccountKeyedStateTests
{
    static string NewBaseDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static UsageSnapshot Snapshot(double sessionPct, DateTimeOffset resetsAt, DateTimeOffset t) =>
        new(sessionPct, resetsAt.ToString("O"), 10.0, resetsAt.AddDays(3).ToString("O"), t);

    [Fact]
    public async Task SyncIdentity_KeyChanges_RebuildsModelAndLogSink_NoEnvelopeOrRateCarriesOver()
    {
        string baseDir = NewBaseDir("keyedstate-switch");
        try
        {
            var state = new AccountKeyedState(baseDir, initialIdentityKey: "uuid-A");
            DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset resetA = t0.AddHours(3);

            // Account A burns up to a high, monotone-envelope-tracked percentage.
            state.Model.Ingest(Snapshot(80.0, resetA, t0), t0, 0);
            QuotaView viewA = state.Model.Evaluate(t0, 0);
            Assert.Equal(80.0, viewA.Session.UsedPct);

            bool rebuilt = state.SyncIdentity("uuid-B");
            Assert.True(rebuilt);
            Assert.Equal("uuid-B", state.IdentityKey);
            Assert.False(state.WarmStarted); // fresh state is eligible for its own warm start again

            // Account B's very first sample is a LOW percentage. If any state carried over from
            // A (the monotone envelope, in particular -- P = max(P, sample) within a window),
            // this would incorrectly read back as max(80, 20) = 80.
            DateTimeOffset t1 = t0.AddMinutes(1);
            DateTimeOffset resetB = t1.AddHours(3);
            state.Model.Ingest(Snapshot(20.0, resetB, t1), t1, 60_000);
            QuotaView viewB = state.Model.Evaluate(t1, 60_000);

            Assert.Equal(20.0, viewB.Session.UsedPct); // proves no envelope/rate carried over from A
            await state.DisposeAsync();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void SyncIdentity_SameKey_IsANoOp()
    {
        string baseDir = NewBaseDir("keyedstate-noop");
        try
        {
            var state = new AccountKeyedState(baseDir, "uuid-A");
            QuotaModel modelBefore = state.Model;

            bool rebuilt = state.SyncIdentity("uuid-A");

            Assert.False(rebuilt);
            Assert.Same(modelBefore, state.Model); // unchanged -- no needless rebuild for an unchanged identity
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void SyncIdentity_RebuildsIntoADistinctPerAccountLogDirectory_KeyedByIdentityKey()
    {
        string baseDir = NewBaseDir("keyedstate-logdir");
        try
        {
            var state = new AccountKeyedState(baseDir, initialIdentityKey: null);
            string pendingDir = state.CurrentLogDir;
            Assert.Equal(Path.Combine(baseDir, "_pending"), pendingDir);

            state.SyncIdentity("11111111-aaaa-bbbb-cccc-222222222222");

            Assert.Equal(Path.Combine(baseDir, "11111111-aaaa-bbbb-cccc-222222222222"), state.CurrentLogDir);
            Assert.NotEqual(pendingDir, state.CurrentLogDir);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task TwoIndependentStates_SameBaseDir_DifferentKeys_HaveIndependentCsvPathsAndModels()
    {
        string baseDir = NewBaseDir("keyedstate-parallel");
        try
        {
            var stateA = new AccountKeyedState(baseDir, "uuid-AAAA");
            var stateB = new AccountKeyedState(baseDir, "uuid-BBBB");

            Assert.NotEqual(stateA.CurrentLogDir, stateB.CurrentLogDir);
            Assert.Equal(Path.Combine(baseDir, "uuid-AAAA"), stateA.CurrentLogDir);
            Assert.Equal(Path.Combine(baseDir, "uuid-BBBB"), stateB.CurrentLogDir);
            Assert.NotSame(stateA.Model, stateB.Model);

            DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset resetAt = t0.AddHours(3);
            stateA.Model.Ingest(Snapshot(70.0, resetAt, t0), t0, 0);
            stateB.Model.Ingest(Snapshot(5.0, resetAt, t0), t0, 0);

            Assert.Equal(70.0, stateA.Model.Evaluate(t0, 0).Session.UsedPct);
            Assert.Equal(5.0, stateB.Model.Evaluate(t0, 0).Session.UsedPct);

            await stateA.DisposeAsync();
            await stateB.DisposeAsync();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    /// <summary>
    /// The exact live bug on this machine (docs/multi-account.md "Identity guard"): a Team
    /// login and a personal Max login for the SAME PERSON share one accountUuid but have
    /// different organizationUuid. Keying on AccountIdentity.StateKey -- not accountUuid alone
    /// -- is what keeps their CSVs and models apart; keying on accountUuid alone would collapse
    /// both onto Path.Combine(baseDir, sharedAccountUuid) and this test would fail.
    /// </summary>
    [Fact]
    public async Task TwoIdentities_SameAccountUuid_DifferentOrganizationUuid_HaveIndependentCsvPathsAndModels()
    {
        const string sharedAccountUuid = "11111111-1111-4111-8111-111111111111";
        const string teamOrgUuid = "11111111-1111-1111-1111-111111111111";
        const string maxOrgUuid = "22222222-2222-2222-2222-222222222222";
        string teamKey = AccountIdentity.StateKeyOf(sharedAccountUuid, teamOrgUuid);
        string maxKey = AccountIdentity.StateKeyOf(sharedAccountUuid, maxOrgUuid);

        string baseDir = NewBaseDir("keyedstate-same-person-two-orgs");
        try
        {
            var teamState = new AccountKeyedState(baseDir, teamKey);
            var maxState = new AccountKeyedState(baseDir, maxKey);

            Assert.NotEqual(teamState.CurrentLogDir, maxState.CurrentLogDir);
            Assert.Equal(Path.Combine(baseDir, teamKey), teamState.CurrentLogDir);
            Assert.Equal(Path.Combine(baseDir, maxKey), maxState.CurrentLogDir);
            Assert.NotSame(teamState.Model, maxState.Model);

            DateTimeOffset t0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset resetAt = t0.AddHours(3);
            teamState.Model.Ingest(Snapshot(90.0, resetAt, t0), t0, 0); // Team seat: near its limit
            maxState.Model.Ingest(Snapshot(12.0, resetAt, t0), t0, 0);  // Personal Max seat: barely used

            // Neither model's envelope/rate is contaminated by the other plan's percentage --
            // this is exactly the "single envelope fed two plans' numbers" symptom this fixes.
            Assert.Equal(90.0, teamState.Model.Evaluate(t0, 0).Session.UsedPct);
            Assert.Equal(12.0, maxState.Model.Evaluate(t0, 0).Session.UsedPct);

            await teamState.DisposeAsync();
            await maxState.DisposeAsync();
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    /// <summary>Identity-change detection must fire on the pair changing, not just accountUuid -- switching organisations for the same person is exactly the case that must reset a slot's model.</summary>
    [Fact]
    public void SyncIdentity_SameAccountUuid_OnlyOrganizationChanges_StillRebuilds()
    {
        const string sharedAccountUuid = "11111111-1111-4111-8111-111111111111";
        string teamKey = AccountIdentity.StateKeyOf(sharedAccountUuid, "11111111-1111-1111-1111-111111111111");
        string maxKey = AccountIdentity.StateKeyOf(sharedAccountUuid, "22222222-2222-2222-2222-222222222222");

        string baseDir = NewBaseDir("keyedstate-org-switch");
        try
        {
            var state = new AccountKeyedState(baseDir, teamKey);
            bool rebuilt = state.SyncIdentity(maxKey);

            Assert.True(rebuilt);
            Assert.Equal(maxKey, state.IdentityKey);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
