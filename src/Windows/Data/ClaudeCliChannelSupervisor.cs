using System.Diagnostics;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Serialized lifecycle owner for the claude.exe channel (Codex review High #1,
/// #6): starts it, watches its Faulted event, and relaunches with bounded backoff
/// (5s, 15s, 60s, then a 5-minute cap; reset after any healthy poll) whenever it
/// becomes unhealthy -- stdout EOF, process exit, an internal pump crash, a
/// repeating oversized-line pattern, or a request timeout. Exactly one
/// launch/relaunch attempt runs at a time, serialized through _launchGate, so a
/// fault storm can never spawn overlapping children.
///
/// GetUsageAsync is what StatusBarApplicationContext polls through: whenever there
/// is no healthy channel (initial launch still failing, or a relaunch pending) it
/// returns a failure result immediately rather than blocking, so the poll gate
/// this app relies on (StatusBarApplicationContext._polling) can never stay closed
/// waiting on a channel that is not there. Every failure -- including the very
/// first launch attempt -- flows through the exact path
/// StatusBarApplicationContext already uses to call QuotaModel.IngestFailure, so
/// freshness degrades through Stale/Unknown exactly the way an ordinary poll
/// failure does; the caller never needs a second code path for "no channel yet".
///
/// Stopping (docs/multi-account.md "Child lifecycle"): StopAsync marks the supervisor disposed
/// FIRST, so the child's clean exit after its stdin is closed is not treated as a crash and
/// relaunched, then stops the channel gracefully. A launch that is already in flight when the stop
/// begins is disposed as soon as it completes instead of being published into a supervisor nobody
/// will ever stop (the leak this type used to have).
/// </summary>
public sealed class ClaudeCliChannelSupervisor : IAsyncDisposable
{
    static readonly TimeSpan[] DefaultBackoffSteps =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// How long a stopping child gets to exit after its stdin is closed. Measured on Windows
    /// against claude.exe 2.1.292 (`-p --input-format stream-json`, empty config dir): 0.39-0.54 s
    /// from closing stdin to exit, with or without a request served first. Four times the worst
    /// observation; a child that outlasts it is killed.
    /// </summary>
    public static readonly TimeSpan DefaultExitWait = TimeSpan.FromSeconds(2);

    readonly ChildProcessSpec _spec;
    readonly DiskLogSink _logSink;
    readonly TimeSpan[] _backoffSteps;
    readonly SemaphoreSlim _launchGate = new(1, 1);
    readonly CancellationTokenSource _lifetimeCts = new();

    readonly System.Collections.Concurrent.ConcurrentBag<(int Pid, DateTime StartTime)> _launched = new();

    ClaudeCliChannel? _channel;
    volatile bool _disposed;
    volatile string? _lastFailureReason = "channel not started yet";
    int _backoffIndex;

    /// <param name="backoffSteps">Test-only: replaces the relaunch backoff schedule.</param>
    public ClaudeCliChannelSupervisor(ChildProcessSpec spec, DiskLogSink logSink, TimeSpan[]? backoffSteps = null)
    {
        _spec = spec;
        _logSink = logSink;
        _backoffSteps = backoffSteps is { Length: > 0 } ? backoffSteps : DefaultBackoffSteps;
    }

    /// <summary>Fire-and-forget: kicks off the first launch attempt without blocking the caller (the UI thread, at construction time).</summary>
    public void Start() => _ = LaunchWithRecoveryAsync(TimeSpan.Zero);

    public async Task<UsageReadResult> GetUsageAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ClaudeCliChannel? channel = Volatile.Read(ref _channel);
        if (channel is null || !channel.IsHealthy)
            return new UsageReadResult(null, _lastFailureReason ?? "channel not connected", TimeSpan.Zero);

        UsageReadResult result = await channel.GetUsageAsync(timeout, ct).ConfigureAwait(false);
        if (result.Snapshot != null)
            Volatile.Write(ref _backoffIndex, 0); // reset after a healthy poll
        else
            _lastFailureReason = result.Error;
        return result;
    }

    /// <summary>
    /// Last resort after a stop that did not finish in time: kills every child THIS supervisor started
    /// that is still running (matched by process id AND start time, so a different process that merely
    /// reused an id is never touched) and waits for each to be gone. Returns how many were killed.
    /// </summary>
    public int KillLaunchedChildren()
    {
        int killed = 0;
        foreach ((int pid, DateTime startTime) in _launched)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited || p.StartTime != startTime) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
                killed++;
            }
            catch
            {
                // already gone, or not ours any more
            }
        }
        return killed;
    }

    /// <summary>The live child's process id, or null while there is none (not started yet, or between relaunches).</summary>
    public int? CurrentProcessId => Volatile.Read(ref _channel)?.ProcessId;

    async Task LaunchWithRecoveryAsync(TimeSpan initialDelay)
    {
        if (initialDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(initialDelay, _lifetimeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        try
        {
            await _launchGate.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_disposed) return;

            try
            {
                ClaudeCliChannel channel = ClaudeCliChannel.Start(_spec, _logSink);
                channel.Faulted += OnChannelFaulted;
                _launched.Add((channel.ProcessId, channel.ProcessStartTime));
                Volatile.Write(ref _channel, channel);

                // The stop may have begun while Start() was running -- it cannot know about this
                // channel yet, so nothing else will ever stop it. Publishing first and re-checking
                // second means either this check or the stop's own Exchange sees the channel.
                if (_disposed)
                {
                    ClaudeCliChannel? leaked = Interlocked.Exchange(ref _channel, null);
                    if (leaked != null)
                    {
                        leaked.Faulted -= OnChannelFaulted;
                        try { leaked.Dispose(); } catch { /* best effort */ }
                    }
                    return;
                }

                _lastFailureReason = null;
                SafeLog.Info("claude.exe channel started");
            }
            catch (Exception ex)
            {
                // Startup failure is reported to the model the same way any other poll
                // failure is (Codex review High #1): GetUsageAsync above returns this reason
                // whenever _channel is still null, which flows into QuotaModel.IngestFailure
                // exactly like a transport failure would.
                _lastFailureReason = $"claude.exe channel failed to start: {ex.Message}";
                SafeLog.Warn(_lastFailureReason);
                ScheduleRelaunch();
            }
        }
        finally
        {
            _launchGate.Release();
        }
    }

    void OnChannelFaulted(ClaudeCliChannel channel, string reason)
    {
        if (_disposed) return;
        _lastFailureReason = reason;
        channel.Faulted -= OnChannelFaulted;
        Interlocked.CompareExchange(ref _channel, null, channel); // stop routing new polls to a dead channel immediately

        try { channel.Dispose(); } catch { /* best effort */ }

        ScheduleRelaunch();
    }

    void ScheduleRelaunch()
    {
        if (_disposed) return;
        int index = Interlocked.Increment(ref _backoffIndex) - 1;
        TimeSpan delay = _backoffSteps[Math.Min(Math.Max(index, 0), _backoffSteps.Length - 1)];
        SafeLog.Info($"claude.exe channel relaunch scheduled in {delay.TotalSeconds:F0}s");
        _ = LaunchWithRecoveryAsync(delay);
    }

    /// <summary>Graceful stop with the measured default exit wait (see DefaultExitWait).</summary>
    public ValueTask DisposeAsync() => StopAsync(DefaultExitWait);

    /// <summary>
    /// Stops the child gracefully: disposed first (so its clean exit is not relaunched), stdin
    /// closed under the channel's stdin lock, a bounded wait for it to exit by itself, then a
    /// kill of the whole process tree and a wait for that to complete.
    /// </summary>
    public async ValueTask StopAsync(TimeSpan exitWait)
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();

        ClaudeCliChannel? channel = Interlocked.Exchange(ref _channel, null);
        if (channel != null)
        {
            channel.Faulted -= OnChannelFaulted;
            try { await channel.StopAsync(exitWait).ConfigureAwait(false); }
            catch { /* best effort: StopAsync falls through to Dispose on its own failures */ }
        }

        // Let any in-flight launch attempt notice cancellation and exit before we return.
        bool acquired = false;
        try { acquired = await _launchGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* best effort */ }
        finally { if (acquired) _launchGate.Release(); }

        // A launch that was inside Start() when this began has published its channel by now (the
        // gate wait above covers it); if it slipped past its own _disposed check, stop it here.
        ClaudeCliChannel? late = Interlocked.Exchange(ref _channel, null);
        if (late != null)
        {
            late.Faulted -= OnChannelFaulted;
            try { late.Dispose(); } catch { /* best effort */ }
        }
    }
}
