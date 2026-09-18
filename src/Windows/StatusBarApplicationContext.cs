using System.Drawing.Imaging;
using System.Globalization;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Icons;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;

namespace ClaudeStatusBar;

/// <summary>
/// Owns every tray icon, the panel, the poll/eval timers and the CLI channel for the app's whole
/// lifetime. Polling runs on the thread pool; results are marshalled back to the UI thread with
/// SynchronizationContext.Post (not Control.Invoke).
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
/// writes): DemoQuotaSource/MultiAccountDemoSource feed synthetic QuotaViews through the exact
/// same _evalTimer -> icon/PanelForm path instead.
///
/// Channel lifecycle (Codex review High #1, #6): the channel is not owned
/// directly here any more -- ClaudeCliChannelSupervisor owns launch, health
/// tracking and bounded-backoff relaunch. PollAsync always calls through the
/// supervisor, which returns a failure result immediately whenever no healthy
/// channel exists, so a poll can never block waiting for a channel that isn't
/// there and the freshness ladder (Live -> Stale -> Unknown) degrades exactly the
/// way it does for an ordinary transport failure -- never a stale confident number.
///
/// Multi-account (docs/multi-account.md): channel, QuotaModel, DiskLogSink, poll scheduling and
/// WarmStart-before-first-Ingest are owned per account by Data/AccountRuntime.cs, one per
/// accounts.json entry. This context owns the _accounts list, drives every account's Evaluate()
/// on the same 1s tick independently (one account failing, logged out, or missing must never
/// stop any other), and reconciles ONE TrayIconHandle per account the display plan
/// (Model/AccountDisplayPlan.cs) says should have an icon: one per enabled account in perAccount
/// mode (capped at MaxIcons), or the single closest-to-blocked account in binding mode. The panel
/// always shows one account at a time (_explicitPanelAccountIndex, or the display-mode default
/// when unset) plus, when more than one account is configured, a compact "other accounts" list
/// any row of which switches the panel to that account.
///
/// Shutdown (Codex review High #5, Medium #8, Low #17): every exit route
/// (ExitApp from the tray menu, Application.Run returning, Program's finally)
/// funnels through the single idempotent Shutdown() below, in the specific order
/// the review calls out: shutdown flag first, then panel hooks/timers/tray icons
/// (cheap, UI-thread, synchronous), then the child process kill and log writers
/// (potentially slow I/O) bounded and off the UI thread.
/// </summary>
public sealed class StatusBarApplicationContext : ApplicationContext
{
    static readonly TimeSpan EvalTickInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DemoStateInterval = TimeSpan.FromSeconds(4);
    static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);
    const int DemoMaxIcons = 3;

    /// <summary>One account as the display/icon-selection layer needs it: a label (null keeps the single-account panel/tooltip pixel-identical) and its last QuotaView.</summary>
    readonly record struct DisplayAccount(string? Label, QuotaView View);

    /// <summary>One step of the --demo / --capture-states cycle: either a legacy single-account state (Accounts.Count == 1, Label null) or a MultiAccountDemoSource frame.</summary>
    readonly record struct DemoFrame(string Key, AccountDisplayMode Mode, IReadOnlyList<DisplayAccount> Accounts, int FocusIndex);

    readonly bool _demoMode;
    readonly PanelForm _panel;
    readonly System.Windows.Forms.Timer _evalTimer;
    readonly System.Windows.Forms.Timer? _demoTimer;
    readonly SynchronizationContext _uiContext;
    readonly List<AccountRuntime> _accounts = new();
    readonly Dictionary<int, TrayIconHandle> _iconsByIndex = new();
    readonly ContextMenuStrip _trayMenu;
    readonly ToolStripMenuItem _toggleModeItem;

    AccountsConfig _accountsConfig = AccountsConfig.Default();
    AccountDisplayMode _displayMode = AccountDisplayMode.PerAccount;
    int _maxIcons = 3;
    int? _explicitPanelAccountIndex;
    int? _lastRightClickedAccountIndex;
    int? _shownAccountIndex; // whichever index RenderAccounts last showed the panel for -- see OnRefreshRequested

    IReadOnlyList<DemoFrame> _demoFrames = Array.Empty<DemoFrame>();
    int _demoIndex;
    bool _shuttingDown;

    public StatusBarApplicationContext(bool demoMode = false, bool showPanelOnStartup = false)
    {
        _demoMode = demoMode;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _uiContext = SynchronizationContext.Current!;

        (_trayMenu, _toggleModeItem) = BuildTrayMenu();
        _panel = new PanelForm();
        _panel.OtherAccountClicked += FocusAccount;
        _panel.RefreshRequested += OnRefreshRequested;

        _evalTimer = new System.Windows.Forms.Timer { Interval = (int)EvalTickInterval.TotalMilliseconds };
        _evalTimer.Tick += (_, _) => SafeTick();

        if (_demoMode)
        {
            _demoFrames = BuildDemoFrames(DateTimeOffset.UtcNow);
            _demoTimer = new System.Windows.Forms.Timer { Interval = (int)DemoStateInterval.TotalMilliseconds };
            _demoTimer.Tick += (_, _) => SafeAdvanceDemo();
            AdvanceDemo(); // show the first state immediately, don't wait 4s
            _demoTimer.Start();
        }
        else
        {
            _accountsConfig = AccountsConfig.LoadOrCreateDefault();
            _displayMode = _accountsConfig.DisplayMode;
            _maxIcons = Math.Max(1, _accountsConfig.MaxIcons);
            UpdateToggleModeItemText();

            int index = 0;
            foreach (AccountEntry entry in _accountsConfig.Accounts)
            {
                if (entry.Enabled)
                {
                    string slot = index == 0 ? "default" : index.ToString(CultureInfo.InvariantCulture);
                    _accounts.Add(new AccountRuntime(slot, entry, index, _uiContext));
                }
                index++;
            }

            // Every account is driven independently from here on (docs/multi-account.md): one
            // account's channel failing to launch, being logged out, or its config directory
            // going missing must never stop any other account's polling or eval loop.
            foreach (AccountRuntime account in _accounts) account.Start();

            RenderAccounts(_accounts.Select(a => new DisplayAccount(null, a.LastView)).ToList(), _displayMode, _maxIcons);
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

    // ---- tray menu (docs/multi-account.md, the task's "Tray context menu" item) ----

    (ContextMenuStrip Menu, ToolStripMenuItem ToggleItem) BuildTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Foreground,
        };

        var showPanelItem = new ToolStripMenuItem("Visa panel") { ForeColor = DarkMenuColors.Foreground };
        showPanelItem.Click += (_, _) => FocusAccount(DefaultFocusAccountIndex());

        var toggleItem = new ToolStripMenuItem { ForeColor = DarkMenuColors.Foreground };
        toggleItem.Click += (_, _) => ToggleDisplayMode();

        var exitItem = new ToolStripMenuItem("Exit") { ForeColor = DarkMenuColors.Foreground };
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(showPanelItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(exitItem);
        return (menu, toggleItem);
    }

    /// <summary>Which account "Visa panel" opens on when it wasn't reached through a specific icon's own right-click: whichever icon was last right-clicked, else the current display-mode default.</summary>
    int DefaultFocusAccountIndex()
    {
        if (_lastRightClickedAccountIndex is { } idx) return idx;
        IReadOnlyList<DisplayAccount> current = CurrentDisplayAccounts();
        return EffectiveDefaultPanelAccountIndex(current, _displayMode, DateTimeOffset.UtcNow) ?? 0;
    }

    IReadOnlyList<DisplayAccount> CurrentDisplayAccounts() =>
        _demoMode
            ? (_demoFrames.Count > 0 ? _demoFrames[_demoIndex % _demoFrames.Count].Accounts : Array.Empty<DisplayAccount>())
            : _accounts.Select(a => new DisplayAccount(_accounts.Count > 1 ? a.Label : null, a.LastView)).ToList();

    /// <summary>docs/multi-account.md's context-menu toggle: writes displayMode to accounts.json and applies immediately.</summary>
    void ToggleDisplayMode()
    {
        _displayMode = _displayMode == AccountDisplayMode.PerAccount ? AccountDisplayMode.Binding : AccountDisplayMode.PerAccount;
        UpdateToggleModeItemText();
        PersistDisplayMode();
        if (!_demoMode) RenderAccounts(_accounts.Select(a => new DisplayAccount(_accounts.Count > 1 ? a.Label : null, a.LastView)).ToList(), _displayMode, _maxIcons);
    }

    /// <summary>Shows the ACTION the click would perform (what mode you'd switch TO), the common toggle-item idiom.</summary>
    void UpdateToggleModeItemText() =>
        _toggleModeItem.Text = _displayMode == AccountDisplayMode.PerAccount
            ? "Visa bara den som är närmast taket"
            : "Visa ikon per konto";

    void PersistDisplayMode()
    {
        if (_demoMode) return; // demo mode never touches the user's real accounts.json
        try
        {
            _accountsConfig.DisplayMode = _displayMode;
            AccountsConfig.Save(_accountsConfig);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"failed to persist accounts.json display mode: {ex.Message}");
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

    /// <summary>
    /// The 1Hz UI tick: Evaluate() every account in live mode (each is cheap, per
    /// QuotaModel.Evaluate's own contract) and reconcile/re-render every tray icon plus the
    /// panel, or re-push the current demo frame so its relative countdowns keep advancing.
    /// </summary>
    void Tick()
    {
        if (_demoMode)
        {
            PushCurrentDemoFrame();
            return;
        }

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        long monoMs = Environment.TickCount64;
        foreach (AccountRuntime account in _accounts) account.Evaluate(utcNow, monoMs);
        RefreshDisambiguatedLabels();

        var current = _accounts.Select(a => new DisplayAccount(_accounts.Count > 1 ? a.Label : null, a.LastView)).ToList();
        RenderAccounts(current, _displayMode, _maxIcons);
    }

    void PushCurrentDemoFrame()
    {
        if (_demoFrames.Count == 0) return;
        DemoFrame frame = _demoFrames[_demoIndex % _demoFrames.Count];
        RenderAccounts(frame.Accounts, frame.Mode, DemoMaxIcons);
    }

    void AdvanceDemo()
    {
        if (_demoFrames.Count == 0) return;
        Tick();
        _demoIndex++;
    }

    /// <summary>
    /// The shared rendering path for both live and demo accounts (docs/multi-account.md
    /// "Display"): decides which accounts get a tray icon (Model/AccountDisplayPlan.cs),
    /// creates/destroys TrayIconHandles to match, renders every live icon, and updates the panel
    /// with whichever account is currently focused plus the "other accounts" rows.
    /// </summary>
    void RenderAccounts(IReadOnlyList<DisplayAccount> current, AccountDisplayMode mode, int maxIcons)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_explicitPanelAccountIndex is { } exp && exp >= current.Count) _explicitPanelAccountIndex = null;

        var candidates = current.Select(a => new AccountDisplayPlan.Candidate(true, a.View)).ToList();
        IReadOnlyList<int> desired = current.Count == 0
            ? Array.Empty<int>()
            : AccountDisplayPlan.SelectIconAccounts(candidates, mode, Math.Max(1, maxIcons), now);
        var desiredSet = new HashSet<int>(desired);

        foreach (int key in _iconsByIndex.Keys.Where(k => !desiredSet.Contains(k)).ToList())
        {
            _iconsByIndex[key].Dispose();
            _iconsByIndex.Remove(key);
        }

        foreach (int idx in desired.OrderBy(i => i))
        {
            if (_iconsByIndex.ContainsKey(idx)) continue;
            var handle = new TrayIconHandle { NotifyIcon = { ContextMenuStrip = _trayMenu } };
            int captured = idx;
            handle.NotifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) FocusAccount(captured); };
            handle.NotifyIcon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _lastRightClickedAccountIndex = captured; };
            _iconsByIndex[idx] = handle;
        }

        bool multi = current.Count > 1;
        foreach ((int idx, TrayIconHandle handle) in _iconsByIndex)
            handle.Slot.Update(current[idx].View, multi ? current[idx].Label : null);

        if (current.Count == 0)
        {
            _shownAccountIndex = null;
            _panel.UpdateView(QuotaView.Initial, _demoMode, null, Array.Empty<OtherAccountRow>());
            _panel.SetAnchorIcon(null);
            return;
        }

        int panelIndex = _explicitPanelAccountIndex ?? EffectiveDefaultPanelAccountIndex(current, mode, now) ?? 0;
        _shownAccountIndex = panelIndex;
        DisplayAccount shown = current[panelIndex];

        IReadOnlyList<OtherAccountRow> otherRows = multi
            ? current
                .Select((a, i) => (a, i))
                .Where(t => t.i != panelIndex)
                .Select(t => PanelText.ComposeOtherAccountRow(t.i, t.a.Label ?? $"Konto {t.i + 1}", t.a.View, now, TimeZoneInfo.Local))
                .ToList()
            : Array.Empty<OtherAccountRow>();

        // The task's reload button: busy exactly while a poll for the SHOWN account is in
        // flight (never all accounts, and never demo mode -- there is no AccountRuntime to
        // poll there). Reads AccountRuntime.IsPolling, the very same flag PollAsync's own
        // concurrency guard is keyed on, so the button can never show "idle" while a poll it
        // just started is actually still running.
        bool refreshInFlight = !_demoMode && panelIndex < _accounts.Count && _accounts[panelIndex].IsPolling;

        _panel.UpdateView(shown.View, _demoMode, multi ? shown.Label ?? $"Konto {panelIndex + 1}" : null, otherRows, refreshInFlight);
        _panel.SetAnchorIcon(_iconsByIndex.TryGetValue(panelIndex, out TrayIconHandle? anchorHandle)
            ? anchorHandle.NotifyIcon
            : _iconsByIndex.Values.Select(h => h.NotifyIcon).FirstOrDefault());
    }

    /// <summary>
    /// The task's reload button: forces an immediate poll for whichever account the panel is
    /// currently showing (never all accounts). No-op in demo mode (there is no AccountRuntime),
    /// with no account currently shown, or while that account already has a poll in flight --
    /// AccountRuntime.RequestImmediateRefresh/PollAsync's own _polling guard makes the last case
    /// safe on its own, but checking IsPolling here too avoids even scheduling the redundant
    /// call. Re-ticks immediately afterward so the button's busy state shows up without waiting
    /// up to 1s for the next regular eval tick (the same pattern FocusAccount already uses).
    /// </summary>
    void OnRefreshRequested()
    {
        if (_demoMode) return;
        if (_shownAccountIndex is not { } idx || idx < 0 || idx >= _accounts.Count) return;

        AccountRuntime account = _accounts[idx];
        if (!account.IsPolling) _ = account.RequestImmediateRefresh();
        Tick();
    }

    /// <summary>docs/multi-account.md "Display": in binding mode the default focus is the binding account itself; otherwise the first configured account.</summary>
    static int? EffectiveDefaultPanelAccountIndex(IReadOnlyList<DisplayAccount> current, AccountDisplayMode mode, DateTimeOffset now)
    {
        if (current.Count == 0) return null;
        if (mode != AccountDisplayMode.Binding) return 0;

        var candidates = current.Select(a => new AccountDisplayPlan.Candidate(true, a.View)).ToList();
        return AccountDisplayPlan.SelectBinding(candidates, now) ?? 0;
    }

    /// <summary>
    /// docs/multi-account.md "Panel": clicking a tray icon opens the panel on that icon's
    /// account; clicking an "other accounts" row switches to that account. If the panel is
    /// already open on this exact account, this click closes it instead (the existing
    /// single-icon toggle behaviour), so a click doesn't reopen what it just closed.
    /// </summary>
    void FocusAccount(int accountIndex)
    {
        if (_panel.Visible && _explicitPanelAccountIndex == accountIndex) { _panel.Toggle(); return; }
        _explicitPanelAccountIndex = accountIndex;
        if (!_panel.Visible) _panel.ShowPanel();
        Tick();
    }

    /// <summary>
    /// Recomputes AccountLabel.Disambiguate over the whole account set and installs the result
    /// on each AccountRuntime (docs/multi-account.md "Labels": duplicate labels get the plan,
    /// then the email local part, appended).
    /// </summary>
    void RefreshDisambiguatedLabels()
    {
        if (_accounts.Count == 0) return;
        var inputs = _accounts
            .Select(a => new AccountLabelInput(a.OverrideLabel, a.Identity, a.SubscriptionType))
            .ToList();
        IReadOnlyList<string> labels = AccountLabel.Disambiguate(inputs);
        for (int i = 0; i < _accounts.Count; i++) _accounts[i].ApplyDisambiguatedLabel(labels[i]);
    }

    /// <summary>The full --demo / --capture-states cycle: every legacy single-account DemoQuotaSource state, then the multi-account frames (docs/multi-account.md, the task's "Demo mode" item).</summary>
    static IReadOnlyList<DemoFrame> BuildDemoFrames(DateTimeOffset utcNow)
    {
        var frames = new List<DemoFrame>();
        foreach (DemoQuotaSource.DemoState s in DemoQuotaSource.Build(utcNow))
            frames.Add(new DemoFrame(s.Key, AccountDisplayMode.PerAccount, new[] { new DisplayAccount(null, s.View) }, FocusIndex: 0));

        foreach (MultiAccountDemoSource.MultiState m in MultiAccountDemoSource.Build(utcNow))
        {
            IReadOnlyList<DisplayAccount> accounts = m.Accounts.Select(a => new DisplayAccount(a.Label, a.View)).ToList();
            frames.Add(new DemoFrame(m.Key, m.Mode, accounts, m.FocusIndex));
        }

        return frames;
    }

    /// <summary>
    /// Automation-only path for --capture-states/--capture-icon-sheet (see the
    /// task's verification step 3): steps through every demo frame (single- and
    /// multi-account, both display modes), showing and screen-capturing the real,
    /// non-activating panel (safe: it never takes focus, which is the whole point
    /// of ShowWithoutActivation/WS_EX_NOACTIVATE), then renders the icon contact
    /// sheet purely in-memory, then exits.
    /// </summary>
    public void RunAutomatedCapture(string? statesDir, string? iconSheetPath)
    {
        // Both timers that would otherwise independently re-render demo content must stop: the
        // 4s _demoTimer obviously, but also the regular 1Hz _evalTimer -- its demo-mode Tick()
        // path re-renders _demoFrames[_demoIndex % Count] using the field _demoIndex, which is
        // now frozen (only _demoTimer's own handler ever advances it) and out of sync with this
        // method's own local `i`. Left running, it would race this method's render+capture
        // sequence every second and occasionally overwrite the panel with a stale frame right
        // before the screenshot (confirmed: intermittently captured the wrong frame before this
        // fix). RunAutomatedCapture drives its own render (via RenderAccounts) + capture loop
        // below, so the regular eval timer has nothing left to do during this run.
        _evalTimer.Stop();
        _demoTimer?.Stop();
        IReadOnlyList<DemoFrame> frames = BuildDemoFrames(DateTimeOffset.UtcNow);
        int i = 0;
        bool waitingToCapture = false;
        var stepTimer = new System.Windows.Forms.Timer { Interval = 250 };

        stepTimer.Tick += (_, _) =>
        {
            if (_shuttingDown) { stepTimer.Stop(); return; }
            stepTimer.Stop();

            if (waitingToCapture)
            {
                if (statesDir != null) CapturePanel(frames[i].Key, statesDir);
                _panel.HidePanel();
                i++;
                waitingToCapture = false;
            }

            if (i >= frames.Count)
            {
                if (iconSheetPath != null) SaveIconSheet(DemoQuotaSource.Build(DateTimeOffset.UtcNow), iconSheetPath);
                stepTimer.Dispose();
                ExitApp();
                return;
            }

            _explicitPanelAccountIndex = frames[i].FocusIndex < frames[i].Accounts.Count ? frames[i].FocusIndex : 0;
            RenderAccounts(frames[i].Accounts, frames[i].Mode, DemoMaxIcons);
            _panel.ShowPanel();
            waitingToCapture = true;
            stepTimer.Interval = 500; // let layout/paint settle before the screenshot
            stepTimer.Start();
        };
        stepTimer.Start();
    }

    /// <summary>
    /// Invalidate + Update forces a synchronous WM_PAINT right before the capture (cheap
    /// insurance against the panel's own paint not yet having landed), then copies the composited
    /// desktop pixels at the panel's bounds -- PrintWindow was tried here instead and rejected:
    /// against this window's WS_EX_TOOLWINDOW/WS_EX_TOPMOST/ShowWithoutActivation combination it
    /// intermittently produced solid-black captures, strictly worse than CopyFromScreen. The
    /// actual stale-frame race (captured the wrong demo frame) was the 1Hz _evalTimer racing this
    /// method's own render+capture loop -- see RunAutomatedCapture's _evalTimer.Stop().
    /// </summary>
    void CapturePanel(string name, string dir)
    {
        Directory.CreateDirectory(dir);
        _panel.Invalidate();
        _panel.Update();

        Rectangle bounds = _panel.Bounds;
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(Path.Combine(dir, $"{name}.png"), ImageFormat.Png);
    }

    /// <summary>All 8 demo states x 4 sizes x 2 backgrounds, 8x nearest-neighbour scaled, composed in-memory (no window, no screen capture needed). Icon renders are single-account-shaped by design -- unrelated to the multi-account panel captures above.</summary>
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

        try { _evalTimer.Stop(); _evalTimer.Dispose(); } catch { /* best effort */ }
        try { _demoTimer?.Stop(); _demoTimer?.Dispose(); } catch { /* best effort */ }

        foreach (TrayIconHandle handle in _iconsByIndex.Values)
        {
            try { handle.Dispose(); } catch (Exception ex) { SafeLog.Warn($"TrayIconHandle.Dispose during shutdown threw: {ex.Message}"); }
        }
        _iconsByIndex.Clear();

        // Terminate every account's child and flush its log writer off the UI thread, bounded
        // (Codex review High #5): closing a redirected pipe/flushing the CSV can block if the
        // child is not reading or the disk is slow, and that must never hang the thread this
        // runs on across every exit route. One account's cleanup hanging must not stop another's.
        RunBoundedOnBackgroundThread(async () =>
        {
            foreach (AccountRuntime account in _accounts)
            {
                try { await account.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { SafeLog.Warn($"account disposal during shutdown threw: {ex.Message}"); }
            }
        }, ShutdownDeadline);

        try { _trayMenu.Dispose(); } catch { /* best effort */ }
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

    /// <summary>docs/multi-account.md's tray context menu, "Keep it dark-themed like today": matches PanelForm's own dark palette rather than the WinForms default light menu.</summary>
    sealed class DarkMenuColors : ProfessionalColorTable
    {
        public static readonly Color Background = Color.FromArgb(255, 0x20, 0x20, 0x20);
        public static readonly Color Foreground = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
        static readonly Color Border = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);
        static readonly Color Hover = Color.FromArgb(255, 0x3A, 0x3A, 0x3A);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
