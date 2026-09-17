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
}
