using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Config/AccountReconciler.cs (docs/multi-account.md "Reconciliation"): the version 1 to 2
/// migration, and the rule that the slot folders -- not accounts.json -- are the truth. Every
/// identity here is a placeholder.
/// </summary>
public sealed class AccountReconcilerTests : IDisposable
{
    const string UuidA = "11111111-1111-4111-8111-111111111111";
    const string UuidB = "22222222-2222-4222-8222-222222222222";
    const string OrgX = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    const string OrgY = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    readonly string _temp = Path.Combine(Path.GetTempPath(), "csb-reconcile-" + Guid.NewGuid().ToString("N"));
    readonly string _root;
    readonly string _json;
    readonly SlotStore _store;

    public AccountReconcilerTests()
    {
        _root = Path.Combine(_temp, "accounts");
        _json = Path.Combine(_temp, "accounts.json");
        Directory.CreateDirectory(_root);
        _store = new SlotStore(_root);
    }

    public void Dispose()
    {
        foreach (string link in _junctions) { try { Directory.Delete(link); } catch { /* already gone */ } }
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    string Config(string id) => Path.Combine(_root, id, "config");

    /// <summary>Creates a slot folder; state null means no slot.json (a legacy folder). identity writes a .claude.json with that oauthAccount.</summary>
    void MakeSlot(string id, SlotState? state, (string Account, string Org)? identity = null)
    {
        Directory.CreateDirectory(Config(id));
        if (state is { } s) _store.WriteMarker(id, s);
        if (identity is { } who)
        {
            File.WriteAllText(Path.Combine(Config(id), ".claude.json"), $$"""
            { "oauthAccount": { "accountUuid": "{{who.Account}}", "organizationUuid": "{{who.Org}}", "organizationName": "Acme AB" } }
            """);
        }
    }

    static AccountsConfig V2(params string[] slots) => new()
    {
        Version = AccountsConfig.CurrentVersion,
        Accounts = slots.Select(s => new AccountEntry { Slot = s }).ToList(),
    };

    // ---- migration, version 1 -> 2 ----

    [Fact]
    public void Migrate_ReferencedFolderBecomesASlotEntry_AndGetsAnActiveMarker()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        MakeSlot("4", state: null, (UuidA, OrgY));
        File.WriteAllText(_json, $$"""
        {
          "displayMode": "binding", "maxIcons": 2,
          "accounts": [
            { "configDir": {{System.Text.Json.JsonSerializer.Serialize(Config("1"))}}, "label": "Team", "enabled": true },
            { "configDir": {{System.Text.Json.JsonSerializer.Serialize(Config("4"))}}, "label": null, "enabled": false }
          ]
        }
        """);
        AccountsConfig config = AccountsConfig.Load(_json).Config;

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.True(result.Changed);
        Assert.Equal(2, config.Version);
        Assert.Equal(AccountDisplayMode.Binding, config.DisplayMode);
        Assert.Equal(2, config.MaxIcons);
        Assert.Equal(new[] { "1", "4" }, config.Accounts.Select(a => a.Slot));
        Assert.Equal("Team", config.Accounts[0].Label);
        Assert.True(config.Accounts[0].Enabled);
        Assert.False(config.Accounts[1].Enabled);
        Assert.All(config.Accounts, a => Assert.Null(a.LegacyConfigDir));
        Assert.Equal(SlotState.Active, _store.ReadMarker("1")!.State);
        Assert.Equal(SlotState.Active, _store.ReadMarker("4")!.State);

        AccountsConfig.Save(config, _json);
        Assert.DoesNotContain("configDir", File.ReadAllText(_json));
    }

    [Fact]
    public void Migrate_DropsFollowers_AndPathsTheGuardRefuses_ButLeavesUnreferencedFoldersAlone()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        MakeSlot("7", state: null, (UuidB, OrgX));                       // exists, but no v1 entry references it
        string outside = Path.Combine(_temp, "somewhere-else", "config");
        Directory.CreateDirectory(outside);
        var config = new AccountsConfig
        {
            Accounts = new List<AccountEntry>
            {
                new() { LegacyConfigDir = null },                         // the old "follow my default login" entry
                new() { LegacyConfigDir = outside },                      // not under the accounts root
                new() { LegacyConfigDir = Config("9") },                  // right shape, folder does not exist
                new() { LegacyConfigDir = @"\\server\share\accounts\1\config" },
                new() { LegacyConfigDir = Path.Combine(_temp, "accounts-evil", "1", "config") }, // shares a prefix, is another folder
                new() { LegacyConfigDir = Config("1") },                  // the one good entry
            },
        };

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "1", }, config.Accounts.Select(a => a.Slot));
        Assert.Equal(5, result.Dropped);
        Assert.True(Directory.Exists(Config("7")));                       // unreferenced legacy folder: not deleted ...
        Assert.Null(_store.ReadMarker("7"));                              // ... and not adopted either
        Assert.False(File.Exists(Path.Combine(_root, "7", "slot.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "9")));         // a missing folder is not created by migration
    }

    /// <summary>v1 files were written by an older script in whatever spelling it used; they are compared canonically (full path, separators, trailing separator, `..`, case) before the guard, so a perfectly good folder is not dropped over how its path was spelled.</summary>
    [Fact]
    public void Migrate_ComparesLegacyPathsCanonically()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        MakeSlot("4", state: null, (UuidB, OrgX));
        MakeSlot("6", state: null, (UuidA, OrgY));
        MakeSlot("8", state: null, (UuidB, OrgY));
        var config = new AccountsConfig
        {
            Accounts = new List<AccountEntry>
            {
                new() { LegacyConfigDir = Config("1") + "\\" },                                  // trailing separator
                new() { LegacyConfigDir = Config("4").Replace('\\', '/') },                      // forward slashes
                new() { LegacyConfigDir = Config("6").ToUpperInvariant() },                      // case (the guard ignores it)
                new() { LegacyConfigDir = Path.Combine(_root, "5", "..", "8", "config") },       // .. resolved
            },
        };

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "1", "4", "6", "8" }, config.Accounts.Select(a => a.Slot));
    }

    [Fact]
    public void Migrate_ExpandsEightDotThreeNamesInTheFolder()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        string shortSpelling = ShortPathOf(Config("1"));
        var config = new AccountsConfig { Accounts = new List<AccountEntry> { new() { LegacyConfigDir = shortSpelling } } };

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "1" }, config.Accounts.Select(a => a.Slot)); // on a volume without 8.3 names the spelling is unchanged and this is trivially true
    }

    // ---- one bad slot does not abort the pass ----

    [Fact]
    public void OneUnreadableSlot_DoesNotAbortTheWholePass()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        // An active slot whose config dir is a junction: ConfigDirOf (the path guard) throws for it.
        Directory.CreateDirectory(Path.Combine(_root, "11111111"));
        _store.WriteMarker("11111111", SlotState.Active);
        MakeJunction(Config("11111111"), Path.Combine(_temp, "elsewhere"));
        // ... and a pending one that does hold a login, and a retired one, alongside it.
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgX));
        MakeSlot("22222222", SlotState.Retired);
        AccountsConfig config = V2("e5f6a7b8");

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.Equal(SlotState.Active, _store.ReadMarker("a1b2c3d4")!.State);              // adopted
        Assert.False(Directory.Exists(Path.Combine(_root, "22222222")));                    // retired one deleted
        Assert.Contains(config.Accounts, a => a.Slot == "e5f6a7b8");
        Assert.Contains(config.Accounts, a => a.Slot == "a1b2c3d4");
        Assert.True(result.Adopted >= 1);
    }

    [Fact]
    public void ABadPendingSlot_IsSkippedAndTheRestStillAdopted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "11111111"));
        _store.WriteMarker("11111111", SlotState.Pending);
        MakeJunction(Config("11111111"), Path.Combine(_temp, "elsewhere"));  // ConfigDirOf throws for this pending slot
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgX));
        AccountsConfig config = V2();

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "a1b2c3d4" }, config.Accounts.Select(a => a.Slot));
    }

    // ---- helpers ----

    readonly List<string> _junctions = new();

    void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "test setup: the junction was not created");
        _junctions.Add(link);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint bufferLength);

    static string ShortPathOf(string path)
    {
        var sb = new System.Text.StringBuilder(1024);
        uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : path;
    }

    [Fact]
    public void Migrate_KeepsUnknownFields()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        File.WriteAllText(_json, $$"""
        { "futureSetting": "keepme", "accounts": [ { "configDir": {{System.Text.Json.JsonSerializer.Serialize(Config("1"))}}, "futureEntryField": 7 } ] }
        """);
        AccountsConfig config = AccountsConfig.Load(_json).Config;

        AccountReconciler.Reconcile(config, _store);
        AccountsConfig.Save(config, _json);

        string saved = File.ReadAllText(_json);
        Assert.Contains("futureSetting", saved);
        Assert.Contains("futureEntryField", saved);
    }

    // ---- reconciliation ----

    [Fact]
    public void RetiredFolders_AreDeleted_NeverAdopted_AndTheirEntriesDropped()
    {
        MakeSlot("a1b2c3d4", SlotState.Retired, (UuidA, OrgX));
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        AccountsConfig config = V2("a1b2c3d4", "e5f6a7b8");

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.False(Directory.Exists(Path.Combine(_root, "a1b2c3d4")));
        Assert.Equal(new[] { "e5f6a7b8" }, config.Accounts.Select(a => a.Slot));
        Assert.Equal(1, result.Retired);
        Assert.True(result.Changed);
    }

    [Fact]
    public void ARetiredFolderThatCannotBeDeletedYet_StaysRetired_ForTheNextStart()
    {
        MakeSlot("a1b2c3d4", SlotState.Retired);
        string locked = Path.Combine(Config("a1b2c3d4"), "held");
        File.WriteAllText(locked, "x");
        AccountsConfig config = V2();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AccountReconciler.Reconcile(config, _store);
        }

        Assert.Equal(SlotState.Retired, _store.ReadMarker("a1b2c3d4")!.State);
        Assert.Empty(config.Accounts);

        AccountReconciler.Reconcile(config, _store); // next start, lock gone
        Assert.False(Directory.Exists(Path.Combine(_root, "a1b2c3d4")));
    }

    [Fact]
    public void ActiveFoldersMissingFromAccountsJson_AreAppendedEnabled_InCreationOrder()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        Thread.Sleep(20);
        MakeSlot("a1b2c3d4", SlotState.Active, (UuidA, OrgX));
        AccountsConfig config = V2();

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "e5f6a7b8", "a1b2c3d4" }, config.Accounts.Select(a => a.Slot)); // created first, listed first
        Assert.All(config.Accounts, a => Assert.True(a.Enabled));
        Assert.Equal(2, result.Appended);
    }

    [Fact]
    public void ExistingEntries_KeepTheirOrderLabelAndEnabledFlag()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        MakeSlot("a1b2c3d4", SlotState.Active, (UuidA, OrgX));
        var config = new AccountsConfig
        {
            Version = 2,
            Accounts = new List<AccountEntry>
            {
                new() { Slot = "e5f6a7b8", Label = "Mine", Enabled = false },
                new() { Slot = "a1b2c3d4", Label = null, Enabled = true },
            },
        };

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "e5f6a7b8", "a1b2c3d4" }, config.Accounts.Select(a => a.Slot));
        Assert.Equal("Mine", config.Accounts[0].Label);
        Assert.False(config.Accounts[0].Enabled);
    }

    [Fact]
    public void PendingFolder_WithAnIdentity_IsAdoptedAsActive_AndListed()
    {
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgX));   // the app was killed between the browser login and the save
        AccountsConfig config = V2();

        AccountReconciler.Result result = AccountReconciler.Reconcile(config, _store);

        Assert.Equal(SlotState.Active, _store.ReadMarker("a1b2c3d4")!.State);
        Assert.Equal(new[] { "a1b2c3d4" }, config.Accounts.Select(a => a.Slot));
        Assert.Equal(1, result.Adopted);
    }

    [Fact]
    public void PendingFolder_WithoutAnIdentity_IsRetiredAndDeleted()
    {
        MakeSlot("a1b2c3d4", SlotState.Pending);                  // the login never finished
        AccountsConfig config = V2();

        AccountReconciler.Reconcile(config, _store);

        Assert.False(Directory.Exists(Path.Combine(_root, "a1b2c3d4")));
        Assert.Empty(config.Accounts);
    }

    [Fact]
    public void PendingFolder_ThatDuplicatesAnActiveAccount_IsDiscarded_NotAdopted()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidA, OrgX));
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgX));   // same person AND organisation
        AccountsConfig config = V2("e5f6a7b8");

        AccountReconciler.Reconcile(config, _store);

        Assert.False(Directory.Exists(Path.Combine(_root, "a1b2c3d4")));
        Assert.Equal(new[] { "e5f6a7b8" }, config.Accounts.Select(a => a.Slot));
    }

    [Fact]
    public void PendingFolder_ForTheSamePersonInAnotherOrganisation_IsNotADuplicate()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidA, OrgX));
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgY));   // identity is accountUuid + organizationUuid
        AccountsConfig config = V2("e5f6a7b8");

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "e5f6a7b8", "a1b2c3d4" }, config.Accounts.Select(a => a.Slot));
    }

    [Fact]
    public void PendingFolders_ProtectedByARunningLoginFlow_AreLeftExactlyAsTheyAre()
    {
        MakeSlot("a1b2c3d4", SlotState.Pending);
        AccountsConfig config = V2();

        AccountReconciler.Reconcile(config, _store, protectedSlots: new HashSet<string> { "a1b2c3d4" });

        Assert.True(Directory.Exists(Config("a1b2c3d4")));
        Assert.Equal(SlotState.Pending, _store.ReadMarker("a1b2c3d4")!.State);
    }

    [Fact]
    public void EntriesWhoseFolderIsMissing_OrNotActive_AreDropped()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        var config = V2("e5f6a7b8", "a1b2c3d4" /* no folder */, "12345678", "e5f6a7b8" /* listed twice */);
        MakeSlot("12345678", SlotState.Pending);                  // not active (and has no identity, so discarded)

        AccountReconciler.Reconcile(config, _store);

        Assert.Equal(new[] { "e5f6a7b8" }, config.Accounts.Select(a => a.Slot));
    }

    [Fact]
    public void EntriesWithAnInvalidSlotId_AreDropped()
    {
        var config = V2("..\\1", "A1B2C3D4", "");

        AccountReconciler.Reconcile(config, _store);

        Assert.Empty(config.Accounts);
    }

    [Fact]
    public void LegacyFoldersWithoutAMarker_AreNeverAdoptedByReconciliation()
    {
        MakeSlot("1", state: null, (UuidA, OrgX));
        AccountsConfig config = V2();

        AccountReconciler.Reconcile(config, _store);

        Assert.Empty(config.Accounts);
        Assert.True(Directory.Exists(Config("1")));
        Assert.Null(_store.ReadMarker("1"));
    }

    [Fact]
    public void Reconcile_IsIdempotent()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        MakeSlot("a1b2c3d4", SlotState.Pending, (UuidA, OrgX));
        AccountsConfig config = V2();
        Assert.True(AccountReconciler.Reconcile(config, _store).Changed);

        AccountReconciler.Result second = AccountReconciler.Reconcile(config, _store);

        Assert.False(second.Changed);
        Assert.Equal(2, config.Accounts.Count);
    }

    // ---- a lost or malformed accounts.json never orphans a login ----

    [Fact]
    public void MalformedAccountsJson_IsQuarantined_AndTheSetIsRebuiltFromTheActiveSlots()
    {
        MakeSlot("e5f6a7b8", SlotState.Active, (UuidB, OrgX));
        MakeSlot("a1b2c3d4", SlotState.Active, (UuidA, OrgX));
        MakeSlot("1", state: null, (UuidA, OrgY));                // a legacy folder without a marker stays out
        File.WriteAllText(_json, "{ truncated ");

        AccountsConfigLoad load = AccountsConfig.Load(_json);
        AccountReconciler.Result result = AccountReconciler.Reconcile(load.Config, _store);
        AccountsConfig.Save(load.Config, _json);

        Assert.Equal(AccountsConfigStatus.Quarantined, load.Status);
        Assert.Single(Directory.GetFiles(_temp, "accounts.bad-*.json"));
        Assert.Equal(2, load.Config.Accounts.Count);
        Assert.Equal(new[] { "a1b2c3d4", "e5f6a7b8" }, load.Config.Accounts.Select(a => a.Slot).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(2, result.Appended);
        Assert.Equal(2, AccountsConfig.Load(_json).Config.Accounts.Count); // and it persists
    }

    [Fact]
    public void MissingAccountsJson_WithActiveSlots_RebuildsThem()
    {
        MakeSlot("a1b2c3d4", SlotState.Active, (UuidA, OrgX));

        AccountsConfigLoad load = AccountsConfig.Load(_json);
        AccountReconciler.Reconcile(load.Config, _store);

        Assert.Equal(AccountsConfigStatus.Missing, load.Status);
        Assert.Equal(new[] { "a1b2c3d4" }, load.Config.Accounts.Select(a => a.Slot));
    }

    [Fact]
    public void NoSlotsAtAll_IsAValidEmptyState()
    {
        AccountsConfigLoad load = AccountsConfig.Load(_json);

        AccountReconciler.Reconcile(load.Config, _store);

        Assert.Empty(load.Config.Accounts);
        Assert.Equal(2, load.Config.Version);
    }
}
