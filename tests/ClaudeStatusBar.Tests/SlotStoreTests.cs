using System.Diagnostics;
using ClaudeStatusBar.Config;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Config/SlotStore.cs: the path guard every login / delete / spawn goes through, and the slot
/// lifecycle (docs/multi-account.md "Slots"). The guard is tested as an attacker would use it --
/// traversal, other roots, UNC, the user's own Claude folder, alternative spellings of an accepted
/// path, junctions -- because its whole job is to refuse everything but exactly
/// `&lt;root&gt;\&lt;id&gt;\config`.
/// </summary>
public sealed class SlotStoreTests : IDisposable
{
    readonly string _temp = Path.Combine(Path.GetTempPath(), "csb-slots-" + Guid.NewGuid().ToString("N"));
    readonly string _root;
    readonly SlotStore _store;
    readonly List<string> _junctions = new();

    public SlotStoreTests()
    {
        _root = Path.Combine(_temp, "accounts");
        Directory.CreateDirectory(_root);
        _store = new SlotStore(_root);
    }

    public void Dispose()
    {
        // Links first, so deleting the tree can never follow one.
        foreach (string link in _junctions)
        {
            try { Directory.Delete(link); } catch { /* already gone */ }
        }
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    string MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(target);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "test setup: the junction was not created");
        _junctions.Add(link);
        return link;
    }

    string Config(string id) => Path.Combine(_root, id, "config");

    // ---- the guard accepts exactly <root>\<id>\config ----

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("0042")]
    [InlineData("12345678")]
    [InlineData("a1b2c3d4")]
    [InlineData("00000000")]
    public void Guard_AcceptsValidIds_AndReturnsItsOwnSpelling(string id)
    {
        Assert.True(_store.TryGuardConfigDir(Config(id), out string? slot, out string? canonical));
        Assert.Equal(id, slot);
        Assert.Equal(Config(id), canonical);
        Assert.Equal(Config(id), _store.ConfigDirOf(id));
    }

    [Fact]
    public void Guard_AcceptsADifferentCaseOfTheRootAndOfConfig_ButReturnsTheCanonicalSpelling()
    {
        string shouted = Path.Combine(_root.ToUpperInvariant(), "a1b2c3d4", "CONFIG");

        Assert.True(_store.TryGuardConfigDir(shouted, out string? slot, out string? canonical));
        Assert.Equal("a1b2c3d4", slot);
        Assert.Equal(Config("a1b2c3d4"), canonical); // never the caller's spelling
    }

    [Fact]
    public void SlotDirGuard_AcceptsExactlyRootSlashId()
    {
        Assert.True(_store.TryGuardSlotDir(Path.Combine(_root, "a1b2c3d4"), out string? slot, out string? canonical));
        Assert.Equal("a1b2c3d4", slot);
        Assert.Equal(Path.Combine(_root, "a1b2c3d4"), canonical);
    }

    // ---- ... and refuses everything else ----

    [Theory]
    [InlineData("A1B2C3D4")]   // upper-case hex: ids are lower-case
    [InlineData("a1b2c3d")]    // 7 characters
    [InlineData("a1b2c3d45")]  // 9 characters
    [InlineData("a1b2c3dg")]   // not hex
    [InlineData("1a")]         // neither all digits nor 8 hex
    [InlineData("-1")]
    [InlineData("1.")]         // Windows would strip the dot and open slot 1
    [InlineData("1 ")]         // ... and the trailing space
    [InlineData("١٢٣")]        // non-ASCII digits are not [0-9]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void Guard_RefusesIdsOfTheWrongShape(string id)
    {
        string path = _root + "\\" + id + "\\config";
        Assert.False(_store.TryGuardConfigDir(path, out _, out _));
        Assert.False(SlotStore.IsValidId(id));
        Assert.Throws<UnauthorizedAccessException>(() => _store.ConfigDirOf(id));
    }

    [Fact]
    public void Guard_RefusesNullAndEmpty()
    {
        Assert.False(_store.TryGuardConfigDir(null, out _, out _));
        Assert.False(_store.TryGuardConfigDir("", out _, out _));
        Assert.False(SlotStore.IsValidId(null));
        Assert.Throws<UnauthorizedAccessException>(() => _store.ConfigDirOf(null!));
    }

    [Fact]
    public void Guard_RefusesIdWithATrailingNewline()
    {
        Assert.False(SlotStore.IsValidId("a1b2c3d4\n")); // `$` would have matched this; `\z` does not
        Assert.False(SlotStore.IsValidId("1\n"));
    }

    [Theory]
    [InlineData(@"{root}\1\..\2\config")]            // traversal that still lands on a valid slot
    [InlineData(@"{root}\..\accounts\1\config")]     // out and back in
    [InlineData(@"{root}\1\config\..")]
    [InlineData(@"{root}\1\config\..\config")]
    [InlineData(@"{root}\1\config\..\..\..")]
    [InlineData(@"{root}\..")]
    [InlineData(@"{root}\.")]
    [InlineData(@"{root}")]                          // the accounts root itself
    [InlineData(@"{root}\")]
    [InlineData(@"{root}\1")]                        // a slot dir is not a config dir
    [InlineData(@"{root}\1\config\sub")]             // deeper
    [InlineData(@"{root}\1\config\")]                // trailing separator
    [InlineData(@"{root}\1\config\\")]
    [InlineData(@"{root}\\1\config")]                // doubled separator
    [InlineData(@"{root}\1\other")]
    [InlineData(@"{root}\1\config.")]                // Windows strips the dot
    [InlineData(@"{root}\1\config ")]
    [InlineData(@"{root}\1\config::$DATA")]          // alternate data stream spellings
    [InlineData(@"{root}\1\config:stream")]
    [InlineData(@"{root}-evil\1\config")]            // a sibling that merely shares the prefix
    [InlineData(@"{root}x\1\config")]
    public void Guard_RefusesTraversalAndAlternativeSpellings(string template)
    {
        string path = template.Replace("{root}", _root);
        Assert.False(_store.TryGuardConfigDir(path, out _, out _), path);
    }

    [Fact]
    public void Guard_RefusesForwardSlashSpellings()
    {
        string forward = _root.Replace('\\', '/') + "/1/config";
        Assert.False(_store.TryGuardConfigDir(forward, out _, out _));
    }

    [Fact]
    public void Guard_RefusesOtherRoots_UncPaths_AndTheUsersOwnClaudeFolder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] hostile =
        {
            Path.Combine(profile, ".claude"),
            Path.Combine(profile, ".claude", "config"),
            Path.Combine(profile, ".claude.json"),
            @"C:\Windows\System32",
            @"C:\Users\someone\AppData\Local\ClaudeStatusBar\accounts\1\config", // looks right, wrong root
            @"\\server\share\accounts\1\config",
            @"\\localhost\c$\accounts\1\config",
            @"\\?\" + _root + @"\1\config",
            @"\\.\" + _root + @"\1\config",
            "%USERPROFILE%\\.claude",
            "%LOCALAPPDATA%\\ClaudeStatusBar\\accounts\\1\\config",
            "~\\.claude",
            "accounts\\1\\config",                                           // relative
            "1\\config",
        };
        foreach (string path in hostile)
            Assert.False(_store.TryGuardConfigDir(path, out _, out _), path);
    }

    [Fact]
    public void SlotDirGuard_RefusesTheConfigDirAndTheRoot()
    {
        Assert.False(_store.TryGuardSlotDir(Config("1"), out _, out _));
        Assert.False(_store.TryGuardSlotDir(_root, out _, out _));
        Assert.False(_store.TryGuardSlotDir(_root + "\\", out _, out _));
        Assert.False(_store.TryGuardSlotDir(Path.Combine(_root, "1") + "\\", out _, out _));
        Assert.False(_store.TryGuardSlotDir(Path.Combine(_root, "..", "accounts", "1"), out _, out _));
    }

    // ---- reparse points ----

    [Fact]
    public void Guard_RefusesAConfigDirThatIsAJunction()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1"));
        MakeJunction(Config("1"), Path.Combine(_temp, "elsewhere"));

        Assert.False(_store.TryGuardConfigDir(Config("1"), out _, out _));
        Assert.Throws<UnauthorizedAccessException>(() => _store.ConfigDirOf("1"));
    }

    [Fact]
    public void Guard_RefusesASlotDirThatIsAJunction_ForBothFormsOfTheGuard()
    {
        string target = Path.Combine(_temp, "elsewhere");
        Directory.CreateDirectory(Path.Combine(target, "config"));
        MakeJunction(Path.Combine(_root, "2"), target);

        Assert.False(_store.TryGuardConfigDir(Config("2"), out _, out _));
        Assert.False(_store.TryGuardSlotDir(Path.Combine(_root, "2"), out _, out _));
    }

    [Fact]
    public void Guard_RefusesEverythingWhenTheAccountsRootItselfIsAJunction()
    {
        string realRoot = Path.Combine(_temp, "real-accounts");
        Directory.CreateDirectory(Path.Combine(realRoot, "1", "config"));
        string linkedRoot = MakeJunction(Path.Combine(_temp, "linked-accounts"), realRoot);
        var store = new SlotStore(linkedRoot);

        Assert.False(store.TryGuardConfigDir(Path.Combine(linkedRoot, "1", "config"), out _, out _));
        Assert.Throws<UnauthorizedAccessException>(() => store.ConfigDirOf("1"));
        Assert.Empty(store.Enumerate());
    }

    [Fact]
    public void TryDelete_RefusesAJunctionedSlot_AndLeavesWhatItPointsAtUntouched()
    {
        string victim = Path.Combine(_temp, "victim");
        Directory.CreateDirectory(Path.Combine(victim, "config"));
        string precious = Path.Combine(victim, "config", "precious.txt");
        File.WriteAllText(precious, "keep me");
        MakeJunction(Path.Combine(_root, "5"), victim);

        Assert.False(_store.TryDelete("5"));

        Assert.True(File.Exists(precious));
    }

    // ---- lifecycle ----

    [Fact]
    public void CreatePending_MakesAnEightHexSlotWithAConfigDirAndAPendingMarker_AndNoIdentity()
    {
        string id = _store.CreatePending();

        Assert.Matches("^[0-9a-f]{8}$", id);
        Assert.True(Directory.Exists(Config(id)));
        SlotMarker? marker = _store.ReadMarker(id);
        Assert.NotNull(marker);
        Assert.Equal(SlotState.Pending, marker!.State);
        Assert.Equal(1, marker.Schema);

        string raw = File.ReadAllText(Path.Combine(_root, id, "slot.json"));
        Assert.Contains("\"state\": \"pending\"", raw);
        Assert.Contains("\"createdUtc\"", raw);
        Assert.Contains("\"schema\": 1", raw);
        Assert.DoesNotContain("@", raw);
        Assert.DoesNotContain("uuid", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, System.Text.Json.JsonDocument.Parse(raw).RootElement.EnumerateObject().Count()); // state, createdUtc, schema -- nothing else
    }

    [Fact]
    public void CreatePending_NeverReusesAnIdThatAnyFolderStillHolds_RetiredOnesIncluded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "aaaaaaaa"));
        new SlotStore(_root).WriteMarker("aaaaaaaa", SlotState.Retired);
        Directory.CreateDirectory(Path.Combine(_root, "bbbbbbbb")); // a folder with no marker at all
        var draws = new Queue<string>(new[] { "aaaaaaaa", "aaaaaaaa", "bbbbbbbb", "cccccccc" });
        var store = new SlotStore(_root, idSource: () => draws.Dequeue());

        string id = store.CreatePending();

        Assert.Equal("cccccccc", id);
        Assert.Empty(draws);
    }

    [Fact]
    public void CreatePending_ManyTimes_GivesDistinctIds()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => _store.CreatePending()).ToList();
        Assert.Equal(200, ids.Distinct().Count());
    }

    [Fact]
    public void Enumerate_ListsOnlyValidIdFolders_WithTheirMarkers()
    {
        string pending = _store.CreatePending();
        Directory.CreateDirectory(Config("1"));                        // a legacy folder: valid id, no slot.json
        Directory.CreateDirectory(Path.Combine(_root, "notes"));       // not a slot
        Directory.CreateDirectory(Path.Combine(_root, "A1B2C3D4"));    // wrong case
        File.WriteAllText(Path.Combine(_root, "99"), "a file, not a folder");

        IReadOnlyList<SlotInfo> found = _store.Enumerate();

        Assert.Equal(new[] { "1", pending }.OrderBy(x => x, StringComparer.Ordinal), found.Select(s => s.Id));
        Assert.Null(found.Single(s => s.Id == "1").Marker);
        Assert.Equal(SlotState.Pending, found.Single(s => s.Id == pending).Marker!.State);
    }

    [Fact]
    public void ReadMarker_TreatsMalformedAndUnknownStatesAsNoMarker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1"));
        File.WriteAllText(Path.Combine(_root, "1", "slot.json"), "{ not json");
        Assert.Null(_store.ReadMarker("1"));

        File.WriteAllText(Path.Combine(_root, "1", "slot.json"), """{"state":"zombie","createdUtc":"2026-01-01T00:00:00Z","schema":1}""");
        Assert.Null(_store.ReadMarker("1"));
    }

    [Fact]
    public void WriteMarker_KeepsTheOriginalCreationTime()
    {
        string id = _store.CreatePending();
        DateTimeOffset created = _store.ReadMarker(id)!.CreatedUtc;

        _store.WriteMarker(id, SlotState.Active);

        SlotMarker marker = _store.ReadMarker(id)!;
        Assert.Equal(SlotState.Active, marker.State);
        Assert.Equal(created, marker.CreatedUtc);
    }

    [Fact]
    public void TryDelete_RemovesTheWholeFolder()
    {
        string id = _store.CreatePending();
        File.WriteAllText(Path.Combine(Config(id), ".credentials.json"), "x");
        Directory.CreateDirectory(Path.Combine(Config(id), "projects", "p"));

        Assert.True(_store.TryDelete(id));

        Assert.False(Directory.Exists(Path.Combine(_root, id)));
        Assert.True(_store.TryDelete(id)); // already gone is fine
    }

    [Fact]
    public void TryDelete_WhenALoginFileIsLocked_KeepsTheRetiredMarkerSoTheNextStartFinishesIt()
    {
        string id = _store.CreatePending();
        _store.WriteMarker(id, SlotState.Retired);
        string locked = Path.Combine(Config(id), ".credentials.json");
        File.WriteAllText(locked, "x");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(_store.TryDelete(id));
        }

        Assert.True(File.Exists(Path.Combine(_root, id, "slot.json")), "the marker must outlive a failed delete");
        Assert.Equal(SlotState.Retired, _store.ReadMarker(id)!.State);

        Assert.True(_store.TryDelete(id)); // lock released: the retry succeeds
        Assert.False(Directory.Exists(Path.Combine(_root, id)));
    }

    [Fact]
    public async Task DeleteWithRetryAsync_SucceedsOnceTheLockIsReleased()
    {
        string id = _store.CreatePending();
        string locked = Path.Combine(Config(id), "file");
        File.WriteAllText(locked, "x");
        var holder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(300); holder.Dispose(); });

        bool deleted = await _store.DeleteWithRetryAsync(id, attempts: 6, delayMs: 200);

        Assert.True(deleted);
        Assert.False(Directory.Exists(Path.Combine(_root, id)));
    }

    // ---- off the UI thread ----

    sealed class ThreadRecordingStore : SlotStore
    {
        public int DeleteThreadId;
        public bool DeleteOnPoolThread;

        public ThreadRecordingStore(string root) : base(root) { }

        public override bool TryDelete(string slotId)
        {
            DeleteThreadId = Environment.CurrentManagedThreadId;
            DeleteOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return base.TryDelete(slotId);
        }
    }

    [Fact]
    public async Task DeleteWithRetryAsync_RunsEvenTheFirstAttemptOffTheCallingThread()
    {
        var store = new ThreadRecordingStore(_root);
        string id = store.CreatePending();
        int callerThread = Environment.CurrentManagedThreadId;

        bool deleted = await store.DeleteWithRetryAsync(id);

        Assert.True(deleted);
        Assert.NotEqual(callerThread, store.DeleteThreadId); // the first attempt did not run inline on the caller (the UI thread, in the app)
        Assert.True(store.DeleteOnPoolThread);
    }

    // ---- canonical spelling of a legacy path ----

    [Fact]
    public void CanonicalizeLegacyPath_ResolvesSeparatorsTrailingSeparatorsAndDots()
    {
        Assert.Equal(Config("1"), _store.CanonicalizeLegacyPath(Config("1") + "\\"));
        Assert.Equal(Config("1"), _store.CanonicalizeLegacyPath(Config("1").Replace('\\', '/')));
        Assert.Equal(Config("1"), _store.CanonicalizeLegacyPath(Path.Combine(_root, "2", "..", "1", "config")));
    }

    [Fact]
    public void CanonicalizeLegacyPath_NeverMakesAnOutsidePathLookInside()
    {
        string outside = Path.Combine(_temp, "accounts-evil", "1", "config");

        string canonical = _store.CanonicalizeLegacyPath(outside);

        Assert.False(_store.TryGuardConfigDir(canonical, out _, out _));
        Assert.False(_store.TryGuardConfigDir(_store.CanonicalizeLegacyPath(@"\\server\share\accounts\1\config"), out _, out _));
        Assert.False(_store.TryGuardConfigDir(_store.CanonicalizeLegacyPath(Path.Combine(_root, "..", "elsewhere", "1", "config")), out _, out _));
    }

    [Fact]
    public void ToLongPath_KeepsAPartThatDoesNotExistYet()
    {
        string notYet = Path.Combine(_root, "99999999", "config");

        Assert.EndsWith(Path.Combine("99999999", "config"), SlotStore.ToLongPath(notYet));
    }
}
