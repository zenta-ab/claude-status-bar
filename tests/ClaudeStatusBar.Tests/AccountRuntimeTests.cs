using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Model;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// End-to-end AccountRuntime tests against tests/FakeClaudeChild (the same stand-in
/// ChannelSupervisorTests uses), covering docs/multi-account.md's "accounts are polled
/// independently" and privacy requirements. A plain SynchronizationContext (thread-pool Post,
/// same async-completion shape WindowsFormsSynchronizationContext has in production) stands in
/// for the UI thread.
/// </summary>
[Collection("process")]
public class AccountRuntimeTests
{
    static string ExePath => Path.Combine(AppContext.BaseDirectory, "FakeClaudeChild.exe");

    static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string NewConfigDirWithIdentity(string prefix, string accountUuid, string? email = null, string? displayName = null)
    {
        string dir = NewTempDir(prefix);
        WriteIdentity(dir, accountUuid, organizationUuid: null, email, displayName);
        return dir;
    }

    static void WriteIdentity(string configDir, string accountUuid, string? organizationUuid, string? email = null, string? displayName = null)
    {
        string emailJson = email is null ? "null" : $"\"{email}\"";
        string displayNameJson = displayName is null ? "null" : $"\"{displayName}\"";
        string orgUuidJson = organizationUuid is null ? "null" : $"\"{organizationUuid}\"";
        File.WriteAllText(Path.Combine(configDir, ".claude.json"), $$"""
        {
          "oauthAccount": {
            "accountUuid": "{{accountUuid}}",
            "organizationUuid": {{orgUuidJson}},
            "organizationName": "Test Org",
            "emailAddress": {{emailJson}},
            "displayName": {{displayNameJson}}
          }
        }
        """);
    }

    static ChildProcessSpec FakeSpec(string mode, string workDir) => new(
        ResolveExePath: () => ExePath,
        Arguments: $"--mode={mode}",
        WorkingDirectory: workDir);

    static ChildProcessSpec SlowFakeSpec(string workDir, int delayMs) => new(
        ResolveExePath: () => ExePath,
        Arguments: $"--mode=slow --delay-ms={delayMs}",
        WorkingDirectory: workDir);

    /// <summary>PollAsync's completion is posted asynchronously (fire-and-forget, exactly like production's UI-thread Post) -- poll for the effect to land instead of asserting immediately after awaiting PollAsync itself.</summary>
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50).ConfigureAwait(false);
        }
        Assert.True(condition(), "condition was never satisfied within the timeout");
    }

    [Fact]
    public async Task TwoAccounts_InParallel_IndependentCsvPathsIndependentModels_OneFailureLeavesTheOtherLive()
    {
        string baseLogDir = NewTempDir("runtime-parallel-logs");
        string configDirA = NewConfigDirWithIdentity("runtime-parallel-cfgA", "aaaaaaaa-0000-0000-0000-000000000000");
        string configDirB = NewConfigDirWithIdentity("runtime-parallel-cfgB", "bbbbbbbb-0000-0000-0000-000000000000");
        string workDirA = NewTempDir("runtime-parallel-workA");
        string workDirB = NewTempDir("runtime-parallel-workB");
        var uiContext = new SynchronizationContext();

        var runtimeA = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("normal", workDirA), configDirOverride: configDirA);
        var runtimeB = new AccountRuntime("1", new AccountEntry { Enabled = true }, 1, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("die-immediately", workDirB), configDirOverride: configDirB);

        try
        {
            runtimeA.Start();
            runtimeB.Start();

            await WaitUntilAsync(() =>
            {
                runtimeA.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtimeA.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            // Account B's child dies immediately on every launch (--mode=die-immediately): it
            // must stay degraded, and must never block or corrupt account A's own polling/eval loop.
            await runtimeB.PollAsync();
            runtimeB.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.NotEqual(Freshness.Live, runtimeB.LastView.Freshness);

            // Independent CSV paths, keyed by each account's own accountUuid.
            Assert.Equal(Path.Combine(baseLogDir, "aaaaaaaa-0000-0000-0000-000000000000"), runtimeA.CurrentLogDir);
            Assert.Equal(Path.Combine(baseLogDir, "bbbbbbbb-0000-0000-0000-000000000000"), runtimeB.CurrentLogDir);
            Assert.NotEqual(runtimeA.CurrentLogDir, runtimeB.CurrentLogDir);

            string[] csvFilesA = Directory.Exists(runtimeA.CurrentLogDir)
                ? Directory.GetFiles(runtimeA.CurrentLogDir, "window-shape-*.csv") : Array.Empty<string>();
            Assert.NotEmpty(csvFilesA); // A actually polled successfully and wrote its own CSV
            Assert.False(Directory.Exists(runtimeB.CurrentLogDir) && Directory.GetFiles(runtimeB.CurrentLogDir, "window-shape-*.csv").Length > 0);
        }
        finally
        {
            await runtimeA.DisposeAsync();
            await runtimeB.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDirA, recursive: true);
            Directory.Delete(configDirB, recursive: true);
            Directory.Delete(workDirA, recursive: true);
            Directory.Delete(workDirB, recursive: true);
        }
    }

    [Fact]
    public async Task Privacy_EmailAndDisplayName_NeverReachTheLogSinkOrTheCsv()
    {
        string baseLogDir = NewTempDir("runtime-privacy-logs");
        const string secretEmail = "totally-secret-person@example.com";
        const string secretDisplayName = "Totally Secret Person";
        string configDir = NewConfigDirWithIdentity("runtime-privacy-cfg", "cccccccc-0000-0000-0000-000000000000", secretEmail, secretDisplayName);
        string workDir = NewTempDir("runtime-privacy-work");
        var uiContext = new SynchronizationContext();

        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("normal", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            // Give the DiskLogSink's background writer a moment to flush what the poll enqueued.
            await Task.Delay(300);

            Assert.Equal(secretEmail, runtime.Identity?.EmailAddress); // sanity: identity really was read
            Assert.True(Directory.Exists(runtime.CurrentLogDir));

            foreach (string file in Directory.GetFiles(runtime.CurrentLogDir, "*", SearchOption.AllDirectories))
            {
                string content = File.ReadAllText(file);
                Assert.DoesNotContain(secretEmail, content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secretDisplayName, content, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The live bug this whole feature exists to fix (docs/multi-account.md "Identity guard"):
    /// accountUuid identifies the PERSON, not the plan. A `/login` that swaps a config
    /// directory's organisation while accountUuid stays the same (e.g. the same person's Team
    /// and personal Max logins) must still be treated as an account switch and rebuild this
    /// slot's state into a NEW log directory -- keyed on AccountIdentity.StateKey, not
    /// accountUuid alone, or the two plans' percentages would keep sharing one model.
    /// </summary>
    [Fact]
    public async Task PollAsync_SameAccountUuid_OrganizationChanges_RebuildsIntoANewLogDirectory()
    {
        string baseLogDir = NewTempDir("runtime-orgswitch-logs");
        const string sharedAccountUuid = "11111111-1111-4111-8111-111111111111";
        const string teamOrgUuid = "11111111-1111-1111-1111-111111111111";
        const string maxOrgUuid = "22222222-2222-2222-2222-222222222222";
        string configDir = NewTempDir("runtime-orgswitch-cfg");
        WriteIdentity(configDir, sharedAccountUuid, teamOrgUuid);
        string workDir = NewTempDir("runtime-orgswitch-work");
        var uiContext = new SynchronizationContext();

        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("normal", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            string teamLogDir = runtime.CurrentLogDir;
            Assert.Equal(Path.Combine(baseLogDir, $"{sharedAccountUuid}_{teamOrgUuid}"), teamLogDir);
            Assert.Equal($"{sharedAccountUuid}_{teamOrgUuid}", runtime.IdentityKey);

            // Same person (/login stays on the same accountUuid) switches to their personal Max
            // login in the same config directory -- only organizationUuid changes.
            WriteIdentity(configDir, sharedAccountUuid, maxOrgUuid);
            await runtime.PollAsync();
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);

            string maxLogDir = runtime.CurrentLogDir;
            Assert.Equal(Path.Combine(baseLogDir, $"{sharedAccountUuid}_{maxOrgUuid}"), maxLogDir);
            Assert.Equal($"{sharedAccountUuid}_{maxOrgUuid}", runtime.IdentityKey);
            Assert.NotEqual(teamLogDir, maxLogDir); // a same-accountUuid keying would have kept this the SAME directory
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// The task's reload-button requirement: "make sure a forced poll cannot run twice
    /// concurrently for one account", and that it "must not corrupt freshness bookkeeping --
    /// it is just an early poll". RequestImmediateRefresh is a plain alias for PollAsync, whose
    /// pre-existing _polling guard (IsPolling) is what actually enforces the single-flight rule
    /// -- this proves that guard end to end against a real (slowed-down) child, and that once
    /// the legitimate call completes, freshness is exactly what an ordinary accepted poll would
    /// produce (Live), never something disturbed by the overlap.
    /// </summary>
    [Fact]
    public async Task RequestImmediateRefresh_WhileAlreadyInFlight_IsANoOp_AndFreshnessIsUndisturbed()
    {
        string baseLogDir = NewTempDir("runtime-refresh-logs");
        string configDir = NewConfigDirWithIdentity("runtime-refresh-cfg", "dddddddd-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-refresh-work");
        var uiContext = new SynchronizationContext();

        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: SlowFakeSpec(workDir, delayMs: 800), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            Assert.False(runtime.IsPolling);

            // Fire a forced refresh, then a second one while the first is still in flight (the
            // 800ms response delay gives this a wide, deterministic window -- no race with the
            // real child's I/O timing).
            Task first = runtime.RequestImmediateRefresh();
            Assert.True(runtime.IsPolling, "expected the forced refresh to be in flight immediately after starting it");

            Task second = runtime.RequestImmediateRefresh();
            Assert.True(second.IsCompleted, "a second forced refresh while one is already in flight must return immediately, not start a concurrent poll");

            await first;
            Assert.False(runtime.IsPolling);

            // Just an early poll: the model's freshness bookkeeping is exactly what an ordinary
            // accepted poll produces, never disturbed by the overlapping (no-op) second call.
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.Equal(Freshness.Live, runtime.LastView.Freshness);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// docs/multi-account.md "Duplicate accounts", the task's item 1: marking an account a
    /// duplicate must stop its polling outright (never just leave it running unseen), and
    /// resolving it must restart polling exactly like a fresh account would, with no manual
    /// nudge beyond SetDuplicate(false) itself.
    /// </summary>
    [Fact]
    public async Task SetDuplicate_True_StopsPolling_False_ResumesItAndPollsAgain()
    {
        string baseLogDir = NewTempDir("runtime-dup-logs");
        string configDir = NewConfigDirWithIdentity("runtime-dup-cfg", "eeeeeeee-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-dup-work");
        var uiContext = new SynchronizationContext();

        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("normal", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            DateTimeOffset? pollTimeBeforePause = runtime.LastView.LastSuccessAt;

            runtime.SetDuplicate(true, duplicateOfIndex: 0);
            Assert.True(runtime.IsDuplicate);
            Assert.Equal(0, runtime.DuplicateOfIndex);

            // The task's "no poll child" rule: even a direct PollAsync call while marked
            // duplicate must be a no-op -- AccountRuntime.PollAsync's own IsDuplicate guard, not
            // just the (already-stopped) timer.
            await runtime.PollAsync();
            Assert.False(runtime.IsPolling);
            Assert.Equal(pollTimeBeforePause, runtime.LastView.LastSuccessAt);

            DateTimeOffset beforeResume = DateTimeOffset.UtcNow;
            runtime.SetDuplicate(false);
            Assert.False(runtime.IsDuplicate);
            Assert.Null(runtime.DuplicateOfIndex);

            // SetDuplicate(false) itself kicks off an immediate poll (never waits for the timer's
            // next tick) -- proven by a genuinely NEW LastSuccessAt landing after the resume call,
            // not merely freshness staying Live (which would also hold true if resume did nothing).
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.LastSuccessAt > beforeResume;
            }, TimeSpan.FromSeconds(10));
            Assert.Equal(Freshness.Live, runtime.LastView.Freshness);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// docs/multi-account.md "Duplicate accounts": a duplicate's own poll (and therefore its own
    /// SyncIdentityIfChanged) is stopped, so RefreshIdentityOnly is the ONLY thing that can still
    /// notice a login change made directly in its config directory -- without it, a duplicate
    /// could never resolve on its own once the user fixed the underlying login.
    /// </summary>
    [Fact]
    public async Task RefreshIdentityOnly_ReadsANewIdentityFromDisk_WithoutTouchingPollingState()
    {
        string configDir = NewConfigDirWithIdentity("runtime-refreshid-cfg", "11111111-0000-0000-0000-000000000000");
        string baseLogDir = NewTempDir("runtime-refreshid-logs");
        string workDir = NewTempDir("runtime-refreshid-work");
        var uiContext = new SynchronizationContext();

        // .Start() is never called here -- this test only exercises the identity file read, not
        // the channel, matching what a paused (SetDuplicate(true)) account actually does.
        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("normal", workDir), configDirOverride: configDir);

        try
        {
            Assert.Equal("11111111-0000-0000-0000-000000000000", runtime.Identity?.AccountUuid);

            WriteIdentity(configDir, "22222222-0000-0000-0000-000000000000", organizationUuid: null);
            runtime.RefreshIdentityOnly();

            Assert.Equal("22222222-0000-0000-0000-000000000000", runtime.Identity?.AccountUuid);
            Assert.False(runtime.IsPolling); // no channel/poll activity was ever started by this call
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- docs/multi-account.md "NeedsLogin" ----

    static int Launches(string launchLog) => File.Exists(launchLog) ? File.ReadAllLines(launchLog).Length : 0;

    /// <summary>
    /// A child that answers with the "no subscription login" shape: the first answer restarts the
    /// child ONCE (a stale process is the usual cause), and only the fresh child saying the same
    /// thing makes the account NeedsLogin. Exactly two launches -- never a restart loop.
    /// </summary>
    [Fact]
    public async Task NotLoggedIn_RestartsTheChildOnce_ThenAFreshChildSayingTheSameThingIsNeedsLogin()
    {
        string baseLogDir = NewTempDir("runtime-nl-logs");
        string configDir = NewConfigDirWithIdentity("runtime-nl-cfg", "ffffffff-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-nl-work");
        string launchLog = Path.Combine(workDir, "launches.log");
        var uiContext = new SynchronizationContext();
        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec($"not-logged-in --launch-log=\"{launchLog}\"", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();

            await WaitUntilAsync(() => runtime.NeedsLogin, TimeSpan.FromSeconds(20));

            Assert.Equal(2, Launches(launchLog)); // the original child and exactly one replacement
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.True(runtime.LastView.NeedsLogin);
            Assert.Equal(Freshness.Unknown, runtime.LastView.Freshness); // the grey "!" ring

            // Further not-logged-in answers do not restart it again.
            await runtime.PollAsync();
            await Task.Delay(500);
            Assert.Equal(2, Launches(launchLog));
            Assert.True(runtime.NeedsLogin);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>The first child was just stale; its replacement reads fine: no NeedsLogin at all, ever.</summary>
    [Fact]
    public async Task NotLoggedIn_FromAStaleChild_IsCuredByTheOneRestart()
    {
        string baseLogDir = NewTempDir("runtime-nlcure-logs");
        string configDir = NewConfigDirWithIdentity("runtime-nlcure-cfg", "99999999-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-nlcure-work");
        string launchLog = Path.Combine(workDir, "launches.log");
        string marker = Path.Combine(workDir, "first-launch.marker");
        var uiContext = new SynchronizationContext();
        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir,
            specOverride: FakeSpec($"not-logged-in-once --marker=\"{marker}\" --launch-log=\"{launchLog}\"", workDir),
            configDirOverride: configDir);

        try
        {
            runtime.Start();

            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(20));

            Assert.False(runtime.NeedsLogin);
            Assert.False(runtime.LastView.NeedsLogin);
            Assert.Equal(2, Launches(launchLog));
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>Any successful reading clears NeedsLogin (the user logged in again some other way, or the login came back).</summary>
    [Fact]
    public async Task NeedsLogin_ClearsOnTheNextSuccessfulReading()
    {
        string baseLogDir = NewTempDir("runtime-nlclear-logs");
        string configDir = NewConfigDirWithIdentity("runtime-nlclear-cfg", "88888888-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-nlclear-work");
        string marker = Path.Combine(workDir, "login-renewed.marker");
        var uiContext = new SynchronizationContext();
        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec($"not-logged-in-until-marker --marker=\"{marker}\"", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() => runtime.NeedsLogin, TimeSpan.FromSeconds(20));

            File.WriteAllText(marker, "renewed");
            await runtime.PollAsync();

            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return !runtime.NeedsLogin && runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));
            Assert.False(runtime.LastView.NeedsLogin);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>"Senast avläst" is the last SUCCESSFUL read: an account that stops answering keeps the time of its last good reading.</summary>
    [Fact]
    public async Task LastSuccessAt_StaysAtTheLastGoodReading_WhileFailuresPileUp()
    {
        string baseLogDir = NewTempDir("runtime-lastok-logs");
        string configDir = NewConfigDirWithIdentity("runtime-lastok-cfg", "77777777-0000-0000-0000-000000000000");
        string workDir = NewTempDir("runtime-lastok-work");
        var uiContext = new SynchronizationContext();
        var runtime = new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, uiContext,
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec("die-after --count=1", workDir), configDirOverride: configDir);

        try
        {
            runtime.Start();
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.LastSuccessAt is not null;
            }, TimeSpan.FromSeconds(10));
            DateTimeOffset firstRead = runtime.LastView.LastSuccessAt!.Value;

            // The child answered once and exits on the next request: every poll from here on fails.
            await runtime.PollAsync();
            await Task.Delay(300);
            await runtime.PollAsync();
            await Task.Delay(300);

            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.Equal(firstRead, runtime.LastView.LastSuccessAt);
            Assert.NotNull(runtime.LastView.Error);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(baseLogDir, recursive: true);
            Directory.Delete(configDir, recursive: true);
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- slots: an AccountRuntime resolves its config dir through the path guard ----

    [Fact]
    public void Constructor_RefusesASlotThePathGuardRefuses()
    {
        var store = new SlotStore(Path.Combine(Path.GetTempPath(), "csb-rt-guard-" + Guid.NewGuid().ToString("N"), "accounts"));

        Assert.Throws<UnauthorizedAccessException>(() =>
            new AccountRuntime(@"..\x", new AccountEntry { Enabled = true }, 0, new SynchronizationContext(), slots: store));
        Assert.Throws<UnauthorizedAccessException>(() =>
            new AccountRuntime("default", new AccountEntry { Enabled = true }, 0, new SynchronizationContext(), slots: store));
    }

    [Fact]
    public async Task Constructor_ReadsTheIdentityFromTheSlotsConfigDir()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "csb-rt-slot-" + Guid.NewGuid().ToString("N"));
        var store = new SlotStore(Path.Combine(tempRoot, "accounts"));
        string slot = store.CreatePending();
        WriteIdentity(store.ConfigDirOf(slot), "66666666-0000-0000-0000-000000000000", organizationUuid: null);
        var runtime = new AccountRuntime(slot, new AccountEntry { Slot = slot, Enabled = true }, 0, new SynchronizationContext(),
            baseLogDirOverride: Path.Combine(tempRoot, "logs"), slots: store);
        try
        {
            Assert.Equal(slot, runtime.Slot);
            Assert.Equal("66666666-0000-0000-0000-000000000000", runtime.Identity?.AccountUuid);
        }
        finally
        {
            await runtime.DisposeAsync();
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ---- docs/multi-account.md "Loading": from start until the first result, the first failure, or 30 s ----

    AccountRuntime LoadingRuntime(string mode, string tag, out string[] cleanup, TimeSpan? loadingTimeout = null)
    {
        string baseLogDir = NewTempDir($"runtime-{tag}-logs");
        string configDir = NewConfigDirWithIdentity($"runtime-{tag}-cfg", "55555555-0000-0000-0000-000000000000");
        string workDir = NewTempDir($"runtime-{tag}-work");
        cleanup = new[] { baseLogDir, configDir, workDir };
        return new AccountRuntime("0", new AccountEntry { Enabled = true }, 0, new SynchronizationContext(),
            baseLogDirOverride: baseLogDir, specOverride: FakeSpec(mode, workDir), configDirOverride: configDir,
            loadingTimeout: loadingTimeout);
    }

    static void Cleanup(string[] dirs)
    {
        foreach (string d in dirs) { try { Directory.Delete(d, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Loading_ABrandNewRuntime_IsLoading_NotAnError()
    {
        AccountRuntime runtime = LoadingRuntime("hang", "load-new", out string[] dirs);
        try
        {
            Assert.True(runtime.LastView.Loading); // before it is even started
            runtime.Start();
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);

            Assert.True(runtime.LastView.Loading);
            Assert.Equal(Freshness.Unknown, runtime.LastView.Freshness);
            Assert.False(runtime.LastView.NeedsLogin);
        }
        finally
        {
            await runtime.DisposeAsync();
            Cleanup(dirs);
        }
    }

    [Fact]
    public async Task Loading_EndsWithTheFirstSuccessfulRead()
    {
        AccountRuntime runtime = LoadingRuntime("normal", "load-ok", out string[] dirs);
        try
        {
            runtime.Start();

            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return runtime.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));

            Assert.False(runtime.LastView.Loading);
        }
        finally
        {
            await runtime.DisposeAsync();
            Cleanup(dirs);
        }
    }

    [Fact]
    public async Task Loading_EndsWithTheFirstFailure_AfterWhichItIsAnOrdinaryUnknown()
    {
        AccountRuntime runtime = LoadingRuntime("die-immediately", "load-fail", out string[] dirs);
        try
        {
            runtime.Start();

            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return !runtime.LastView.Loading;
            }, TimeSpan.FromSeconds(15));

            Assert.Equal(Freshness.Unknown, runtime.LastView.Freshness);
            Assert.NotNull(runtime.LastView.Error);
            Assert.False(runtime.LastView.NeedsLogin);
        }
        finally
        {
            await runtime.DisposeAsync();
            Cleanup(dirs);
        }
    }

    [Fact]
    public async Task Loading_EndsAfterTheTimeout_EvenWithNoResultAtAll()
    {
        // A child that never answers, and a short stand-in for the 30 s.
        AccountRuntime runtime = LoadingRuntime("hang", "load-timeout", out string[] dirs, TimeSpan.FromMilliseconds(700));
        try
        {
            runtime.Start();
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.True(runtime.LastView.Loading);

            await Task.Delay(900);
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);

            Assert.False(runtime.LastView.Loading);
            Assert.Equal(Freshness.Unknown, runtime.LastView.Freshness); // from here the normal Unknown rules apply
        }
        finally
        {
            await runtime.DisposeAsync();
            Cleanup(dirs);
        }
    }

    [Fact]
    public async Task Loading_ARebuild_StartsItAgain_ForTheNewRuntime()
    {
        AccountRuntime first = LoadingRuntime("normal", "load-rebuild1", out string[] dirs1);
        AccountRuntime? second = null;
        string[] dirs2 = Array.Empty<string>();
        try
        {
            first.Start();
            await WaitUntilAsync(() =>
            {
                first.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                return first.LastView.Freshness == Freshness.Live;
            }, TimeSpan.FromSeconds(10));
            Assert.False(first.LastView.Loading);

            // A rebuild replaces every runtime: the account is "Hämtar kvoten…" again until the new one reads.
            await first.DisposeAsync();
            second = LoadingRuntime("hang", "load-rebuild2", out dirs2);
            second.Start();
            second.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);

            Assert.True(second.LastView.Loading);
        }
        finally
        {
            await first.DisposeAsync();
            if (second is not null) await second.DisposeAsync();
            Cleanup(dirs1);
            Cleanup(dirs2);
        }
    }

    [Fact]
    public async Task Loading_TheFirstNotLoggedInAnswer_IsNotYetAResult_SoNoErrorLookShowsBetweenTheTwoAnswers()
    {
        AccountRuntime runtime = LoadingRuntime("not-logged-in", "load-nl", out string[] dirs);
        try
        {
            runtime.Start();
            bool errorLookSeen = false;

            // The first answer restarts the child; the confirming answer decides. Until then it is still loading.
            await WaitUntilAsync(() =>
            {
                runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
                if (!runtime.LastView.Loading && !runtime.LastView.NeedsLogin) errorLookSeen = true;
                return runtime.NeedsLogin;
            }, TimeSpan.FromSeconds(20));

            Assert.False(errorLookSeen, "between the first and the confirming answer the account looked like a plain error");
            runtime.Evaluate(DateTimeOffset.UtcNow, Environment.TickCount64);
            Assert.False(runtime.LastView.Loading);
            Assert.True(runtime.LastView.NeedsLogin);
        }
        finally
        {
            await runtime.DisposeAsync();
            Cleanup(dirs);
        }
    }
}
