using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClaudeStatusBar.Diagnostics;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Owns one long-lived `claude.exe -p --input-format stream-json --output-format
/// stream-json --verbose` child. Requests are correlated on request_id through a
/// ConcurrentDictionary of TaskCompletionSources; every raw line in and out, plus
/// every successful poll's window-shape row, is handed to a DiskLogSink -- ALL
/// disk I/O for this channel happens off the pipe-reading and request-completion
/// paths (Codex review High #4).
///
/// This type owns exactly one child process for its own lifetime: it does not
/// retry, relaunch, or otherwise recover from the child dying or the channel
/// becoming unhealthy. That is ClaudeCliChannelSupervisor's job -- it starts a new
/// ClaudeCliChannel, watches the Faulted event, and disposes+replaces it with
/// bounded backoff. See that type's doc comment for the full lifecycle contract
/// (Codex review High #1, #6).
///
/// Spawning this child creates a real Claude Code session and fires SessionStart
/// hooks, so its working directory is a dedicated agent folder, never a user repo.
/// </summary>
public sealed class ClaudeCliChannel : IDisposable
{
    const int OverflowRecycleThreshold = 3;
    static readonly long OverflowWindowMs = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

    readonly Process _process;
    readonly DiskLogSink _logSink;
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
    readonly SemaphoreSlim _stdinLock = new(1, 1);
    readonly object _overflowLock = new();
    readonly Queue<long> _overflowTimesMono = new();
    IntPtr _jobHandle;
    long _nextRequestId;
    int _faultedFlag; // 0 = healthy, 1 = faulted (Interlocked-guarded so MarkFaulted is idempotent)
    string? _faultReason;
    bool _disposed;

    /// <summary>Raised exactly once per channel instance, the moment it is judged unhealthy (stdout EOF, process exit, pump crash, an oversized-line pattern, or a request timeout). The supervisor disposes this channel and relaunches on this signal.</summary>
    public event Action<ClaudeCliChannel, string>? Faulted;

    public bool IsHealthy => Volatile.Read(ref _faultedFlag) == 0 && !_disposed;

    ClaudeCliChannel(Process process, DiskLogSink logSink, IntPtr jobHandle)
    {
        _process = process;
        _logSink = logSink;
        _jobHandle = jobHandle;
    }

    public static ClaudeCliChannel Start(ChildProcessSpec spec, DiskLogSink logSink)
    {
        // Re-resolved on every call (Codex review High #1): claude.exe auto-update replaces
        // the binary at this path, so a path cached once at app start can go stale across a
        // long-running supervised session. The supervisor calls Start() fresh on every
        // relaunch, so this always sees the current binary.
        string claudeExe = spec.ResolveExePath();
        if (!File.Exists(claudeExe))
            throw new FileNotFoundException("claude.exe not found", claudeExe);

        Directory.CreateDirectory(spec.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = claudeExe,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory, // dedicated folder: this is a real session, must not run inside a user repo
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
        };

        // Job Object is created and configured BEFORE the process starts (Codex review High
        // #2): the previous ordering left the child able to run entirely unsandboxed if job
        // creation/configuration itself failed after the process was already live. Failing
        // closed here means an unsandboxed child is never launched at all.
        //
        // This is still not the fully atomic guarantee: the native
        // PROC_THREAD_ATTRIBUTE_JOB_LIST process-creation attribute assigns the job as part
        // of CreateProcess itself, closing the gap completely (including a parent crash
        // between Process.Start returning and AssignProcessToJobObject succeeding, and any
        // descendant the child spawns in that same window). That path needs native
        // CreateProcess via P/Invoke -- Process.Start has no supported hook for job-list
        // process attributes -- and is NOT implemented here; the remaining gap is the few
        // milliseconds between Process.Start returning and the assignment below, during
        // which an abnormal (non-normal-exit) kill of this process could in theory let an
        // already-spawned descendant escape. Assignment happens with no intervening work, so
        // this window is as small as this process model allows without that native path.
        IntPtr job = JobObject.CreateKillOnCloseJob();
        if (job == IntPtr.Zero)
            throw new InvalidOperationException("failed to create kill-on-close job object; refusing to launch an unsandboxed claude.exe child");

        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for claude.exe");

            if (!JobObject.AssignProcessToJobObject(job, process.Handle))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"claude.exe could not be assigned to the kill-on-close job object, GetLastWin32Error={err}; failing closed rather than running it unsandboxed");
            }

            process.EnableRaisingEvents = true;
            var channel = new ClaudeCliChannel(process, logSink, job);
            process.Exited += (_, _) => channel.MarkFaulted("child process exited");
            channel.StartPumps();
            return channel;
        }
        catch
        {
            // Ownership-transfer cleanup (Codex review High #2): any exception past this
            // point -- job assignment failure, pump startup failure, whatever -- must not
            // strand the child process or the raw job handle.
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { process?.Dispose(); } catch { /* best effort */ }
            JobObject.Close(job);
            throw;
        }
    }

    void StartPumps()
    {
        new Thread(StdoutPump) { IsBackground = true, Name = "claude-cli-stdout" }.Start();
        // Must drain stderr on its own thread: a full stderr pipe deadlocks the child.
        new Thread(StderrPump) { IsBackground = true, Name = "claude-cli-stderr" }.Start();
    }

    void StdoutPump()
    {
        try
        {
            PumpStream(_process.StandardOutput, "out", HandleLine);
            MarkFaulted("stdout EOF"); // ReadLine-equivalent loop ended cleanly: the child closed stdout
        }
        catch (Exception ex)
        {
            MarkFaulted($"stdout pump crashed: {ex.Message}");
        }
    }

    void StderrPump()
    {
        try
        {
            PumpStream(_process.StandardError, "stderr", static _ => { }); // already logged raw by PumpStream; nothing else to do with it
        }
        catch
        {
            // pipe closed on shutdown; nothing actionable -- stdout is the primary health signal.
        }
    }

    /// <summary>
    /// Bounded incremental framing (Codex review High #7) shared by both pumps: reads raw
    /// chunks (never a whole unbounded line at once) and feeds them through a LineFramer,
    /// which caps any single record to LineFramer.MaxRecordChars and reports oversized
    /// records as Overflow instead of growing memory without limit.
    /// </summary>
    void PumpStream(TextReader reader, string source, Action<string> onLine)
    {
        var framer = new LineFramer();
        char[] buffer = new char[8192];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            foreach (FrameEvent evt in framer.Feed(buffer.AsSpan(0, read)))
            {
                if (evt.Kind == FrameEventKind.Overflow)
                {
                    _logSink.EnqueueRaw("fault", $"{source} line exceeded {LineFramer.MaxRecordChars} chars; discarded to next newline");
                    RecordOverflow(source);
                    continue;
                }

                try
                {
                    _logSink.EnqueueRaw(source, evt.Line!);
                    onLine(evt.Line!);
                }
                catch (Exception ex)
                {
                    // Per-line isolation (Codex review High #6): one bad line must never end the pump.
                    _logSink.EnqueueRaw("fault", $"{source} line handling threw: {ex.Message}");
                }
            }
        }
    }

    /// <summary>A repeating oversized-line pattern (3x within 10 minutes) recycles the channel outright, on the theory that a child stuck emitting garbage is unlikely to self-correct.</summary>
    void RecordOverflow(string source)
    {
        long now = Environment.TickCount64;
        bool shouldRecycle;
        lock (_overflowLock)
        {
            _overflowTimesMono.Enqueue(now);
            while (_overflowTimesMono.Count > 0 && now - _overflowTimesMono.Peek() > OverflowWindowMs)
                _overflowTimesMono.Dequeue();
            shouldRecycle = _overflowTimesMono.Count >= OverflowRecycleThreshold;
        }
        if (shouldRecycle)
            MarkFaulted($"{source}: {OverflowRecycleThreshold} oversized lines within 10 minutes; recycling channel");
    }

    /// <summary>
    /// Parses one stdout line into a control_response envelope and, if it matches a pending
    /// request, hands the JsonDocument off to that request. Every path is defensive (Codex
    /// review High #6, Medium #9): an unexpected but valid envelope (e.g.
    /// {"type":"control_response","response":null}) is logged and skipped rather than
    /// throwing, and the JsonDocument is disposed on every path where ownership is not
    /// actually transferred to a waiting caller -- including when TrySetResult returns
    /// false because that request already timed out or was canceled.
    /// </summary>
    void HandleLine(string line)
    {
        EnvelopeParser.EnvelopeParseResult result = EnvelopeParser.Parse(line);
        bool transferred = false;
        try
        {
            switch (result.Kind)
            {
                case EnvelopeKind.ParseError:
                    _logSink.EnqueueRaw("parse-error", result.Error ?? "unknown parse error");
                    return;

                case EnvelopeKind.ControlResponseMatched:
                    if (_pending.TryRemove(result.RequestId!, out var tcs))
                        transferred = tcs.TrySetResult(result.Document!);
                    return;

                default:
                    // Not a control_response we act on (unmatched request_id, or a structurally
                    // valid-but-unexpected envelope) -- nothing owns it.
                    return;
            }
        }
        catch (Exception ex)
        {
            _logSink.EnqueueRaw("fault", $"HandleLine threw: {ex.Message}");
        }
        finally
        {
            if (!transferred) result.Document?.Dispose();
        }
    }

    /// <summary>Sends get_usage and awaits the correlated response, or times out. Returns promptly with a failure result whenever the channel is not healthy -- the poll gate must never stay closed forever (Codex review High #1, #3).</summary>
    public async Task<(UsageSnapshot? Snapshot, string? Error, TimeSpan Latency)> GetUsageAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsHealthy)
            return (null, _faultReason ?? "channel is not healthy", TimeSpan.Zero);

        string requestId = Interlocked.Increment(ref _nextRequestId).ToString();
        var tcs = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        var sw = Stopwatch.StartNew();
        // The deadline starts here, BEFORE semaphore acquisition (Codex review High #3), and
        // is threaded through acquisition, write and flush below -- the operations most
        // likely to hang if the child stops reading stdin.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        bool acquiredLock = false;
        try
        {
            await _stdinLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            acquiredLock = true;

            string payload = $"{{\"type\":\"control_request\",\"request_id\":\"{requestId}\",\"request\":{{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}}}";
            _logSink.EnqueueRaw("in", payload);
            await _process.StandardInput.WriteAsync(payload.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await _process.StandardInput.WriteAsync("\n".AsMemory(), timeoutCts.Token).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(timeoutCts.Token).ConfigureAwait(false);

            _stdinLock.Release();
            acquiredLock = false;

            using CancellationTokenRegistration reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
            using JsonDocument doc = await tcs.Task.ConfigureAwait(false);
            sw.Stop();

            UsageSnapshot? snapshot = UsageParser.TryParse(doc.RootElement, out string? error);
            if (snapshot != null) _logSink.EnqueueCsv(snapshot, sw.Elapsed);
            return (snapshot, error, sw.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            // Codex review High #3: this can be acquisition, write, flush, or waiting for the
            // response -- in every case the channel must be recycled, not merely abandoned.
            // Abandoning the task alone leaves the writer and the stdin lock stuck forever if
            // the child truly stopped reading; MarkFaulted fails every other pending request
            // too and lets the supervisor kill+dispose+relaunch, which is what actually
            // unblocks a stuck OS-level pipe write.
            string message = $"get_usage timed out after {timeout.TotalMilliseconds:F0}ms";
            MarkFaulted(message);
            return (null, message, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return (null, "get_usage canceled by caller", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            MarkFaulted($"get_usage failed: {ex.Message}"); // a write/flush exception leaves the pipe in an unknown state
            return (null, ex.Message, sw.Elapsed);
        }
        finally
        {
            if (acquiredLock) _stdinLock.Release();
            _pending.TryRemove(requestId, out _); // clean up on every path, including timeout
        }
    }

    /// <summary>Idempotent: only the first call does anything. Fails every pending request and raises Faulted exactly once so the supervisor can recycle this channel.</summary>
    void MarkFaulted(string reason)
    {
        if (Interlocked.CompareExchange(ref _faultedFlag, 1, 0) != 0) return;
        _faultReason = reason;
        _logSink.EnqueueRaw("fault", $"channel marked unhealthy: {reason}");

        // The poll gate must never stay closed forever (Codex review High #1, #3): every
        // request still waiting on this channel is failed right now rather than left to time
        // out on its own.
        foreach (string key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(new IOException($"claude.exe channel became unhealthy: {reason}"));
        }

        try
        {
            Faulted?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            _logSink.EnqueueRaw("fault", $"Faulted subscriber threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (string key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetCanceled();
        }

        // Kill first (Codex review High #5): closing StandardInput before the child is dead
        // can flush synchronously and block if the child is not reading, which on the UI
        // shutdown path would hang the app before ever reaching the kill below. Once the
        // child (and, via the job object, its descendants) is gone, closing the pipe cannot
        // block on anything.
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { _process.StandardInput.Close(); } catch { /* already gone */ }
        try { _process.Dispose(); } catch { /* already gone */ }

        if (_jobHandle != IntPtr.Zero)
        {
            JobObject.Close(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Minimal Win32 Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: the claude.exe
/// child is assigned to this job immediately after spawn, so if this process dies
/// unexpectedly the child dies with it rather than surviving as an orphan. A fuller
/// Job Object wrapper (CPU/memory limits, Interop/JobObject.cs) is M8 -- this is
/// the minimal version needed to avoid the worst M0 failure mode.
/// </summary>
static class JobObject
{
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoType, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    public static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        uint size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
        {
            SafeLog.Warn($"SetInformationJobObject(KILL_ON_JOB_CLOSE) failed, GetLastWin32Error={Marshal.GetLastWin32Error()}");
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    public static void Close(IntPtr job) => CloseHandle(job);
}
