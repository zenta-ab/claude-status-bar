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

    static async Task<(UsageSnapshot? Snapshot, string? Error, TimeSpan Latency)> PollUntilSuccessAsync(
        ClaudeCliChannelSupervisor supervisor, TimeSpan perAttemptTimeout, TimeSpan maxWait)
    {
        DateTime deadline = DateTime.UtcNow + maxWait;
        (UsageSnapshot? Snapshot, string? Error, TimeSpan Latency) result = (null, "not attempted", TimeSpan.Zero);
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
}
