using System.Drawing.Imaging;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;

namespace ClaudeStatusBar;

/// <summary>
/// Owns the tray icon, the panel, the poll/eval timers and the CLI channel for the
/// app's whole lifetime. Polling runs on the thread pool; results are marshalled
/// back to the UI thread with SynchronizationContext.Post (not Control.Invoke).
///
/// Wiring rules from Model/QuotaModel.cs's doc comment (docs/forecast-and-states.md):
///   - One QuotaModel for the app's lifetime.
///   - On the FIRST successful poll only: WarmStart(snapshot, utcNow) BEFORE
///     Ingest(...) for that same snapshot.
///   - Every poll result: Ingest on success, IngestFailure on failure.
///   - A 1s UI timer (_evalTimer) calls Evaluate(utcNow, monoMs) -- cheap, safe
///     every second, and the only thing that can notice a Freshness change
///     between polls.
///   - After every poll, the poll timer's next interval comes from NextPollDelay.
///
/// --demo mode never constructs a QuotaModel, a ClaudeCliChannelSupervisor, or a
/// DiskLogSink (no claude.exe child, no session, no hooks, no log directory
/// writes): DemoQuotaSource feeds synthetic QuotaViews through the exact same
/// _evalTimer -> IconSlot/PanelForm path instead.
///
/// Channel lifecycle (Codex review High #1, #6): the channel is not owned
/// directly here any more -- ClaudeCliChannelSupervisor owns launch, health
/// tracking and bounded-backoff relaunch. PollAsync always calls through the
/// supervisor, which returns a failure result immediately whenever no healthy
/// channel exists, so a poll can never block waiting for a channel that isn't
/// there and the freshness ladder (Live -> Stale -> Unknown) degrades exactly the
/// way it does for an ordinary transport failure -- never a stale confident number.
///
/// Shutdown (Codex review High #5, Medium #8, Low #17): every exit route
/// (ExitApp from the tray menu, Application.Run returning, Program's finally)
/// funnels through the single idempotent Shutdown() below, in the specific order
/// the review calls out: shutdown flag first, then panel hooks/timers/tray icon
/// (cheap, UI-thread, synchronous), then the child process kill and log writers
/// (potentially slow I/O) bounded and off the UI thread.
/// </summary>
public sealed class StatusBarApplicationContext : ApplicationContext
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    static readonly TimeSpan EvalTickInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DemoStateInterval = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);

    readonly bool _demoMode;
    readonly NotifyIcon _notifyIcon;
    readonly IconSlot _iconSlot;
    readonly PanelForm _panel;
    readonly System.Windows.Forms.Timer _pollTimer;
    readonly System.Windows.Forms.Timer _evalTimer;
    readonly System.Windows.Forms.Timer? _demoTimer;
    readonly SynchronizationContext _uiContext;
    readonly ClaudeCliChannelSupervisor? _channelSupervisor;
    readonly DiskLogSink? _logSink;
    readonly QuotaModel? _quotaModel;

    IReadOnlyList<DemoQuotaSource.DemoState> _demoStates = Array.Empty<DemoQuotaSource.DemoState>();
    int _demoIndex;
    QuotaView _currentView = QuotaView.Initial;
    bool _polling;
    bool _warmStarted;
    bool _shuttingDown;

    public StatusBarApplicationContext(bool demoMode = false, bool showPanelOnStartup = false)
    {
        _demoMode = demoMode;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _uiContext = SynchronizationContext.Current!;

        _notifyIcon = new NotifyIcon
        {
            Text = "Claude Status Bar",
            Visible = true,
        };
        _iconSlot = new IconSlot(_notifyIcon);
        _iconSlot.Update(QuotaView.Initial); // something visible immediately, before the first poll/demo tick lands

        _panel = new PanelForm(_notifyIcon);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _panel.Toggle();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Visa panel", null, (_, _) => _panel.Toggle());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = menu;

        _evalTimer = new System.Windows.Forms.Timer { Interval = (int)EvalTickInterval.TotalMilliseconds };
        _evalTimer.Tick += (_, _) => SafeTick();

        if (_demoMode)
        {
            _pollTimer = new System.Windows.Forms.Timer { Interval = 60_000 }; // never started -- demo mode does not poll
            _demoStates = DemoQuotaSource.Build(DateTimeOffset.UtcNow);
            _demoTimer = new System.Windows.Forms.Timer { Interval = (int)DemoStateInterval.TotalMilliseconds };
            _demoTimer.Tick += (_, _) => SafeAdvanceDemo();
            AdvanceDemo(); // show the first state immediately, don't wait 4s
            _demoTimer.Start();
        }
        else
        {
            _quotaModel = new QuotaModel();

            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
            _logSink = new DiskLogSink(logDir);
            _channelSupervisor = new ClaudeCliChannelSupervisor(ChildProcessSpec.Default(), _logSink);
            _channelSupervisor.Start(); // launches in the background; any failure is reported through the same IngestFailure path a poll failure uses

            _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
            _pollTimer.Tick += (_, _) => _ = PollAsync();
            _pollTimer.Start();
            _ = PollAsync(); // do not wait for the first tick to show up
        }

        _evalTimer.Start();

        if (showPanelOnStartup)
        {
            // Test-only path (see Program.cs --show-panel): give the first poll a
            // moment to land (README: first get_usage call takes ~1.1-1.6s) before
            // showing, so an automated screenshot captures real data, not "Hämtar…".
            var showTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            showTimer.Tick += (_, _) =>
            {
                showTimer.Stop();
                showTimer.Dispose();
                if (!_shuttingDown) _panel.ShowPanel();
            };
            showTimer.Start();
        }
    }

    /// <summary>Targeted try/finally around the 1Hz timer body (Codex review Medium #15): an Evaluate/render exception must never take down the message loop.</summary>
    void SafeTick()
    {
        if (_shuttingDown) return;
        try { Tick(); }
        catch (Exception ex) { SafeLog.Warn($"Eval tick threw: {ex.Message}"); }
    }

    void SafeAdvanceDemo()
    {
        if (_shuttingDown) return;
        try { AdvanceDemo(); }
        catch (Exception ex) { SafeLog.Warn($"Demo tick threw: {ex.Message}"); }
    }

    /// <summary>The 1Hz UI tick: Evaluate() in live mode, or a re-push of the current demo view so its relative countdowns keep advancing on screen.</summary>
    void Tick()
    {
        if (!_demoMode)
        {
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            long monoMs = Environment.TickCount64;
            _currentView = _quotaModel!.Evaluate(utcNow, monoMs);
        }

        _iconSlot.Update(_currentView);
        _panel.UpdateView(_currentView, _demoMode);
    }

    void AdvanceDemo()
    {
        if (_demoStates.Count == 0) return;
        _currentView = _demoStates[_demoIndex % _demoStates.Count].View;
        _demoIndex++;
        Tick();
    }

    async Task PollAsync()
    {
        if (_channelSupervisor is null || _polling || _shuttingDown) return;
        _polling = true;
        try
        {
            var (snapshot, error, latency) = await _channelSupervisor.GetUsageAsync(RequestTimeout).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                // Shutdown may have started while this poll was in flight (Codex review
                // Medium #8): a posted completion must never touch disposed UI resources.
                if (_shuttingDown) return;
                ApplyResult(snapshot, error, latency);
            }, null);
        }
        finally
        {
            _polling = false;
        }
    }

    void ApplyResult(UsageSnapshot? snapshot, string? error, TimeSpan latency)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        long monoMs = Environment.TickCount64;

        if (snapshot is null)
        {
            SafeLog.Info($"get_usage failed after {latency.TotalMilliseconds:F0}ms: {error}");
            _quotaModel!.IngestFailure(error ?? "unknown error", utcNow, monoMs);
        }
        else
        {
            if (error != null)
                SafeLog.Info($"get_usage parse warning: {error}");

            SafeLog.Info(
                $"get_usage {latency.TotalMilliseconds:F0}ms  " +
                $"session(5h)={snapshot.SessionUtilization:F1}%  resets_at={snapshot.SessionResetsAt}  " +
                $"weekly(all models)={(snapshot.WeeklyUtilization?.ToString("F1") ?? "?")}%");

            // WarmStart must run BEFORE Ingest for this same snapshot, and only on the
            // first successful poll ever -- see QuotaModel.WarmStart's doc comment for why
            // the order matters (its replay would otherwise be dropped by the dt<=0 guard).
            if (!_warmStarted)
            {
                _quotaModel!.WarmStart(snapshot, utcNow);
                _warmStarted = true;
            }
            _quotaModel!.Ingest(snapshot, utcNow, monoMs);
        }

        TimeSpan next = _quotaModel!.NextPollDelay(utcNow);
        _pollTimer.Interval = Math.Max(1, (int)Math.Round(next.TotalMilliseconds));

        // Push immediately rather than waiting up to 1s for the next eval tick.
        _currentView = _quotaModel.Evaluate(utcNow, monoMs);
        LogCommittedState(); // round-2 verification aid (docs/reviews/2026-09-11-codex-quota-model-round2.md item #4): nothing else durably records the committed verdict per poll
        _iconSlot.Update(_currentView);
        _panel.UpdateView(_currentView, _demoMode);
    }

    /// <summary>One line per poll recording the committed verdict (not just the raw % SafeLog.Info already logs above), so a restart's first-poll behavior can be inspected after the fact. Best-effort: never allowed to affect the poll path.</summary>
    void LogCommittedState()
    {
        // Through DiskLogSink like every other disk write: off the UI thread, size-capped, 7-day retention.
        _logSink?.EnqueueRaw("state",
            $"session={_currentView.Session.State} sessionReason={_currentView.Session.MeasuringReason ?? "-"} " +
            $"weekly={_currentView.Weekly.State} weeklyReason={_currentView.Weekly.MeasuringReason ?? "-"} freshness={_currentView.Freshness}");
    }

    /// <summary>
    /// Automation-only path for --capture-states/--capture-icon-sheet (see the
    /// task's verification step 3): steps through every demo state, showing and
    /// screen-capturing the real, non-activating panel (safe: it never takes
    /// focus, which is the whole point of ShowWithoutActivation/WS_EX_NOACTIVATE),
    /// then renders the icon contact sheet purely in-memory, then exits.
    /// </summary>
    public void RunAutomatedCapture(string? statesDir, string? iconSheetPath)
    {
        _demoTimer?.Stop();
        IReadOnlyList<DemoQuotaSource.DemoState> states = DemoQuotaSource.Build(DateTimeOffset.UtcNow);
        int i = 0;
        bool waitingToCapture = false;
        var stepTimer = new System.Windows.Forms.Timer { Interval = 250 };

        stepTimer.Tick += (_, _) =>
        {
            if (_shuttingDown) { stepTimer.Stop(); return; }
            stepTimer.Stop();

            if (waitingToCapture)
            {
                if (statesDir != null) CapturePanel(states[i].Key, statesDir);
                _panel.HidePanel();
                i++;
                waitingToCapture = false;
            }

            if (i >= states.Count)
            {
                if (iconSheetPath != null) SaveIconSheet(states, iconSheetPath);
                stepTimer.Dispose();
                ExitApp();
                return;
            }

            _currentView = states[i].View;
            Tick();
            _panel.ShowPanel();
            waitingToCapture = true;
            stepTimer.Interval = 500; // let layout/paint settle before the screenshot
            stepTimer.Start();
        };
        stepTimer.Start();
    }

    void CapturePanel(string name, string dir)
    {
        Directory.CreateDirectory(dir);
        Rectangle bounds = _panel.Bounds;
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(Path.Combine(dir, $"{name}.png"), ImageFormat.Png);
    }

    /// <summary>All 8 demo states x 4 sizes x 2 backgrounds, 8x nearest-neighbour scaled, composed in-memory (no window, no screen capture needed).</summary>
    static void SaveIconSheet(IReadOnlyList<DemoQuotaSource.DemoState> states, string path)
    {
        int[] sizes = { 16, 20, 24, 32 };
        (string Name, bool Dark, Color Color)[] backgrounds =
        {
            ("dark", true, Color.FromArgb(255, 0x20, 0x20, 0x20)),
            ("light", false, Color.FromArgb(255, 0xF3, 0xF3, 0xF3)),
        };
        const int scale = 8;
        const int maxPx = 32;
        int cellW = maxPx * scale + 24;
        int cellH = cellW + 22;
        int cols = states.Count;
        int rows = backgrounds.Length * sizes.Length;

        using var sheet = new Bitmap(cellW * cols, cellH * rows);
        using (var g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

            using var labelFont = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var labelBrushDark = new SolidBrush(Color.White);
            using var labelBrushLight = new SolidBrush(Color.Black);

            int row = 0;
            foreach (var bg in backgrounds)
            {
                foreach (int px in sizes)
                {
                    for (int col = 0; col < states.Count; col++)
                    {
                        var cellRect = new Rectangle(col * cellW, row * cellH, cellW, cellH);
                        using (var cellBg = new SolidBrush(bg.Color))
                            g.FillRectangle(cellBg, cellRect);

                        QuotaIconParams p = QuotaIconParams.Build(states[col].View, px, taskbarDark: bg.Dark, DateTimeOffset.UtcNow);
                        using Bitmap icon = GaugeRenderer.RenderQuota(p);
                        var dest = new Rectangle(cellRect.X + 10, cellRect.Y + 10, px * scale, px * scale);
                        g.DrawImage(icon, dest);

                        string label = $"{states[col].Key} {px}px {bg.Name}";
                        g.DrawString(label, labelFont, bg.Dark ? labelBrushDark : labelBrushLight, cellRect.X + 8, cellRect.Bottom - 18);
                    }
                    row++;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sheet.Save(path, ImageFormat.Png);
    }

    void ExitApp()
    {
        Shutdown();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Shutdown();
        base.Dispose(disposing);
    }

    /// <summary>
    /// The one idempotent shutdown path for every exit route (Codex review High #5,
    /// Medium #8, Low #17): the tray menu's Exit, Application.Run returning for any
    /// other reason, and Program's outer finally all end up here exactly once.
    /// Order matters -- see the type doc comment for why each step is where it is.
    /// </summary>
    void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        // Panel hooks removed and the panel itself torn down FIRST (Codex review High #5):
        // HidePanel() stops the DismissWatcher's global hooks immediately, and Dispose()
        // finishes the rest (tick timer, fonts) before anything below can block.
        try { _panel.HidePanel(); } catch (Exception ex) { SafeLog.Warn($"HidePanel during shutdown threw: {ex.Message}"); }
        try { _panel.Dispose(); } catch (Exception ex) { SafeLog.Warn($"Panel.Dispose during shutdown threw: {ex.Message}"); }

        try { _pollTimer.Stop(); _pollTimer.Dispose(); } catch { /* best effort */ }
        try { _evalTimer.Stop(); _evalTimer.Dispose(); } catch { /* best effort */ }
        try { _demoTimer?.Stop(); _demoTimer?.Dispose(); } catch { /* best effort */ }

        try { _notifyIcon.Visible = false; } catch { /* best effort */ }

        // Terminate the child and flush the log writers off the UI thread, bounded (Codex
        // review High #5): closing a redirected pipe/flushing the CSV can block if the
        // child is not reading or the disk is slow, and that must never hang the thread
        // this runs on across every exit route.
        RunBoundedOnBackgroundThread(async () =>
        {
            if (_channelSupervisor != null) await _channelSupervisor.DisposeAsync().ConfigureAwait(false);
            if (_logSink != null) await _logSink.DisposeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }, ShutdownDeadline);

        try { _iconSlot.Dispose(); } catch { /* best effort */ }
        try { _notifyIcon.ContextMenuStrip?.Dispose(); } catch { /* best effort */ }
        try { _notifyIcon.Dispose(); } catch { /* best effort */ }
    }

    static void RunBoundedOnBackgroundThread(Func<Task> work, TimeSpan deadline)
    {
        try
        {
            Task.Run(work).Wait(deadline);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"Shutdown cleanup did not finish cleanly: {ex.Message}");
        }
    }
}
