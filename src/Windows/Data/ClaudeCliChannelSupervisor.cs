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
/// </summary>
public sealed class ClaudeCliChannelSupervisor : IAsyncDisposable
{
    static readonly TimeSpan[] BackoffSteps =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5),
    };

    readonly ChildProcessSpec _spec;
    readonly DiskLogSink _logSink;
    readonly SemaphoreSlim _launchGate = new(1, 1);
    readonly CancellationTokenSource _lifetimeCts = new();

    ClaudeCliChannel? _channel;
    volatile bool _disposed;
    volatile string? _lastFailureReason = "channel not started yet";
    int _backoffIndex;

    public ClaudeCliChannelSupervisor(ChildProcessSpec spec, DiskLogSink logSink)
    {
        _spec = spec;
        _logSink = logSink;
    }

    /// <summary>Fire-and-forget: kicks off the first launch attempt without blocking the caller (the UI thread, at construction time).</summary>
    public void Start() => _ = LaunchWithRecoveryAsync(TimeSpan.Zero);

    public async Task<(UsageSnapshot? Snapshot, string? Error, TimeSpan Latency)> GetUsageAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ClaudeCliChannel? channel = Volatile.Read(ref _channel);
        if (channel is null || !channel.IsHealthy)
            return (null, _lastFailureReason ?? "channel not connected", TimeSpan.Zero);

        (UsageSnapshot? snapshot, string? error, TimeSpan latency) = await channel.GetUsageAsync(timeout, ct).ConfigureAwait(false);
        if (snapshot != null)
            Volatile.Write(ref _backoffIndex, 0); // reset after a healthy poll
        else
            _lastFailureReason = error;
        return (snapshot, error, latency);
    }

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
                Volatile.Write(ref _channel, channel);
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
        TimeSpan delay = BackoffSteps[Math.Min(Math.Max(index, 0), BackoffSteps.Length - 1)];
        SafeLog.Info($"claude.exe channel relaunch scheduled in {delay.TotalSeconds:F0}s");
        _ = LaunchWithRecoveryAsync(delay);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();

        ClaudeCliChannel? channel = Interlocked.Exchange(ref _channel, null);
        if (channel != null)
        {
            channel.Faulted -= OnChannelFaulted;
            try { channel.Dispose(); } catch { /* best effort */ }
        }

        // Let any in-flight launch attempt notice cancellation and exit before we return.
        bool acquired = false;
        try { acquired = await _launchGate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* best effort */ }
        finally { if (acquired) _launchGate.Release(); }
    }
}
