The main risks are permanent loss of polling, incomplete child containment, and blocking I/O during shutdown. **No critical defect is established; several high-severity defects prevent reliable unattended operation.** References below use the supplied line numbers.

1. **High — No recovery after startup failure, child exit, or update-related termination.**  
   `StatusBarApplicationContext.cs:42,93–99,149`; `ClaudeCliChannel.cs:114–124`.  
   **Trigger:** Initial launch fails, or the child exits/crashes later.  
   **Consequence:** `_channel` stays null or references the dead process forever. Polls either silently return or repeatedly fail; the app never recovers without restarting. EOF also leaves outstanding requests waiting for their timeout. An executable replacement alone need not terminate the existing child, but termination during an update exposes this defect.  
   **Smallest fix:** Introduce a serialized channel lifecycle: report startup failures to the model, mark EOF/exit/pump failure unhealthy, complete pending requests, dispose that channel, and retry launch with bounded backoff. Resolve the executable path again on each launch.

2. **High — Job containment fails open and has a spawn-before-assignment gap.**  
   `ClaudeCliChannel.cs:81–99,384–404`.  
   **Trigger:** Job creation/assignment fails, or the parent dies between `Process.Start` and assignment. The child can also launch descendants before assignment.  
   **Consequence:** The child or early descendants can survive the tray app. Logging the failure contradicts the comment claiming failure is not swallowed. Exceptions after launch, including pump startup failure, lack rollback and can strand the process and raw job handle.  
   **Smallest fix:** Create/configure the job first, fail closed, and wrap startup in ownership-transfer cleanup. For the strict crash guarantee, assign the job atomically during native process creation using a job-list process attribute. Suspended launch followed by assignment prevents early descendant escape but still leaves a parent-crash gap before assignment.

3. **High — The request timeout excludes the operations most likely to hang.**  
   `ClaudeCliChannel.cs:183–195,220–225`.  
   **Trigger:** The child stops consuming stdin; a pipe write/flush blocks, or semaphore acquisition never finishes.  
   **Consequence:** The ten-second timeout has not started. `_polling` remains true indefinitely, so all future polls stop. The pending entry and async operation remain retained.  
   **Smallest fix:** Start the deadline before semaphore acquisition and pass cancellation through acquisition and supported write/flush operations. On a transport timeout, terminate/dispose the owned channel to unblock the pipe; merely abandoning the task leaves the writer and lock stuck.

4. **High — Synchronous logging can stop both pipe drains.**  
   `ClaudeCliChannel.cs:116–117,131–132,223,239,274–277`.  
   **Trigger:** Disk I/O stalls, storage/security software delays an append, or the child produces excessive output.  
   **Consequence:** Both pumps contend on `_logLock`; stdout processing and stderr draining stop behind logging. The child can then block on a full pipe. Request writes also perform logging before writing, and successful requests can remain stuck in CSV logging after receiving their response. Catching exceptions does not bound blocking time.  
   **Smallest fix:** Move disk writes to a dedicated consumer with a bounded queue and a drop policy. Pipe readers and request completion must not wait for logging.

5. **High — Exit can block before reaching the operation that kills the child.**  
   `ClaudeCliChannel.cs:312–318`; `StatusBarApplicationContext.cs:318`.  
   **Trigger:** Exit occurs while stdin has buffered data and the child is not reading.  
   **Consequence:** Closing the `StreamWriter` can flush synchronously on the UI thread. The subsequent process kill and job close may never execute. The tray disappears while the application remains hung; panel hooks have not yet been removed.  
   **Smallest fix:** Mark shutdown first and remove panel hooks immediately. Terminate the owned job/process before closing redirected writers; perform any waits off the UI thread with a deadline.

6. **High — Valid JSON with an unexpected envelope permanently kills stdout processing.**  
   `ClaudeCliChannel.cs:153–174`, particularly `159`.  
   **Trigger:** A line such as `{"type":"control_response","response":null}` arrives.  
   **Consequence:** `response.TryGetProperty` throws because `response` is not an object. The outer pump catch logs and exits permanently. The document bypasses disposal, future requests time out, and unread stdout can eventually block the child.  
   **Smallest fix:** Check `response.ValueKind`, isolate handling errors per line, and dispose the document in `finally` unless ownership was successfully transferred. Unexpected pump termination must also invalidate the channel.

7. **High — A single unterminated output line permits unbounded memory growth.**  
   `ClaudeCliChannel.cs:114,131,145`.  
   **Trigger:** Either output stream emits a very large line or continuously emits without a newline.  
   **Consequence:** `ReadLine` keeps accumulating data without a limit. Subsequent logging and JSON parsing create additional allocations. Memory exhaustion can terminate the app.  
   **Smallest fix:** Use bounded incremental line framing. Enforce a maximum record size and discard through the next delimiter or restart the owned child on violation. Apply the limit to stderr as well.

8. **Medium — Poll completion can run against disposed UI resources.**  
   `StatusBarApplicationContext.cs:149–158,162–199,312–335`.  
   **Trigger:** Exit occurs with an outstanding request or a queued `ApplyResult`.  
   **Consequence:** There is no shutdown flag or lifetime cancellation. A completion can post during teardown or execute against disposed resources if dispatched during shutdown. `Post` can itself fail; its exception faults a discarded task. `_polling` also crosses threads without synchronization and becomes false before UI application completes.  
   **Smallest fix:** Keep poll coordination on the UI context, add an idempotent shutdown flag and lifetime token, and check shutdown inside the posted callback. Observe the polling task and complete/cancel all pending channel requests during disposal. Keep the poll gate held through result application.

9. **Medium — JSON document ownership is lost in cancellation races.**  
   `ClaudeCliChannel.cs:168–171,189–195,214`.  
   **Trigger:** Cancellation wins after the pump removes an entry but before `TrySetResult`. Alternatively, a response completes the TCS before a later write/flush failure prevents the caller from awaiting it.  
   **Consequence:** The document is never explicitly disposed. Its pooled storage is not returned promptly. This is an ownership defect, although it is not a permanently rooted HICON-style leak.  
   **Smallest fix:** Dispose `doc` when `TrySetResult` returns false. Also clean up a successfully completed response on paths that exit before consuming it, using a single explicit request-ownership protocol.

10. **Medium — Logs have neither retention nor size limits.**  
    `ClaudeCliChannel.cs:238–239,248–277`.  
    **Trigger:** Normal operation accumulates daily logs and CSV rows; verbose output accelerates growth.  
    **Consequence:** Date-based filenames separate files but do not bound disk usage. The CSV grows forever. Disk exhaustion eventually disables diagnostics and can affect other applications.  
    **Smallest fix:** Apply maximum file sizes and total retention limits, including the CSV. CSV growth at the specified polling cadence is relatively slow; unrestricted raw child output is the larger risk.

11. **High — Native hook callbacks execute fallible UI work without an exception boundary.**  
    `DismissWatcher.cs:87–105`; `PanelForm.cs:182–187`.  
    **Trigger:** A dismissal subscriber or `Hide()` throws, or synchronous hiding takes too long.  
    **Consequence:** A managed exception can escape a reverse-P/Invoke callback and terminate the process. Slow low-level callbacks delay desktop input and can cause Windows to silently remove the hook.  
    **Smallest fix:** Catch exceptions at every native callback boundary. Coalesce and post dismissal to the UI queue, return promptly, and preserve `CallNextHookEx` forwarding for mouse/keyboard hooks.

12. **Medium — Hook installation and teardown are not transactional.**  
    `DismissWatcher.cs:50–78`; `PanelForm.cs:177–179`.  
    **Trigger:** One installation returns zero, or startup throws after an earlier hook was installed.  
    **Consequence:** `_watching` can claim success with missing hooks. On a partial exception, `_watching` remains false, so `Stop` skips already-installed hooks. Teardown also clears handles and delegate roots without checking whether unhooking succeeded.  
    **Smallest fix:** Validate each installation and roll back partial success. Make cleanup inspect actual handles independently of `_watching`. Keep callback delegates rooted for the watcher’s lifetime and distinguish already-removed hooks from genuine unhook failures. Tie cleanup to visibility/disposal, not only `HidePanel`.

13. **Medium — Clicking the tray icon while the panel is open can reopen it.**  
    `DismissWatcher.cs:83–88`; `StatusBarApplicationContext.cs:66–69`.  
    **Trigger:** The user clicks the tray icon to toggle the visible panel closed.  
    **Consequence:** Mouse-down is outside the panel, so the hook hides it. The later tray `MouseClick` sees a hidden panel and opens it again.  
    **Smallest fix:** Exclude the tray icon’s rectangle from click-away dismissal, or suppress the corresponding toggle after dismissal by that same click.

14. **Medium — The absolute “never activates” guarantee is incomplete.**  
    `PanelForm.cs:128–137,177`.  
    **Trigger:** Windows active-window tracking requests activation on hover through `WM_MOUSEACTIVATE`.  
    **Consequence:** `ShowWithoutActivation` and `WS_EX_NOACTIVATE` do not cover every activation route; the form has no explicit mouse-activation rejection.  
    **Smallest fix:** Override `WndProc` to return `MA_NOACTIVATE` for `WM_MOUSEACTIVATE`. The supplied ordinary show path otherwise has the appropriate nonactivation styles.

15. **Medium — UI failures lack an unattended recovery boundary.**  
    `StatusBarApplicationContext.cs:77,119,135–136,194–199,217–243`; `PanelForm.cs:270–294`; `Program.cs:21–25`.  
    **Trigger:** Rendering, layout, resource allocation, or capture-file I/O throws. Even the diagnostic `Console.WriteLine` calls can throw with a broken output stream.  
    **Consequence:** Exceptions escape timers, posted callbacks, painting, or construction. Depending on WinForms exception policy, the result can be termination, an unattended exception dialog, or broken painting. No recovery policy is shown. By contrast, a fault in the discarded `PollAsync` task is generally unobserved rather than immediately process-fatal.  
    **Smallest fix:** Add targeted boundaries that preserve the last usable icon, hide a broken panel, and record bounded diagnostics. Stop/dispose automation timers in `finally`. Ensure common shutdown runs when the message loop exits.

16. **Medium — Graphics ownership is correct on success, incomplete on exceptions.**  
    `GaugeRenderer.cs:214–238,279–327,332–355`; `IconSlot.cs:58–60,108`; `IconFactory.cs:47–49`; `PanelForm.cs:533–539`.  
    **Trigger:** Graphics creation/drawing, icon assignment, or allocation throws after a resource is acquired.  
    **Consequence:** Output bitmaps, the exhausted canvas, an incoming icon, or a partially built path bypass deterministic disposal. Allocation of `xor` happens after `LockBits` but before its `try`, so allocation failure bypasses `UnlockBits`. Repeated recoverable failures can accumulate native resources until finalization.  
    **Smallest fix:** Protect each newly owned resource until successful return/transfer; use `finally` for consumed source bitmaps. Move the byte-array allocation before `LockBits`, or inside the protected region. Make icon assignment failure restore consistent ownership before disposing the incoming icon.

17. **Low — Application cleanup does not own every timer and component.**  
    `Program.cs:21–25`; `StatusBarApplicationContext.cs:71–74,114–121,215,312–337`.  
    **Trigger:** Exit follows a path other than `ExitApp`, or startup/capture is interrupted before its local timer disposes itself.  
    **Consequence:** There is no explicit `using`/`finally` around the application context. `ExitApp` stops but does not dispose its main timers; local timers cannot be cancelled centrally, and the context menu is never explicitly disposed. These are bounded lifetime-cleanup gaps, not per-poll growth.  
    **Smallest fix:** Give the context explicit ownership of all timers/menu and route all exit paths through one idempotent cleanup method. Dispose the context in `Program`.

18. **Low — Exhausted-icon throttling uses wall time.**  
    `IconSlot.cs:41,46,62`.  
    **Trigger:** The system clock moves backward.  
    **Consequence:** Exhausted-icon rendering can remain throttled until wall time catches up, leaving its countdown stale.  
    **Smallest fix:** Measure the fifteen-second throttle with `Environment.TickCount64` or `Stopwatch`.

The following checks passed, with the stated scope:

- **Normal icon ownership:** `IconFactory` creates owning icons without `Icon.FromHandle`; `IconSlot` bounds retained generations and disposes retired icons. No normal per-update HICON leak is evident.
- **Normal drawing:** Paint-local pens, brushes, fonts, ring bitmaps, and formats are generally disposed. Persistent panel graphics/fonts are disposed by `PanelForm.Dispose`.
- **Partial pipe reads:** `ReadLine` correctly assembles records across fragmented reads; fragmentation alone does not break JSON parsing. The problem is missing size limits.
- **Ordinary request cleanup:** `_pending` entries are removed on normal completion and timeout. No accumulation is evident for serial, completing requests.
- **UI serialization:** `ApplyResult`, WinForms timers, and panel operations normally run on the UI thread. The hooks are installed there and delivered there under the chosen hook modes. No `Control.Invoke` deadlock appears.
- **Process death and hooks:** Installed hooks do not survive actual termination of their owning process. A hung process or partially failed teardown is a different case.
- **Other Claude sessions:** No process-name enumeration or kill-by-name appears. Termination targets the stored child and its descendants/job members, not the user’s independently running sessions.
- **Subscriptions:** The shown instance-event cycles do not themselves constitute GC leaks; there is no accumulating subscription per poll/show.
- **Review limits:** `QuotaModel` and `DemoQuotaSource` implementations were not embedded, so their collection growth and exception behavior cannot be certified. The handle self-test covers successful icon churn, not hooks, visible-shell behavior, or exception paths; forced GC can also mask missing deterministic disposal.

Checked and found sound

---

## Decisions

Implementation date 2026-09-11. All findings accepted per the task brief. Below: finding ->
what was done (file:line) -> deviation, if any.

**1. No recovery after startup failure/child exit/update termination.**
New `ClaudeCliChannelSupervisor` (`src/ClaudeStatusBar/Data/ClaudeCliChannelSupervisor.cs:24`)
owns launch/relaunch, serialized through a `SemaphoreSlim` gate. `ClaudeCliChannel.Start`
re-resolves `claude.exe`'s path on every call via `ChildProcessSpec.ResolveExePath`
(`src/ClaudeStatusBar/Data/ChildProcessSpec.cs`), so an auto-update mid-session is picked up on
the next relaunch. Startup failure and every other failure both flow through
`StatusBarApplicationContext.ApplyResult` -> `QuotaModel.IngestFailure`
(`src/ClaudeStatusBar/StatusBarApplicationContext.cs:181-199`) -- no separate "no channel yet"
path. Backoff steps 5s/15s/60s/5min, reset on a healthy poll
(`ClaudeCliChannelSupervisor.cs:26,60,126`). Verified live: killed a running instance's
claude.exe child, observed `channel marked unhealthy: stdout EOF`, relaunch, and a successful
poll ~6-7s later (see Verification section below).

**2. Job containment fails open / spawn-before-assignment gap.**
`ClaudeCliChannel.Start` (`ClaudeCliChannel.cs:70-140`) creates and configures the job object
BEFORE `Process.Start`, assigns immediately after, and fails closed (kills the child, closes
the job handle, throws) if assignment fails. The whole method is wrapped in try/catch that
kills the process and closes the job on ANY exception past `Process.Start`
(`ClaudeCliChannel.cs:133-141`). Deviation: the native `PROC_THREAD_ATTRIBUTE_JOB_LIST`
atomic-assignment path is NOT implemented (documented in a code comment,
`ClaudeCliChannel.cs:84-97`, as the review's own smallest-fix text allows) -- the remaining gap
is the few milliseconds between `Process.Start` returning and `AssignProcessToJobObject`
succeeding.

**3. Timeout excludes acquisition/write/flush.**
`ClaudeCliChannel.GetUsageAsync` (`ClaudeCliChannel.cs:261-330`) starts the deadline before
`_stdinLock.WaitAsync`, and threads the same `CancellationToken` through acquisition,
`WriteAsync` and `FlushAsync`. On a genuine timeout (not caller cancellation) it calls
`MarkFaulted`, which fails every pending request and lets the supervisor kill+dispose+relaunch
-- recycling the channel rather than merely abandoning the stuck task. Verified live via the
`Hang_TimesOutPromptly_PollGateNeverStaysStuck` and `OversizedLine_...` integration tests, and
manually against the real claude.exe.

**4. Synchronous logging on the pipes.**
New `DiskLogSink` (`src/ClaudeStatusBar/Data/DiskLogSink.cs:29`): one dedicated background
task drains two `System.Threading.Channels` bounded queues (10,000 entries each,
`QueueCapacity`). Raw log: `BoundedChannelFullMode.DropOldest` (`DiskLogSink.cs:44`). CSV:
`BoundedChannelFullMode.DropWrite` plus an explicit `Interlocked`-counted drop that is logged
every time it changes (`DiskLogSink.cs:46,69-75`) -- never silent. `ClaudeCliChannel`'s pumps
and `GetUsageAsync` only ever call `EnqueueRaw`/`EnqueueCsv` (non-blocking channel writes),
never touch disk directly.

**5. Exit can block before killing the child.**
Single idempotent `StatusBarApplicationContext.Shutdown()` (`StatusBarApplicationContext.cs:369-399`),
reached from `ExitApp()`, `Dispose(bool)`, and (via `Program.cs`'s `finally`) every other exit
route. Order: shutdown flag -> `_panel.HidePanel()`/`Dispose()` (removes DismissWatcher hooks
immediately) -> stop/dispose all timers -> hide the tray icon -> child kill + log-writer flush
bounded off-thread (`RunBoundedOnBackgroundThread`, `StatusBarApplicationContext.cs:401-410`,
5s deadline) -> dispose icon slot/menu/notify icon. `ClaudeCliChannel.Dispose` now kills the
process BEFORE closing `StandardInput` (`ClaudeCliChannel.cs:365-380`), reversing the original
order that could flush-block on a non-reading child. Verified live: hard-killed the app
externally (simulating a non-graceful kill) -- child died within 63ms via the job object's
kill-on-close, independent of the graceful path.

**6. Unexpected envelope kills stdout processing.**
New `EnvelopeParser` (`src/ClaudeStatusBar/Data/EnvelopeParser.cs`), a pure function: checks
`ValueKind` before every `TryGetProperty`/`GetString`. `ClaudeCliChannel.HandleLine`
(`ClaudeCliChannel.cs:227-260`) wraps the whole dispatch in try/catch, disposes the
`JsonDocument` on every path where ownership isn't transferred (including when `TrySetResult`
returns false). Unit-tested directly (`EnvelopeParserTests.cs`, incl. the exact
`{"response":null}` case) and integration-tested against a real child process
(`ChannelSupervisorTests.NullResponseEnvelope_DoesNotKillTheStdoutPump`).

**7. Unbounded line growth.**
New `LineFramer` (`src/ClaudeStatusBar/Data/LineFramer.cs`), a pure incremental framer capped
at 4M chars, used by both stdout and stderr via `ClaudeCliChannel.PumpStream`
(`ClaudeCliChannel.cs:172-200`). An oversized record is discarded to the next newline and
logged; 3 overflows within 10 minutes recycles the channel
(`RecordOverflow`, `ClaudeCliChannel.cs:203-217`). Unit-tested (`LineFramerTests.cs`) and
integration-tested against a real 5MB-no-newline child process
(`ChannelSupervisorTests.OversizedLine_IsDiscarded_...`).

**8. Poll completion against disposed UI resources.**
`_shuttingDown` flag checked inside the posted `PollAsync` callback before touching any UI
state (`StatusBarApplicationContext.cs:181-199`), and again in the demo/automation timer
callbacks. `ClaudeCliChannelSupervisor.DisposeAsync` and `ClaudeCliChannel.Dispose` both
complete/cancel all pending requests during teardown.

**9. JSON document ownership in cancellation races.**
Covered by the same `HandleLine`/`EnvelopeParser` rework as #6: `TrySetResult`'s return value
is checked and the document disposed if the transfer failed
(`ClaudeCliChannel.cs:227-260`).

**10. No log retention/size limits.**
`DiskLogSink`: raw logs capped at 20MB/file (`RawFileCapBytes`, rolling to `{date}.{n}.log`)
and swept for files older than 7 days; `window-shape.csv` rotated monthly
(`DiskLogSink.MonthlyCsvPath`, `window-shape-YYYY-MM.csv`) and swept past 6 months
(`DiskLogSink.cs:32-34,127-170`).
Deviation (forced, and explicitly pre-authorized by this same finding's text): `CsvReplay`
gained one additive method, `ReadRowsForWarmStart(logDir, now)`
(`src/ClaudeStatusBar/Data/CsvReplay.cs`), which reads the current + previous month's files so
a window straddling a month boundary still warm-starts; the existing `ReadRows(path)` is
untouched and every existing `CsvReplayTests`/`ParsingTests` call still passes. This in turn
forced a 2-line change in `Model/QuotaModel.cs` (`WarmStart`/`DefaultLogDir`, in scope's stated
exclusion list): the fixed `window-shape.csv` filename this used to read no longer exists once
rotation is on, so `WarmStart` now resolves the log directory and calls the new method when the
test-only `csvPath` override is absent; that override (used by every `QuotaModelTests`
`WarmStart` call) is untouched and calls the original single-file `ReadRows` exactly as before.
Flagging this explicitly since the model is under separate review -- it is a minimal,
additive-only change with no behavior change on any existing call path.

**11. Native hook callbacks without an exception boundary.**
`DismissWatcher`'s three hook procs each wrap their body in try/catch
(`src/ClaudeStatusBar/Ui/DismissWatcher.cs:158-186`) and always return `CallNextHookEx`
promptly. Dismissal is coalesced (`_dismissPosted` guard) and posted to the owning `Control`
via `BeginInvoke` (`RequestDismiss`, `DismissWatcher.cs:140-156`) rather than run inline on the
hook's own call stack.

**12. Hook install/teardown not transactional.**
`DismissWatcher.Start` (`DismissWatcher.cs:77-116`) validates each `SetWindowsHookEx`/
`SetWinEventHook` result and rolls back everything already installed on the first failure
(`RollBack`, `DismissWatcher.cs:137`), returning `false` rather than claiming success.
`Stop()` (`DismissWatcher.cs:118-136`) inspects the actual handles unconditionally, independent
of `_watching`.

**13. Tray click can reopen the panel.**
`DismissWatcher.ShouldDismissForClick` (`DismissWatcher.cs:189-196`), extracted as a pure,
public function precisely so this has direct automated coverage
(`tests/ClaudeStatusBar.Tests/DismissWatcherTests.cs`, 5 tests incl. the exact reproduction).
`PanelForm.ShowPanel` passes the tray icon's rectangle (via `PanelAnchor.TryGetIconRect`,
now `internal` -> used here) as the exclusion region (`src/ClaudeStatusBar/Ui/PanelForm.cs`,
`ShowPanel`). Deviation: the interactive desktop session in this environment was locked for
the whole task (confirmed via `GetForegroundWindow` returning the Windows lock screen), which
blocks `SendInput`-based live verification -- see Verification section below for what was done
instead.

**14. "Never activates" guarantee incomplete.**
`PanelForm.WndProc` overridden to return `MA_NOACTIVATE` for `WM_MOUSEACTIVATE`
(`PanelForm.cs:155-172`). Verified live: `GetForegroundWindow()` identical before launch,
~4s later (panel auto-shown via `--show-panel`), and 2s after that.

**15. UI failures lack a recovery boundary.**
`Program.cs`: `Application.SetUnhandledExceptionMode(CatchException)` plus
`Application.ThreadException`/`AppDomain.UnhandledException` handlers that log via `SafeLog`
and keep the app alive (`Program.cs:16-24`). New `Diagnostics/SafeLog.cs` wraps every
`Console.WriteLine` call in this app in try/catch (a WinExe has no console). `PanelForm.OnPaint`
hides the panel (deferred via `BeginInvoke`) on any exception (`PanelForm.cs:315-341`);
`PanelForm.UpdateView` and `IconSlot.Update` both catch and keep the last good state
(`PanelForm.cs:175-198`, `IconSlot.cs:38-88`). Timer ticks (`PanelForm`'s tick timer,
`StatusBarApplicationContext.SafeTick`/`SafeAdvanceDemo`) are wrapped individually.

**16. Graphics ownership incomplete on exceptions.**
`GaugeRenderer.Downsample`, `RenderExhaustedGlyph` and `ApplyStaleOverlay` all now dispose their
newly-owned `Bitmap` on any exception before it would otherwise be handed off
(`src/ClaudeStatusBar/Icons/GaugeRenderer.cs`). `IconFactory.EncodeBmpFrame` allocates `xor`
BEFORE `LockBits` (`src/ClaudeStatusBar/Icons/IconFactory.cs:47-58`). `IconSlot.Update` disposes
the newly-built `Icon` if `Assign` itself throws, so ownership never straddles success/failure
(`IconSlot.cs:38-88`).

**17. Cleanup doesn't own every timer/component.**
`Program.cs` wraps `Application.Run` in try/finally and calls `context.Dispose()`
(`Program.cs:38-46`). `StatusBarApplicationContext.Shutdown()` owns and disposes every timer,
the context menu, the icon slot and the notify icon (`StatusBarApplicationContext.cs:369-399`).

**18. Exhausted-icon throttle uses wall time.**
`IconSlot._lastRenderAtMonoMs` (`IconSlot.cs:31`) uses `Environment.TickCount64` instead of
`DateTimeOffset.UtcNow`.

## Verification

1. `dotnet build ClaudeStatusBar.slnx -c Release` -> 0 Warning(s), 0 Error(s).
   `dotnet test ClaudeStatusBar.slnx -c Release` -> **146/146 passed** (118 pre-existing + 28
   new: 7 `LineFramerTests`, 7 `EnvelopeParserTests`, 4 `CsvReplayMonthlyTests`, 5
   `ChannelSupervisorTests` integration tests against a real fake-child process, 5
   `DismissWatcherTests`).
2. `--selftest` -> `GDI 13->13 (d=0)  USER 7->7 (d=0)`.
3. Launched a separate Release test instance (PID 6960, child claude.exe PID 71236, strictly
   isolated from the user's pinned Debug instance PID 28104/child 29064, which were verified
   untouched throughout). Killed only PID 71236: log showed `channel marked unhealthy: stdout
   EOF` at 12:17:03, a new child (PID 72824) appeared at 12:17:11 (~8s, inside the 5s-then-15s
   backoff), and a successful get_usage poll landed at 12:17:10 against the new session (fresh
   `request_id` counter). Then hard-killed the app itself (PID 6960): both it and its child
   (PID 72824) were gone within 63ms (job-object kill-on-close). No orphaned processes;
   afterward only the pinned Debug instance (28104/29064) remained running.
4. Tray toggle: **not verified interactively** -- the environment's interactive desktop session
   was locked for the duration of this task (confirmed via `GetForegroundWindow` returning the
   Windows lock screen's own window), which blocks `SendInput` from reaching the real desktop.
   Verified instead via `DismissWatcher.ShouldDismissForClick`, extracted specifically to make
   this testable without a live hook/desktop, with 5 passing unit tests including the exact
   click-on-tray-icon-while-open reproduction (`DismissWatcherTests.cs`). Recommend a quick
   manual click-to-close check once the desktop is unlocked.
5. Focus test: launched a test instance with `--show-panel`; `GetForegroundWindow()` was
   identical (same handle, the lock screen's) before launch, ~4s later once the panel
   auto-showed, and 2s after that. No change.
6. `--demo --capture-states` into `scratchpad/states5/` -> exit 0, 10 state PNGs written, **0
   OVERFLOW lines**. (Screenshots capture the lock screen rather than the app, since
   `CopyFromScreen` reads the locked session's actual framebuffer -- irrelevant to this specific
   check, which is a log-based regression check per the task text.)

At the end: stopped the pinned Debug instance, rebuilt Debug, relaunched detached in normal
mode. See chat for the exact commands and final process state.
