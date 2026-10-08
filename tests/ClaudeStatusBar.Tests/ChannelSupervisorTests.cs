using System.Diagnostics;
using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Integration tests against tests/FakeClaudeChild, a real console process standing in for
/// claude.exe, driven through the exact same ChildProcessSpec/ClaudeCliChannelSupervisor path
/// production code uses. Covers the TESTABILITY section of the runtime-hardening task: the
/// supervisor relaunches with backoff, the poll gate never stays stuck, a bad envelope
/// doesn't kill the pump, an oversized line is discarded, and no process is left running
/// after the test (asserted by PID, scoped to processes this test itself started).
/// </summary>
[Collection("process")]
public class ChannelSupervisorTests
{
    static string ExePath => Path.Combine(AppContext.BaseDirectory, "FakeClaudeChild.exe");

    static ChildProcessSpec BuildSpec(string args, string workDir) => new(
        ResolveExePath: () => ExePath,
        Arguments: args,
        WorkingDirectory: workDir);

    static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static async Task<UsageReadResult> PollUntilSuccessAsync(
        ClaudeCliChannelSupervisor supervisor, TimeSpan perAttemptTimeout, TimeSpan maxWait)
    {
        DateTime deadline = DateTime.UtcNow + maxWait;
        UsageReadResult result = new(null, "not attempted", TimeSpan.Zero);
        while (DateTime.UtcNow < deadline)
        {
            result = await supervisor.GetUsageAsync(perAttemptTimeout).ConfigureAwait(false);
            if (result.Snapshot != null) return result;
            await Task.Delay(150).ConfigureAwait(false);
        }
        return result;
    }

    static async Task AssertNoOrphanedFakeChildrenAsync(DateTime testStartedUtc, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            List<Process> leftover = Process.GetProcessesByName("FakeClaudeChild")
                .Where(p => StartedDuringThisTest(p, testStartedUtc))
                .ToList();
            if (leftover.Count == 0) return;
            if (DateTime.UtcNow > deadline)
            {
                string pids = string.Join(",", leftover.Select(p => p.Id));
                Assert.Fail($"orphaned FakeClaudeChild process(es) survived shutdown: PID(s) {pids}");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    static bool StartedDuringThisTest(Process p, DateTime testStartedUtc)
    {
        try { return !p.HasExited && p.StartTime.ToUniversalTime() >= testStartedUtc.AddSeconds(-2); }
        catch { return false; }
    }

    [Fact]
    public async Task Normal_GetUsageAsync_ReturnsASnapshot()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-normal-work");
        string logDir = NewTempDir("chan-normal-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=normal", workDir), logSink);
        try
        {
            supervisor.Start();
            var (snapshot, error, _) = await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

            Assert.NotNull(snapshot);
            Assert.Null(error);
            Assert.Equal(42.0, snapshot!.SessionUtilization);
            Assert.Equal(13.5, snapshot.WeeklyUtilization);
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Hang_TimesOutPromptly_PollGateNeverStaysStuck()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-hang-work");
        string logDir = NewTempDir("chan-hang-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=hang", workDir), logSink);
        try
        {
            supervisor.Start();
            await Task.Delay(300); // let the child actually start before we race it with a request

            var sw = Stopwatch.StartNew();
            var (snapshot, error, _) = await supervisor.GetUsageAsync(TimeSpan.FromSeconds(1));
            sw.Stop();

            Assert.Null(snapshot);
            Assert.NotNull(error);
            // The call must return promptly (bounded by the timeout), never hang forever --
            // this is the poll gate itself (Codex review High #3).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"GetUsageAsync took {sw.Elapsed} against a 1s timeout -- the poll gate got stuck");
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task NullResponseEnvelope_DoesNotKillTheStdoutPump()
    {
        // Codex review High #6's exact reproduction: the child emits one unsolicited
        // {"type":"control_response","response":null} line before ever seeing a request. The
        // stdout pump must survive it and go on to answer a real request normally.
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-nullresp-work");
        string logDir = NewTempDir("chan-nullresp-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=null-response-then-normal", workDir), logSink);
        try
        {
            supervisor.Start();
            var (snapshot, error, _) = await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

            Assert.NotNull(snapshot);
            Assert.Null(error);
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedLine_IsDiscarded_RequestTimesOutPromptlyRatherThanHanging()
    {
        // Codex review High #7's exact reproduction: a single ~5MB line with no trailing
        // newline. The framer must discard it (bounded memory) rather than ever completing a
        // line from it, and the pending request must still time out promptly, not hang.
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-oversize-work");
        string logDir = NewTempDir("chan-oversize-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=oversized-line", workDir), logSink);
        try
        {
            supervisor.Start();
            await Task.Delay(500); // give the child time to write its oversized line

            var sw = Stopwatch.StartNew();
            var (snapshot, error, _) = await supervisor.GetUsageAsync(TimeSpan.FromSeconds(1));
            sw.Stop();

            Assert.Null(snapshot);
            Assert.NotNull(error);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"GetUsageAsync took {sw.Elapsed} against a 1s timeout");
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task DiesImmediately_SupervisorRelaunchesWithBackoff_PollsSucceedAgain()
    {
        // Codex review High #1: the child dies (here: on every launch until a marker file
        // exists), and the supervisor must relaunch on its own bounded backoff (first step:
        // 5s) rather than leaving the channel permanently dead. Once the marker exists, the
        // next relaunch behaves normally and a poll succeeds.
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-dieonce-work");
        string logDir = NewTempDir("chan-dieonce-logs");
        string marker = Path.Combine(NewTempDir("chan-dieonce-marker"), "died.marker");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec($"--mode=die-once --marker=\"{marker}\"", workDir), logSink);
        try
        {
            supervisor.Start();
            // First launch dies immediately; the supervisor's first backoff step is 5s, so
            // allow comfortably past that for the relaunch to land and a poll to succeed.
            var (snapshot, error, _) = await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));

            Assert.NotNull(snapshot);
            Assert.Null(error);
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(marker)!, recursive: true);
        }
    }

    // ---- docs/multi-account.md "Child lifecycle": graceful stop ----

    [Fact]
    public async Task Stop_IsGraceful_TheChildSeesItsStdinClose_RatherThanBeingKilled()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-graceful-work");
        string logDir = NewTempDir("chan-graceful-logs");
        string eofMarker = Path.Combine(workDir, "stdin-closed.marker");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec($"--mode=normal --on-eof-marker=\"{eofMarker}\"", workDir), logSink);
        try
        {
            supervisor.Start();
            var (snapshot, _, _) = await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            Assert.NotNull(snapshot);
            Assert.False(File.Exists(eofMarker));

            await supervisor.DisposeAsync();

            // A kill never reaches the child's EOF handling; closing stdin does.
            Assert.True(File.Exists(eofMarker), "the child was killed instead of being asked to finish");
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_TheCleanExitItCausesIsNotMistakenForACrash_NoRelaunch()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-norestart-work");
        string logDir = NewTempDir("chan-norestart-logs");
        string launchLog = Path.Combine(workDir, "launches.log");
        var logSink = new DiskLogSink(logDir);
        // A 200 ms first backoff step: were the clean exit treated as a crash, a second child would be up well within the wait below.
        var supervisor = new ClaudeCliChannelSupervisor(
            BuildSpec($"--mode=normal --launch-log=\"{launchLog}\"", workDir), logSink,
            backoffSteps: new[] { TimeSpan.FromMilliseconds(200) });
        try
        {
            supervisor.Start();
            var (snapshot, _, _) = await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
            Assert.NotNull(snapshot);

            await supervisor.DisposeAsync();
            await Task.Delay(1500);

            Assert.Single(File.ReadAllLines(launchLog));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(2));
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_ALaunchStillInFlight_IsNotLeakedAsAnUnownedChild()
    {
        // The leak this guards: DisposeAsync ran while ClaudeCliChannel.Start() was still inside
        // the supervisor, so the channel it produced was published into a supervisor nobody would
        // ever stop again -- a claude.exe child that outlived its account.
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-leak-work");
        string logDir = NewTempDir("chan-leak-logs");
        var logSink = new DiskLogSink(logDir);
        var slowResolve = new ChildProcessSpec(
            ResolveExePath: () => { Thread.Sleep(700); return ExePath; }, // the launch is "in flight" for 700 ms
            Arguments: "--mode=normal",
            WorkingDirectory: workDir);
        var supervisor = new ClaudeCliChannelSupervisor(slowResolve, logSink);
        try
        {
            // Start() runs its first launch attempt on the caller's thread until it first awaits, so it is run on its own thread here.
            Task starting = Task.Run(supervisor.Start);
            await Task.Delay(150); // the launch is now inside ResolveExePath
            await supervisor.DisposeAsync();
            await starting;
            await Task.Delay(300);

            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(3));
            Assert.Null(supervisor.CurrentProcessId);
        }
        finally
        {
            // If the assertion above failed, a leaked child is still alive with its working directory
            // locked: reap it so the cleanup below cannot mask the real failure with an IOException.
            foreach (Process leaked in Process.GetProcessesByName("FakeClaudeChild").Where(p => StartedDuringThisTest(p, testStart)))
            {
                try { leaked.Kill(entireProcessTree: true); leaked.WaitForExit(2000); } catch { /* already gone */ }
            }
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_AChildThatIgnoresItsStdinClose_IsKilledAfterTheBoundedWait_AndIsGone()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-stubborn-work");
        string logDir = NewTempDir("chan-stubborn-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=hang", workDir), logSink);
        try
        {
            supervisor.Start();
            await Task.Delay(500); // let the child actually start
            Assert.NotNull(supervisor.CurrentProcessId);
            int pid = supervisor.CurrentProcessId!.Value;

            var sw = Stopwatch.StartNew();
            await supervisor.StopAsync(TimeSpan.FromMilliseconds(600));
            sw.Stop();

            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(500), $"gave up on the polite route after only {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"stop took {sw.Elapsed}");
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); // killed AND gone, not just signalled
        }
        finally
        {
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_IsIdempotent()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-idem-work");
        string logDir = NewTempDir("chan-idem-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=normal", workDir), logSink);
        try
        {
            supervisor.Start();
            await PollUntilSuccessAsync(supervisor, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));

            await supervisor.DisposeAsync();
            await supervisor.DisposeAsync();
            await supervisor.StopAsync(TimeSpan.FromMilliseconds(100));
        }
        finally
        {
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public async Task KillLaunchedChildren_KillsWhatTheSupervisorStarted_AndOnlyThat()
    {
        DateTime testStart = DateTime.UtcNow;
        string workDir = NewTempDir("chan-killlaunched-work");
        string logDir = NewTempDir("chan-killlaunched-logs");
        var logSink = new DiskLogSink(logDir);
        var supervisor = new ClaudeCliChannelSupervisor(BuildSpec("--mode=hang", workDir), logSink);
        // A bystander the supervisor did not start: it must survive.
        using Process bystander = Process.Start(new ProcessStartInfo(ExePath, "--mode=hang") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        try
        {
            supervisor.Start();
            await Task.Delay(500);
            int pid = supervisor.CurrentProcessId!.Value;

            int killed = supervisor.KillLaunchedChildren();

            Assert.Equal(1, killed);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.False(bystander.HasExited, "a process the supervisor did not start was killed");
        }
        finally
        {
            try { bystander.Kill(entireProcessTree: true); bystander.WaitForExit(3000); } catch { /* already gone */ }
            await supervisor.DisposeAsync();
            await logSink.DisposeAsync(TimeSpan.FromSeconds(2));
            await AssertNoOrphanedFakeChildrenAsync(testStart, TimeSpan.FromSeconds(5));
            Directory.Delete(workDir, recursive: true);
            Directory.Delete(logDir, recursive: true);
        }
    }
}
