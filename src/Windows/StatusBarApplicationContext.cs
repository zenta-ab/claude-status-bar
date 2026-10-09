using System.Drawing.Imaging;
using System.Globalization;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Flow;
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
/// always shows one account at a time (_explicitPanelSlot -- a slot id, never a list position -- or the display-mode default
/// when unset) plus, when more than one account is configured, a compact "other accounts" list
/// any row of which switches the panel to that account.
///
/// Account set (docs/multi-account.md "Account set"): the list is rebuilt, not patched, on every add /
/// remove / re-login (RebuildAccountSetAsync): every runtime is stopped gracefully, the config is
/// reconciled against the login slots, and a new set is built and started. Everything that must
/// survive a rebuild (panel focus, the right-clicked icon, the NeedsLogin toast state) is remembered
/// by slot id. Logins are only ever created, replaced and removed here (the login flow, "Lägg till
/// konto…", "Konton" in the tray menu) -- always into a NEW slot folder this app owns.
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

    /// <summary>
    /// One account as the display/icon-selection layer needs it: a label (null keeps the
    /// single-account panel/tooltip pixel-identical) and its last QuotaView.
    ///
    /// IsDuplicate/DuplicateRow (docs/multi-account.md "Duplicate accounts", live mode only --
    /// demo frames never contain a duplicate): when IsDuplicate is true this account never gets a
    /// tray icon or becomes the binding/default account (RenderAccounts/
    /// EffectiveDefaultPanelAccountIndex both treat it as ineligible), and DuplicateRow is the
    /// pre-built explanatory row (Ui/PanelText.ComposeDuplicateAccountRow) RenderAccounts uses in
    /// its "other accounts" list INSTEAD OF the normal verdict row. DuplicateSuffix is the short
    /// tooltip-only note the account THIS ONE duplicates gets appended to its icon tooltip
    /// (never its panel header label -- see RenderAccounts' icon-update loop).
    /// </summary>
    readonly record struct DisplayAccount(string Slot, string? Label, QuotaView View, bool IsDuplicate = false, OtherAccountRow? DuplicateRow = null, string? DuplicateSuffix = null, string? Title = null, string? Subtitle = null);

    /// <summary>One step of the --demo / --capture-states cycle: either a legacy single-account state (Accounts.Count == 1, Label null) or a MultiAccountDemoSource frame.</summary>
    readonly record struct DemoFrame(string Key, AccountDisplayMode Mode, IReadOnlyList<DisplayAccount> Accounts, int FocusIndex, PanelDisplayMode Panel = PanelDisplayMode.Single, bool DetailOfCombined = false, int HoverIndex = -1);

    readonly bool _demoMode;
    readonly PanelForm _panel;
    readonly System.Windows.Forms.Timer _evalTimer;
    readonly System.Windows.Forms.Timer? _demoTimer;
    readonly System.Windows.Forms.Timer _loadingTimer; // drives the loading icon's frames; runs only while some account is loading
    readonly SynchronizationContext _uiContext;
    readonly List<AccountRuntime> _accounts = new();
    readonly Dictionary<string, TrayIconHandle> _iconsBySlot = new();
    readonly ContextMenuStrip _trayMenu;
    readonly ToolStripMenuItem _toggleModeItem;
    readonly Dictionary<PanelDisplayMode, ToolStripMenuItem> _panelModeItems = new();
    PanelDisplayMode _panelMode = PanelDisplayMode.Single; // docs/multi-account.md "All accounts in one panel"
    bool _combinedOpen;       // the panel is showing the combined view (only meaningful with _panelMode == All)
    string? _markedSlot;      // the card of the icon that was clicked
    readonly ToolStripMenuItem _showPanelItem;
    readonly ToolStripMenuItem _statisticsItem;
    readonly ToolStripMenuItem _addAccountItem;
    readonly ToolStripMenuItem _accountsItem;
    readonly SlotStore _slots = SlotStore.Default;
    readonly ClaudeAuthCli _auth;
    readonly CancellationTokenSource _shutdownCts = new();
    StatisticsForm? _statisticsForm;
    TrayIconHandle? _emptyIcon; // the one grey "log in" icon of the zero-accounts state
    EventWaitHandle? _exitEvent;
    RegisteredWaitHandle? _exitWait;

    AccountsConfig _accountsConfig = AccountsConfig.Empty();
    AccountDisplayMode _displayMode = AccountDisplayMode.PerAccount;
    int _maxIcons = 3;
    string? _explicitPanelSlot;
    string? _lastRightClickedSlot;
    string? _shownSlot; // whichever slot RenderAccounts last showed the panel for -- see OnRefreshRequested
    IReadOnlyList<string?> _renderedSlots = Array.Empty<string?>(); // slot per row of the last render, so a row click maps to the account it was drawn for
    AccountFlowController? _flows; // null in demo mode (no accounts to change)

    IReadOnlyList<DemoFrame> _demoFrames = Array.Empty<DemoFrame>();
    int _demoIndex;
    bool _shuttingDown;
    bool _capturing; // --capture-states drives its own render loop: the loading timer must not repaint over it

    public StatusBarApplicationContext(bool demoMode = false, bool showPanelOnStartup = false)
    {
        _demoMode = demoMode;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _uiContext = SynchronizationContext.Current!;

        _auth = new ClaudeAuthCli(_slots);
        (_trayMenu, _toggleModeItem, _showPanelItem, _statisticsItem, _addAccountItem, _accountsItem) = BuildTrayMenu();
        _panel = new PanelForm();
        _panel.OtherAccountClicked += FocusRenderedRow;
        _panel.CardClicked += slot => { _combinedOpen = false; _explicitPanelSlot = slot; Tick(); };
        _panel.BackToAllRequested += () => { _combinedOpen = true; Tick(); };
        _panel.CardReloginClicked += slot => StartLoginFlow(slot);
        _panel.RefreshRequested += OnRefreshRequested;
        _panel.ReloginRequested += slot => { if ((slot ?? _shownSlot) is { } s) StartLoginFlow(s); };
        _panel.AdviceClicked += () => OpenStatisticsWindow();

        _evalTimer = new System.Windows.Forms.Timer { Interval = (int)EvalTickInterval.TotalMilliseconds };
        _evalTimer.Tick += (_, _) => SafeTick();

        // docs/multi-account.md "Loading": ~8 frames a second, and only while an account is loading
        // (UpdateLoadingTimer starts and stops it). Each tick is the ordinary tick, which re-renders
        // an icon only when its params -- the frame number included -- changed.
        _loadingTimer = new System.Windows.Forms.Timer { Interval = LoadingFrames.FrameIntervalMs };
        _loadingTimer.Tick += (_, _) => SafeTick();

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
            _accountsConfig = AccountFlowController.LoadAndReconcile(_slots);
            _flows = new AccountFlowController(_accountsConfig, _slots, new RuntimeHost(this), _auth, new FlowUi(this), SaveAccountsConfig, _shutdownCts.Token);
            _displayMode = _accountsConfig.DisplayMode;
            _maxIcons = Math.Max(1, _accountsConfig.MaxIcons);
            _panelMode = _accountsConfig.EffectivePanelMode;
            UpdateToggleModeItemText();
            UpdatePanelModeItem();

            RunStatisticsBackfillOnceInBackground();
            ListenForExitSignal();

            BuildAccountSet();

            // Every account is driven independently from here on (docs/multi-account.md): one
            // account's channel failing to launch, being logged out, or its config directory
            // going missing must never stop any other account's polling or eval loop.
            foreach (AccountRuntime account in _accounts) account.Start();

            // AccountRuntime's constructor already reads Identity synchronously, so two accounts
            // that resolve to the same already-logged-in identity are detectable before the first
            // eval tick -- run the duplicate pass once here too, or the very first render would
            // briefly show two identical icons before Tick() caught up a second later.
            RefreshDuplicates();
            RenderAccounts(BuildLiveDisplayAccounts(), _displayMode, _maxIcons);
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

    /// <summary>
    /// docs/statistics.md, decision 5: derives cycles.csv/hourly-*.csv from the window-shape
    /// history already on disk, once per install/update -- gated by a marker file so an ordinary
    /// restart (this app relaunches often) never re-scans months of CSVs. Re-running is always
    /// safe regardless (StatisticsBackfill.Run merges by key, never duplicates), so a missing or
    /// corrupt marker just means it runs again next launch -- never a correctness problem, only
    /// a bit of wasted work. Runs on a background thread and never touches the UI: a slow disk
    /// scan must not delay the first poll or the first icon paint.
    /// </summary>
    static void RunStatisticsBackfillOnceInBackground()
    {
        string baseLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
        string marker = Path.Combine(baseLogDir, ".statistics-backfilled");
        if (File.Exists(marker)) return;

        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(baseLogDir);
                StatisticsBackfill.Run(baseLogDir);
                File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch
            {
                // best-effort, like every other disk-logging path in this app.
            }
        });
    }

    // ---- account set (docs/multi-account.md "Account set") ----
    //
    // What changes WHICH accounts exist -- add, re-login, remove, discard, rebuild, and the NeedsLogin
    // toast -- lives in Flow/AccountFlowController, which is tested without any window. This class
    // supplies its seams: RuntimeHost (the live runtimes), FlowUi (dialogs, toasts, what is remembered
    // by slot id) and the config save.

    /// <summary>
    /// The named event scripts\install.ps1 signals to ask a running app to exit through its normal
    /// graceful shutdown (children stopped cleanly) instead of being killed. Auto-reset, so a
    /// signal sent while no app is waiting cannot make the NEXT instance exit the moment it starts.
    /// </summary>
    public const string ExitEventName = @"Local\ClaudeStatusBar-Exit";

    void ListenForExitSignal()
    {
        try
        {
            _exitEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ExitEventName);
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, timedOut) =>
            {
                if (timedOut) return;
                _uiContext.Post(_ => { if (!_shuttingDown) ExitApp(); }, null);
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"exit signal unavailable (install.ps1 falls back to killing the app): {ex.Message}");
        }
    }

    /// <summary>Saves accounts.json; throws on failure (the flows decide what a failed save means). Demo mode never touches the user's real file.</summary>
    void SaveAccountsConfig(AccountsConfig config)
    {
        if (_demoMode) return;
        AccountsConfig.Save(config);
    }

    /// <summary>One AccountRuntime per enabled entry, in config order. A slot the path guard refuses is skipped with a warning, never started.</summary>
    void BuildAccountSet()
    {
        _accounts.Clear();
        int index = 0;
        foreach (AccountEntry entry in _accountsConfig.Accounts)
        {
            if (entry.Enabled && entry.Slot is { } slot)
            {
                try { _accounts.Add(new AccountRuntime(slot, entry, index, _uiContext, slots: _slots)); }
                catch (Exception ex) { SafeLog.Warn($"slot {slot} not started: {ex.Message}"); }
            }
            index++;
        }
    }

    /// <summary>Starts a login flow (reloginSlot null = add). Menu, icon and panel entry point; no-op in demo mode.</summary>
    void StartLoginFlow(string? reloginSlot)
    {
        if (_flows is { } flows) _ = flows.RunLoginFlowAsync(reloginSlot);
    }

    void StartRemove(string slot)
    {
        if (_flows is { } flows) _ = flows.RemoveAsync(slot);
    }

    /// <summary>The live runtimes as the flow controller sees them.</summary>
    sealed class RuntimeHost : IRuntimeHost
    {
        readonly StatusBarApplicationContext _owner;
        readonly Dictionary<string, AccountRuntime> _stopped = new(StringComparer.Ordinal); // taken out by StopAsync, kept for KillAsync

        public RuntimeHost(StatusBarApplicationContext owner) => _owner = owner;

        public IReadOnlyList<RunningAccount> Running =>
            _owner._accounts
                .Select(a => new RunningAccount(a.Slot, a.Label, a.Identity, a.SubscriptionType, a.NeedsLogin, a.LastView.LastSuccessAt is not null))
                .ToList();

        public async Task<bool> StopAsync(IReadOnlyCollection<string> slots)
        {
            List<AccountRuntime> runtimes = _owner._accounts.Where(a => slots.Contains(a.Slot)).ToList();
            foreach (AccountRuntime runtime in runtimes)
            {
                _owner._accounts.Remove(runtime); // out of the live list at once: no tick renders a half-stopped account
                _stopped[runtime.Slot] = runtime;
            }
            return await StopAccountsAsync(runtimes, ShutdownDeadline);
        }

        public Task KillAsync(IReadOnlyCollection<string> slots) => Task.Run(() =>
        {
            foreach (string slot in slots)
            {
                if (!_stopped.Remove(slot, out AccountRuntime? runtime)) continue;
                int killed = runtime.KillChildren();
                SafeLog.Warn($"slot {slot}: {killed} child process(es) killed after the stop timed out");
            }
        });

        public void StartAll(AccountsConfig config)
        {
            _stopped.Clear();
            foreach (TrayIconHandle handle in _owner._iconsBySlot.Values)
            {
                try { handle.Dispose(); } catch (Exception ex) { SafeLog.Warn($"TrayIconHandle.Dispose during rebuild threw: {ex.Message}"); }
            }
            _owner._iconsBySlot.Clear();
            try { _owner._statisticsForm?.Dispose(); } catch { /* best effort */ }
            _owner._statisticsForm = null;

            _owner.BuildAccountSet();
            foreach (AccountRuntime account in _owner._accounts) account.Start();
            _owner.RefreshDuplicates();
            _owner.RenderAccounts(_owner.BuildLiveDisplayAccounts(), _owner._displayMode, _owner._maxIcons);
        }
    }

    /// <summary>
    /// Stops the given runtimes in parallel (each gracefully), bounded by `deadline`; one hanging stop
    /// never holds up the others. False when the deadline passed first -- the caller must then kill
    /// what is left (AccountRuntime.KillChildren) before relying on the children being gone.
    /// </summary>
    static async Task<bool> StopAccountsAsync(IReadOnlyCollection<AccountRuntime> accounts, TimeSpan deadline)
    {
        if (accounts.Count == 0) return true;
        Task all = Task.WhenAll(accounts.Select(async account =>
        {
            try { await account.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { SafeLog.Warn($"stopping an account threw: {ex.Message}"); }
        }));
        Task winner = await Task.WhenAny(all, Task.Delay(deadline)).ConfigureAwait(false);
        return winner == all;
    }

    /// <summary>Dialogs, toasts, and whatever the UI remembers by slot id.</summary>
    sealed class FlowUi : IFlowUi
    {
        readonly StatusBarApplicationContext _owner;

        public FlowUi(StatusBarApplicationContext owner) => _owner = owner;

        public bool Confirm(string title, string body) =>
            ShowDialog(body, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        public void Inform(string text, bool warning) =>
            ShowDialog(text, LoginText.WindowCaption, MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);

        public void Toast(string text) => _owner.ShowToast(text);

        public void LabelsChanged() => _owner.Tick(); // labels are re-derived from the entries every tick; nothing is rebuilt

        /// <summary>A small topmost dialog: hint, text box prefilled with the current name, OK / Avbryt.</summary>
        public string? PromptName(string title, string hint, string current, int maxLength)
        {
            using var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                TopMost = true,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(360, 124),
            };
            var hintLabel = new Label { Text = hint, Location = new Point(12, 12), Size = new Size(336, 34) };
            var box = new TextBox { Text = current, MaxLength = maxLength, Location = new Point(12, 50), Width = 336 };
            var ok = new Button { Text = LoginText.RenameOk, DialogResult = DialogResult.OK, Location = new Point(192, 88), Size = new Size(75, 26) };
            var cancel = new Button { Text = LoginText.RenameCancel, DialogResult = DialogResult.Cancel, Location = new Point(273, 88), Size = new Size(75, 26) };
            form.Controls.AddRange(new Control[] { hintLabel, box, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            form.Shown += (_, _) => { form.Activate(); SetForegroundWindow(form.Handle); box.Focus(); box.SelectAll(); };
            return form.ShowDialog() == DialogResult.OK ? box.Text : null;
        }

        public void SlotReplaced(string oldSlot, string newSlot)
        {
            if (_owner._explicitPanelSlot == oldSlot) _owner._explicitPanelSlot = newSlot;
            if (_owner._lastRightClickedSlot == oldSlot) _owner._lastRightClickedSlot = newSlot;
        }

        public void SlotRemoved(string slot)
        {
            if (_owner._explicitPanelSlot == slot) _owner._explicitPanelSlot = null;
            if (_owner._lastRightClickedSlot == slot) _owner._lastRightClickedSlot = null;
        }

        /// <summary>
        /// A MessageBox needs an owner that is topmost and activated, or it can open behind the browser
        /// the user has just been in -- a tray app has no window of its own to own it. The owner is a
        /// 1x1 off-screen topmost form shown (and brought to the foreground) just for the dialog.
        /// </summary>
        static DialogResult ShowDialog(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            using var owner = new Form
            {
                TopMost = true,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1, 1),
            };
            owner.Show();
            owner.Activate();
            SetForegroundWindow(owner.Handle);
            return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    // ---- tray menu (docs/multi-account.md, the task's "Tray context menu" item) ----

    (ContextMenuStrip Menu, ToolStripMenuItem ToggleItem, ToolStripMenuItem ShowPanelItem, ToolStripMenuItem StatisticsItem, ToolStripMenuItem AddAccountItem, ToolStripMenuItem AccountsItem) BuildTrayMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Foreground,
        };

        var showPanelItem = new ToolStripMenuItem("Visa panel") { ForeColor = DarkMenuColors.Foreground };
        showPanelItem.Click += (_, _) => { if (DefaultFocusSlot() is { } slot) OnIconClicked(slot); };

        var toggleItem = new ToolStripMenuItem { ForeColor = DarkMenuColors.Foreground };
        toggleItem.Click += (_, _) => ToggleDisplayMode();

        // "Panel >": what clicking an icon opens -- three radio items (docs/multi-account.md "Panels").
        var panelMenu = new ToolStripMenuItem(PanelMenuText) { ForeColor = DarkMenuColors.Foreground };
        panelMenu.DropDown.Renderer = menu.Renderer;
        panelMenu.DropDown.BackColor = DarkMenuColors.Background;
        foreach ((PanelDisplayMode mode, string text) in new[]
        {
            (PanelDisplayMode.Single, PanelSingleText), (PanelDisplayMode.Full, PanelFullText), (PanelDisplayMode.Cards, PanelCardsText),
        })
        {
            var item = new ToolStripMenuItem(text) { ForeColor = DarkMenuColors.Foreground };
            PanelDisplayMode captured = mode;
            item.Click += (_, _) => SetPanelMode(captured);
            _panelModeItems[mode] = item;
            panelMenu.DropDownItems.Add(item);
        }

        var statisticsItem = new ToolStripMenuItem("Statistik…") { ForeColor = DarkMenuColors.Foreground };
        statisticsItem.Click += (_, _) => OpenStatisticsWindow();

        // docs/multi-account.md "Login flow": adding an account is a top-level entry on every icon
        // (the zero-accounts icon included); managing an existing one is under "Konton".
        var addAccountItem = new ToolStripMenuItem(LoginText.AddAccountMenu) { ForeColor = DarkMenuColors.Foreground };
        addAccountItem.Click += (_, _) => StartLoginFlow(reloginSlot: null);

        var accountsItem = new ToolStripMenuItem(LoginText.AccountsMenu) { ForeColor = DarkMenuColors.Foreground };

        var exitItem = new ToolStripMenuItem("Exit") { ForeColor = DarkMenuColors.Foreground };
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(showPanelItem);
        menu.Items.Add(toggleItem);
        menu.Items.Add(panelMenu);
        menu.Items.Add(statisticsItem);
        menu.Items.Add(addAccountItem);
        menu.Items.Add(accountsItem);
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) => RefreshTrayMenu();
        return (menu, toggleItem, showPanelItem, statisticsItem, addAccountItem, accountsItem);
    }

    /// <summary>
    /// Brings the menu in line with the account set each time it opens: entries that need an
    /// account are disabled without one (the zero-accounts state keeps "Lägg till konto…" and
    /// Exit), the login entries are disabled while a login / remove / rebuild is running, and the
    /// "Konton" submenu lists one entry per tracked account, each addressed by SLOT ID (the closures
    /// below capture the slot, never a list position).
    /// </summary>
    void RefreshTrayMenu()
    {
        bool hasAccounts = _demoMode || _accounts.Count > 0;
        bool busy = _flows is { IsBusy: true } or { IsMutating: true };

        _showPanelItem.Enabled = hasAccounts;
        _statisticsItem.Enabled = hasAccounts;
        _addAccountItem.Visible = !_demoMode;
        _addAccountItem.Enabled = !busy;

        foreach (ToolStripItem old in _accountsItem.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
        _accountsItem.DropDownItems.Clear();

        var entries = _demoMode ? new List<AccountEntry>() : _accountsConfig.Accounts.Where(a => a.Slot is not null).ToList();
        _accountsItem.Visible = entries.Count > 0;
        _accountsItem.Enabled = !busy;
        if (entries.Count == 0) return;

        _accountsItem.DropDown.Renderer = _trayMenu.Renderer;
        _accountsItem.DropDown.BackColor = DarkMenuColors.Background;
        foreach (AccountEntry entry in entries)
        {
            string slot = entry.Slot!;
            var accountItem = new ToolStripMenuItem((_flows?.LabelForMenu(slot) ?? slot).Replace("&", "&&")) { ForeColor = DarkMenuColors.Foreground };
            accountItem.DropDown.Renderer = _trayMenu.Renderer;
            accountItem.DropDown.BackColor = DarkMenuColors.Background;

            var relogin = new ToolStripMenuItem(LoginText.ReloginMenu) { ForeColor = DarkMenuColors.Foreground, Enabled = !busy };
            relogin.Click += (_, _) => StartLoginFlow(slot);
            var rename = new ToolStripMenuItem(LoginText.RenameMenu) { ForeColor = DarkMenuColors.Foreground, Enabled = !busy };
            rename.Click += (_, _) => _flows?.Rename(slot);
            var remove = new ToolStripMenuItem(LoginText.RemoveMenu) { ForeColor = DarkMenuColors.Foreground, Enabled = !busy };
            remove.Click += (_, _) => StartRemove(slot);

            accountItem.DropDownItems.Add(relogin);
            accountItem.DropDownItems.Add(rename);
            accountItem.DropDownItems.Add(remove);
            _accountsItem.DropDownItems.Add(accountItem);
        }
    }

    /// <summary>
    /// docs/statistics.md decision 4, the task's item 1: opens the statistics window (a real,
    /// focusable top-level window, unlike the panel) for whichever accounts are currently
    /// configured -- demo mode gets the two synthetic scenarios (Model/StatisticsDemoData)
    /// written to a scratch log directory so the window's own IO-reading code path (Data/
    /// StatisticsDataLoader) never needs a demo-only branch; live mode points it at every real
    /// account's own log directory. Reuses one instance across opens (recreated only if the
    /// account set changed shape or the previous instance was closed/disposed).
    /// </summary>
    public void OpenStatisticsWindow(int? forcedAccountIndex = null)
    {
        try
        {
            if (_statisticsForm is null || _statisticsForm.IsDisposed)
                _statisticsForm = BuildStatisticsForm();

            if (!_statisticsForm.Visible) _statisticsForm.Show();
            _statisticsForm.WindowState = FormWindowState.Normal;
            _statisticsForm.Activate();
            _statisticsForm.Reload();
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"OpenStatisticsWindow failed: {ex.Message}");
        }
    }

    StatisticsForm BuildStatisticsForm()
    {
        // The task's duplicate-aware fix: a duplicate account (docs/multi-account.md, same
        // AccountIdentity.StateKey as an earlier one in config order -- Model/AccountDuplicates.cs)
        // must show up exactly once here too, the same "keep only the first" rule the tray/panel
        // already apply via AccountRuntime.IsDuplicate -- never a second identical selector entry
        // for what is really one login.
        IReadOnlyList<StatisticsForm.AccountRef> refs = _demoMode
            ? BuildDemoStatisticsAccountRefs(DateTimeOffset.UtcNow)
            : _accounts
                .Select((a, i) => (Account: a, Index: i))
                .Where(x => !x.Account.IsDuplicate)
                .Select(x => new StatisticsForm.AccountRef(
                    StatisticsDataLoader.ResolveAccountLabel(x.Account.Label, new AccountLabelInput(x.Account.OverrideLabel, x.Account.Identity, x.Account.SubscriptionType), x.Account.CurrentLogDir, x.Index),
                    x.Account.CurrentLogDir, x.Account.IdentityKey ?? "_pending"))
                .ToList();
        return new StatisticsForm(refs, TimeZoneInfo.Local);
    }

    /// <summary>
    /// The task's demo/capture item: writes Model/StatisticsDemoData's two synthetic scenarios to
    /// a scratch directory under Path.GetTempPath() (never the user's real %LOCALAPPDATA%\
    /// ClaudeStatusBar\logs) via the exact same CycleArchiveCsv/HourlyRollupCsv writers the live
    /// app uses, so the statistics window's normal disk-reading path (StatisticsDataLoader) is
    /// exercised unchanged -- no demo-only branch inside the window itself.
    /// </summary>
    static IReadOnlyList<StatisticsForm.AccountRef> BuildDemoStatisticsAccountRefs(DateTimeOffset utcNow)
    {
        TimeZoneInfo tz = TimeZoneInfo.Local;
        string scratchRoot = Path.Combine(Path.GetTempPath(), "ClaudeStatusBarDemoStats", Guid.NewGuid().ToString("N"));

        string freshDir = Path.Combine(scratchRoot, "fresh-install");
        WriteDemoAccount(freshDir, StatisticsDemoData.BuildFreshInstall(utcNow, tz));

        string yearDir = Path.Combine(scratchRoot, "full-year");
        WriteDemoAccount(yearDir, StatisticsDemoData.BuildFullYear(utcNow, tz));

        return new[]
        {
            new StatisticsForm.AccountRef("Nyinstallerad (2 veckor)", freshDir, StatisticsDemoData.AccountKey),
            new StatisticsForm.AccountRef("Ett år av data", yearDir, StatisticsDemoData.AccountKey),
        };
    }

    static void WriteDemoAccount(string dir, StatisticsDemoData.DemoAccountData data)
    {
        Directory.CreateDirectory(dir);
        CycleArchiveCsv.WriteAllAtomically(Path.Combine(dir, CycleArchiveCsv.FileName), data.Cycles);
        foreach (IGrouping<int, HourlyRollupCsv.Row> yearGroup in data.Hours.GroupBy(h => h.HourStartUtc.UtcDateTime.Year))
            HourlyRollupCsv.WriteAllAtomically(dir, yearGroup.Key, yearGroup);
    }

    /// <summary>Which account "Visa panel" opens on when it wasn't reached through a specific icon's own right-click: whichever icon was last right-clicked, else the current display-mode default.</summary>
    string? DefaultFocusSlot()
    {
        IReadOnlyList<DisplayAccount> current = CurrentDisplayAccounts();
        if (current.Count == 0) return null;
        if (_lastRightClickedSlot is { } slot && current.Any(a => a.Slot == slot)) return slot;
        return current[EffectiveDefaultPanelAccountIndex(current, _displayMode, DateTimeOffset.UtcNow) ?? 0].Slot;
    }

    IReadOnlyList<DisplayAccount> CurrentDisplayAccounts() =>
        _demoMode
            ? (_demoFrames.Count > 0 ? _demoFrames[_demoIndex % _demoFrames.Count].Accounts : Array.Empty<DisplayAccount>())
            : BuildLiveDisplayAccounts();

    /// <summary>docs/multi-account.md's context-menu toggle: writes displayMode to accounts.json and applies immediately.</summary>
    void ToggleDisplayMode()
    {
        _displayMode = _displayMode == AccountDisplayMode.PerAccount ? AccountDisplayMode.Binding : AccountDisplayMode.PerAccount;
        UpdateToggleModeItemText();
        PersistDisplayMode();
        if (!_demoMode) RenderAccounts(BuildLiveDisplayAccounts(), _displayMode, _maxIcons);
    }

    public const string PanelMenuText = "Panel";
    public const string PanelSingleText = "Ett konto i taget";
    public const string PanelFullText = "Alla konton – fulla paneler sida vid sida";
    public const string PanelCardsText = "Alla konton – kompakta kort";

    void UpdatePanelModeItem()
    {
        foreach ((PanelDisplayMode mode, ToolStripMenuItem item) in _panelModeItems) item.Checked = mode == _panelMode;
    }

    /// <summary>
    /// The tray submenu "Panel": clicking an icon opens that account's panel (Single, the default), every
    /// account's full panel side by side (Full), or one panel of compact cards (Cards). Persisted as
    /// "panelMode" in accounts.json (absent = single).
    /// </summary>
    void SetPanelMode(PanelDisplayMode mode)
    {
        if (mode == _panelMode) return;
        _panelMode = mode;
        _combinedOpen = false;
        _panel.HidePanel();
        UpdatePanelModeItem();
        if (!_demoMode)
        {
            try
            {
                _accountsConfig.PanelMode = mode == PanelDisplayMode.Single ? null : mode; // absent = single
                AccountsConfig.Save(_accountsConfig);
            }
            catch (Exception ex)
            {
                SafeLog.Warn($"failed to persist accounts.json panel mode: {ex.Message}");
            }
            RenderAccounts(BuildLiveDisplayAccounts(), _displayMode, _maxIcons);
        }
    }

    /// <summary>
    /// A click on a tray icon (or "Visa panel"). Single: that account's detailed panel. Cards / Full: the
    /// combined panel / the row of full panels, with the clicked icon's card / panel marked; a second click
    /// on the same icon closes it.
    /// </summary>
    void OnIconClicked(string slot)
    {
        if (_panelMode == PanelDisplayMode.Single) { FocusSlot(slot); return; }

        if (_panel.Visible && _combinedOpen && _markedSlot == slot) { _panel.Toggle(); return; }
        _combinedOpen = true;
        _markedSlot = slot;
        Tick(); // build the view first, so the panel opens at its real size
        if (!_panel.Visible) _panel.ShowPanel();
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

        if (_flows is { IsMutating: true }) return; // the set is being changed (the flow renders once it is done): no focus is resolved against a half-changed list, no zero-accounts flash

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        long monoMs = Environment.TickCount64;
        foreach (AccountRuntime account in _accounts) account.Evaluate(utcNow, monoMs);
        RefreshDuplicates();
        RefreshDisambiguatedLabels();
        _flows?.NotifyNeedsLoginTransitions();

        RenderAccounts(BuildLiveDisplayAccounts(), _displayMode, _maxIcons);
    }

    /// <summary>A tray balloon from whichever icon exists (the zero-accounts icon included).</summary>
    void ShowToast(string text)
    {
        try
        {
            NotifyIcon? icon = _iconsBySlot.Values.Select(h => h.NotifyIcon).FirstOrDefault() ?? _emptyIcon?.NotifyIcon;
            icon?.ShowBalloonTip(6000, LoginText.WindowCaption, text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"toast failed: {ex.Message}");
        }
    }

    /// <summary>
    /// docs/multi-account.md "Duplicate accounts": detects every account whose AccountIdentity.
    /// StateKey now matches an earlier one in config order (Model/AccountDuplicates.Detect, the
    /// pure grouping rule) and applies the verdict to each AccountRuntime.SetDuplicate. A
    /// duplicate's own poll (and therefore its own SyncIdentityIfChanged) is stopped while it
    /// stays marked, so its identity is refreshed straight from disk here instead -- otherwise a
    /// login change made directly in its config directory could never be noticed, and the
    /// duplicate could never resolve on its own. No-op with 0 or 1 accounts (nothing to compare).
    /// </summary>
    void RefreshDuplicates()
    {
        if (_accounts.Count < 2) return;

        foreach (AccountRuntime account in _accounts)
            if (account.IsDuplicate) account.RefreshIdentityOnly();

        IReadOnlyList<AccountDuplicates.Result> results =
            AccountDuplicates.Detect(_accounts.Select(a => a.Identity?.StateKey).ToList());
        for (int i = 0; i < _accounts.Count; i++)
            _accounts[i].SetDuplicate(results[i].IsDuplicate, results[i].DuplicateOfIndex);
    }

    /// <summary>
    /// Builds the live-mode DisplayAccount list RenderAccounts/CurrentDisplayAccounts consume:
    /// label (single-account-shaped when there is only one), last view, and -- for a confirmed
    /// duplicate (docs/multi-account.md "Duplicate accounts") -- the pre-built explanatory
    /// "other accounts" row plus, for whichever account IT duplicates, a short tooltip-only
    /// suffix (never touching that account's panel-header Label).
    /// </summary>
    List<DisplayAccount> BuildLiveDisplayAccounts()
    {
        bool multi = _accounts.Count > 1;
        var result = new List<DisplayAccount>(_accounts.Count);
        for (int i = 0; i < _accounts.Count; i++)
        {
            AccountRuntime a = _accounts[i];
            OtherAccountRow? dupRow = a.IsDuplicate
                ? PanelText.ComposeDuplicateAccountRow(i, a.DuplicateOfIndex ?? 0, KeptAccountLabel(a.DuplicateOfIndex))
                : null;
            string? suffix = a.IsDuplicate ? null : BuildDuplicateSuffix(i);
            result.Add(new DisplayAccount(a.Slot, multi ? a.Label : null, a.LastView, a.IsDuplicate, dupRow, suffix,
                Title: a.Label,
                Subtitle: PanelText.ComposeAccountSubtitle(a.Label, a.SubscriptionType, a.Identity?.OrganizationName)));
        }
        return result;
    }

    string KeptAccountLabel(int? index) =>
        index is { } k && k >= 0 && k < _accounts.Count ? (_accounts[k].Label ?? $"Konto {k + 1}") : "okänt konto";

    /// <summary>The short tooltip-only note ("first account's tooltip gets a short suffix noting the duplicate") for whichever account at `keptIndex` one or more other accounts currently duplicate. Null when nothing duplicates it.</summary>
    string? BuildDuplicateSuffix(int keptIndex)
    {
        List<int>? dupIndices = null;
        for (int i = 0; i < _accounts.Count; i++)
        {
            if (!_accounts[i].IsDuplicate || _accounts[i].DuplicateOfIndex != keptIndex) continue;
            (dupIndices ??= new List<int>()).Add(i);
        }
        if (dupIndices is null) return null;

        string list = string.Join(", ", dupIndices.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
        return dupIndices.Count == 1 ? $" · dublett: konto {list}" : $" · dubbletter: konto {list}";
    }

    void PushCurrentDemoFrame()
    {
        if (_demoFrames.Count == 0) return;
        DemoFrame frame = _demoFrames[_demoIndex % _demoFrames.Count];
        ApplyFramePanelMode(frame);
        RenderAccounts(frame.Accounts, frame.Mode, DemoMaxIcons);
    }

    /// <summary>Demo frames say whether they show the combined panel.</summary>
    void ApplyFramePanelMode(DemoFrame frame)
    {
        _panelMode = frame.Panel;
        _combinedOpen = frame.Panel != PanelDisplayMode.Single && !frame.DetailOfCombined;
        _markedSlot = frame.Accounts[frame.FocusIndex < frame.Accounts.Count ? frame.FocusIndex : 0].Slot;
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
    /// with whichever account is currently focused plus the "other accounts" rows. Icons and panel
    /// focus are keyed by SLOT ID, so they survive the account list being rebuilt. With no enabled
    /// account at all (live mode only) it shows the single grey "log in" icon instead.
    /// </summary>
    void RenderAccounts(IReadOnlyList<DisplayAccount> current, AccountDisplayMode mode, int maxIcons)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string?> slots = current.Select(a => (string?)a.Slot).ToList();
        _renderedSlots = slots;

        // A duplicate (docs/multi-account.md "Duplicate accounts") is never a valid explicit
        // panel target -- it has no separate icon to have been reached through, and nothing of
        // its own worth pinning the panel to; AccountFocus falls back to the display mode's own
        // default, exactly like a slot that no longer exists.
        int panelIndex = 0;
        if (current.Count > 0)
        {
            (panelIndex, _explicitPanelSlot) = AccountFocus.Resolve(
                slots, current.Select(a => a.IsDuplicate).ToList(), _explicitPanelSlot,
                EffectiveDefaultPanelAccountIndex(current, mode, now) ?? 0);
        }

        // Candidate.Enabled doubles as "eligible for a tray icon" here: a confirmed duplicate is
        // never one, in EITHER display mode (it can never win binding selection either).
        var candidates = current.Select(a => new AccountDisplayPlan.Candidate(!a.IsDuplicate, a.View)).ToList();
        AccountDisplayPlan.TrayPlan plan = AccountDisplayPlan.PlanTray(candidates, mode, Math.Max(1, maxIcons), now);
        var desiredSlots = new HashSet<string>(plan.AccountIcons.Select(i => current[i].Slot), StringComparer.Ordinal);

        foreach (string key in _iconsBySlot.Keys.Where(k => !desiredSlots.Contains(k)).ToList())
        {
            _iconsBySlot[key].Dispose();
            _iconsBySlot.Remove(key);
        }

        if (plan.ZeroAccountsIcon && !_demoMode)
        {
            RenderZeroAccounts();
            return;
        }
        if (_emptyIcon is not null)
        {
            _emptyIcon.Dispose();
            _emptyIcon = null;
        }
        if (current.Count == 0) return; // demo frames always carry at least one account; nothing to draw otherwise

        foreach (int idx in plan.AccountIcons.OrderBy(i => i))
        {
            string slot = current[idx].Slot;
            if (_iconsBySlot.ContainsKey(slot)) continue;
            var handle = new TrayIconHandle { NotifyIcon = { ContextMenuStrip = _trayMenu } };
            handle.NotifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OnIconClicked(slot); };
            handle.NotifyIcon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) _lastRightClickedSlot = slot; };
            _iconsBySlot[slot] = handle;
        }

        bool multi = current.Count > 1;
        foreach ((string slot, TrayIconHandle handle) in _iconsBySlot)
        {
            if (AccountFocus.IndexOfSlot(slots, slot) is not { } idx) continue;
            // The kept account of a duplicate group gets a short tooltip-only suffix (docs/
            // multi-account.md "Duplicate accounts") -- appended here, never to current[idx].Label
            // itself, so the panel header (which reuses that same Label) stays unaffected.
            string? tooltipLabel = multi && current[idx].Label is { } label
                ? label + current[idx].DuplicateSuffix
                : null;
            handle.Slot.Update(current[idx].View, tooltipLabel);
        }

        _shownSlot = current[panelIndex].Slot;
        DisplayAccount shown = current[panelIndex];

        if (_panelMode == PanelDisplayMode.Cards && _combinedOpen)
        {
            RenderCombined(current, now);
            return;
        }
        if (_panelMode == PanelDisplayMode.Full && _combinedOpen)
        {
            RenderFull(current, now);
            return;
        }

        IReadOnlyList<OtherAccountRow> otherRows = multi
            ? current
                .Select((a, i) => (a, i))
                .Where(t => t.i != panelIndex)
                // A duplicate's pre-built DuplicateRow (docs/multi-account.md "Duplicate
                // accounts") replaces the normal verdict row entirely -- there is nothing to poll
                // for it any more, so there is no verdict to show.
                .Select(t => t.a.DuplicateRow ?? PanelText.ComposeOtherAccountRow(t.i, t.a.Label ?? $"Konto {t.i + 1}", t.a.View, now, TimeZoneInfo.Local))
                .ToList()
            : Array.Empty<OtherAccountRow>();

        // The task's reload button: busy exactly while a poll for the SHOWN account is in
        // flight (never all accounts, and never demo mode -- there is no AccountRuntime to
        // poll there). Reads AccountRuntime.IsPolling, the very same flag PollAsync's own
        // concurrency guard is keyed on, so the button can never show "idle" while a poll it
        // just started is actually still running.
        AccountRuntime? shownRuntime = _demoMode ? null : _accounts.FirstOrDefault(a => a.Slot == shown.Slot);
        bool refreshInFlight = shownRuntime?.IsPolling ?? false;

        // docs/statistics.md decision 4 "Proactive advice": never in demo mode (there is no
        // AccountRuntime driving it there) or with no account shown.
        string? adviceLine = shownRuntime?.AdviceLine;

        // The panel title is the account's label (or, in the legacy single-account demo frames, none);
        // the line under it is the plan and organisation.
        _panel.UpdateView(shown.View, _demoMode, shown.Title ?? (multi ? shown.Label ?? $"Konto {panelIndex + 1}" : null), otherRows, refreshInFlight, adviceLine, shown.Subtitle, showBackLink: _panelMode == PanelDisplayMode.Cards, slot: shown.Slot);
        UpdateLoadingTimer(current);
        _panel.SetAnchorIcon(_iconsBySlot.TryGetValue(shown.Slot, out TrayIconHandle? anchorHandle)
            ? anchorHandle.NotifyIcon
            : _iconsBySlot.Values.Select(h => h.NotifyIcon).FirstOrDefault());
    }

    /// <summary>The accounts to show in a multi-account panel, in order: icons left to right, then the rest in config order.</summary>
    List<DisplayAccount> OrderedForMultiPanel(IReadOnlyList<DisplayAccount> current)
    {
        List<DisplayAccount> shownAccounts = current.Where(a => !a.IsDuplicate).ToList();
        IReadOnlyList<string> order = AccountCards.Order(
            _iconsBySlot.Select(kv => (kv.Key, PanelAnchor.TryGetIconRect(kv.Value.NotifyIcon, out Rectangle r) ? (Rectangle?)r : null)).ToList(),
            shownAccounts.Select(a => a.Slot).ToList());
        var bySlot = shownAccounts.ToDictionary(a => a.Slot, StringComparer.Ordinal);
        return order.Select(slot => bySlot[slot]).ToList();
    }

    /// <summary>
    /// Full mode (docs/multi-account.md "Panels"): every enabled account's ordinary full panel, side by side,
    /// in the same order as the cards. Each model carries its own account's refresh state and advice line, so
    /// the panel's own controls act on its own account.
    /// </summary>
    void RenderFull(IReadOnlyList<DisplayAccount> current, DateTimeOffset now)
    {
        var models = new List<PanelModel>();
        foreach (DisplayAccount a in OrderedForMultiPanel(current))
        {
            int position = current.ToList().FindIndex(x => x.Slot == a.Slot);
            AccountRuntime? runtime = _demoMode ? null : _accounts.FirstOrDefault(r => r.Slot == a.Slot);
            models.Add(new PanelModel(a.Slot, a.View, a.Title ?? a.Label ?? $"Konto {position + 1}", a.Subtitle,
                Array.Empty<OtherAccountRow>(), runtime?.IsPolling ?? false, runtime?.AdviceLine, BackLink: false));
        }

        _shownSlot = null; // no single "shown" account: each panel's controls carry their own slot
        _panel.UpdateFull(models, _markedSlot, _demoMode);
        _panel.SetAnchorIcon(_markedSlot is { } m && _iconsBySlot.TryGetValue(m, out TrayIconHandle? h) ? h.NotifyIcon : _iconsBySlot.Values.Select(x => x.NotifyIcon).FirstOrDefault());
        UpdateLoadingTimer(current);
    }

    /// <summary>
    /// The combined panel (docs/multi-account.md "Panels", cards): a card per enabled
    /// account -- icon or not -- ordered like the icons on screen, then the accounts without an icon in
    /// config order. The clicked icon's card is marked.
    /// </summary>
    void RenderCombined(IReadOnlyList<DisplayAccount> current, DateTimeOffset now)
    {
        var cards = new List<AccountCard>();
        foreach (DisplayAccount a in OrderedForMultiPanel(current))
        {
            int position = current.ToList().FindIndex(x => x.Slot == a.Slot);
            cards.Add(AccountCards.Compose(new CardInput(a.Slot, a.Title ?? a.Label ?? $"Konto {position + 1}", a.Subtitle, a.View), now, TimeZoneInfo.Local));
        }

        _shownSlot = null; // no single account is "the shown one": the reload button / per-account actions do not apply here
        _panel.UpdateCombined(cards, _markedSlot, _demoMode);
        _panel.SetAnchorIcon(_markedSlot is { } m && _iconsBySlot.TryGetValue(m, out TrayIconHandle? h) ? h.NotifyIcon : _iconsBySlot.Values.Select(x => x.NotifyIcon).FirstOrDefault());
        UpdateLoadingTimer(current);
    }

    /// <summary>Runs the loading-frame timer exactly while some shown account is loading.</summary>
    void UpdateLoadingTimer(IReadOnlyList<DisplayAccount> current)
    {
        bool anyLoading = !_capturing && current.Any(a => a.View.Loading);
        if (_loadingTimer.Enabled != anyLoading) _loadingTimer.Enabled = anyLoading;
    }

    /// <summary>
    /// docs/multi-account.md "Zero accounts": exactly one grey icon, tooltip "Logga in för att visa
    /// kvoten", the full menu (so "Lägg till konto…" and Exit are reachable), and a left click that
    /// starts the add flow. No panel: there is nothing to show in it.
    /// </summary>
    void RenderZeroAccounts()
    {
        if (_emptyIcon is null)
        {
            _emptyIcon = new TrayIconHandle { NotifyIcon = { ContextMenuStrip = _trayMenu } };
            _emptyIcon.NotifyIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) StartLoginFlow(reloginSlot: null); };
        }
        _emptyIcon.Slot.Update(QuotaView.Initial with { NeedsLogin = true }, tooltipOverride: LoginText.ZeroAccountsTooltip);
        _loadingTimer.Enabled = false; // nothing loads with no account

        _shownSlot = null;
        _explicitPanelSlot = null;
        _panel.HidePanel();
        _panel.SetAnchorIcon(_emptyIcon.NotifyIcon);
    }

    /// <summary>
    /// The task's reload button: forces an immediate poll for whichever account the panel is
    /// currently showing (never all accounts). No-op in demo mode (there is no AccountRuntime),
    /// with no account currently shown, or while that account already has a poll in flight --
    /// AccountRuntime.RequestImmediateRefresh/PollAsync's own _polling guard makes the last case
    /// safe on its own, but checking IsPolling here too avoids even scheduling the redundant
    /// call. Re-ticks immediately afterward so the button's busy state shows up without waiting
    /// up to 1s for the next regular eval tick (the same pattern FocusSlot already uses).
    /// </summary>
    void OnRefreshRequested(string? requestedSlot)
    {
        if (_demoMode || _flows is { IsMutating: true }) return;
        // The panel says which account's refresh button was clicked (full mode shows several panels).
        if ((requestedSlot ?? _shownSlot) is not { } slot || _accounts.FirstOrDefault(a => a.Slot == slot) is not { } account) return;

        // Defensive (docs/multi-account.md "Duplicate accounts"): _shownSlot should never
        // point at a duplicate -- RenderAccounts resets _explicitPanelSlot away from one
        // -- but a duplicate account can never be usefully refreshed (AccountRuntime.PollAsync's
        // own IsDuplicate guard would just no-op it anyway), so skip even scheduling the call.
        if (account.IsDuplicate) return;
        if (!account.IsPolling) _ = account.RequestImmediateRefresh();
        Tick();
    }

    /// <summary>docs/multi-account.md "Display": in binding mode the default focus is the binding account itself; otherwise the first configured account. A confirmed duplicate (Model/AccountDuplicates.cs) is never eligible in either mode -- see RenderAccounts' own candidates list for why config order's index 0 itself can never be one.</summary>
    static int? EffectiveDefaultPanelAccountIndex(IReadOnlyList<DisplayAccount> current, AccountDisplayMode mode, DateTimeOffset now)
    {
        if (current.Count == 0) return null;
        if (mode != AccountDisplayMode.Binding) return 0;

        var candidates = current.Select(a => new AccountDisplayPlan.Candidate(!a.IsDuplicate, a.View)).ToList();
        return AccountDisplayPlan.SelectBinding(candidates, now) ?? 0;
    }

    /// <summary>
    /// docs/multi-account.md "Panel": clicking a tray icon opens the panel on that icon's
    /// account; clicking an "other accounts" row switches to that account. If the panel is
    /// already open on this exact account, this click closes it instead (the existing
    /// single-icon toggle behaviour), so a click doesn't reopen what it just closed. The account
    /// is addressed by slot id, so a click can never land on a different account after the set
    /// has been rebuilt.
    ///
    /// docs/multi-account.md "Duplicate accounts": a duplicate's own "other accounts" row is the
    /// explanatory message, not a normal navigation target -- clicking it redirects to the
    /// account it duplicates instead of focusing a stopped, unpolled account that has nothing to
    /// show.
    /// </summary>
    void FocusSlot(string slot)
    {
        if (_accounts.FirstOrDefault(a => a.Slot == slot) is { IsDuplicate: true, DuplicateOfIndex: { } keptIndex }
            && keptIndex >= 0 && keptIndex < _accounts.Count)
        {
            slot = _accounts[keptIndex].Slot;
        }

        if (_panel.Visible && _explicitPanelSlot == slot) { _panel.Toggle(); return; }
        _explicitPanelSlot = slot;
        if (!_panel.Visible) _panel.ShowPanel();
        Tick();
    }

    /// <summary>The panel reports a click on row `rowIndex` of the list it was last given; the slot recorded at that render says which account that row is.</summary>
    void FocusRenderedRow(int rowIndex)
    {
        if (rowIndex >= 0 && rowIndex < _renderedSlots.Count && _renderedSlots[rowIndex] is { } slot) FocusSlot(slot);
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
        // isDuplicate (docs/multi-account.md "Duplicate accounts"): a true duplicate never needs
        // a distinguishing label -- it isn't a second account that happens to share a label with
        // another, it's the SAME login, and its row is replaced entirely by
        // Ui/PanelText.ComposeDuplicateAccountRow, so whatever label it comes back with here is
        // never shown anyway.
        var isDuplicate = _accounts.Select(a => a.IsDuplicate).ToList();
        IReadOnlyList<string> labels = AccountLabel.Disambiguate(inputs, isDuplicate);
        for (int i = 0; i < _accounts.Count; i++) _accounts[i].ApplyDisambiguatedLabel(labels[i]);
    }

    /// <summary>The full --demo / --capture-states cycle: every legacy single-account DemoQuotaSource state, then the multi-account frames (docs/multi-account.md, the task's "Demo mode" item).</summary>
    static IReadOnlyList<DemoFrame> BuildDemoFrames(DateTimeOffset utcNow)
    {
        var frames = new List<DemoFrame>();
        foreach (DemoQuotaSource.DemoState s in DemoQuotaSource.Build(utcNow))
            frames.Add(new DemoFrame(s.Key, AccountDisplayMode.PerAccount, new[] { new DisplayAccount("demo-0", null, s.View, Title: "Max", Subtitle: "personlig organisation") }, FocusIndex: 0));

        foreach (MultiAccountDemoSource.MultiState m in MultiAccountDemoSource.Build(utcNow))
        {
            IReadOnlyList<DisplayAccount> accounts = m.Accounts.Select((a, i) => new DisplayAccount($"demo-{i}", a.Label, a.View, Title: a.Label, Subtitle: a.Subtitle)).ToList();
            frames.Add(new DemoFrame(m.Key, m.Mode, accounts, m.FocusIndex));
        }

        // The combined panel (docs/multi-account.md "All accounts in one panel") for 1, 2 and 3 accounts.
        MultiAccountDemoSource.MultiState three = MultiAccountDemoSource.Build(utcNow).First(s => s.Key == "multi_per_account");
        IReadOnlyList<DisplayAccount> threeAccounts = three.Accounts.Select((a, i) => new DisplayAccount($"demo-{i}", a.Label, a.View, Title: a.Label, Subtitle: a.Subtitle)).ToList();
        frames.Add(new DemoFrame("combined_1", AccountDisplayMode.PerAccount, threeAccounts.Take(1).ToList(), FocusIndex: 0, Panel: PanelDisplayMode.Cards));
        frames.Add(new DemoFrame("combined_2", AccountDisplayMode.PerAccount, threeAccounts.Take(2).ToList(), FocusIndex: 1, Panel: PanelDisplayMode.Cards));
        frames.Add(new DemoFrame("combined_3", AccountDisplayMode.PerAccount, threeAccounts, FocusIndex: 1, Panel: PanelDisplayMode.Cards));
        // ... with one account in each unusual state, and the account without an icon (binding shows one icon).
        MultiAccountDemoSource.MultiState states = MultiAccountDemoSource.Build(utcNow).First(s => s.Key == "multi_needs_login");
        MultiAccountDemoSource.MultiState loading = MultiAccountDemoSource.Build(utcNow).First(s => s.Key == "multi_loading");
        var mixed = new List<DisplayAccount>
        {
            threeAccounts[0], threeAccounts[1], threeAccounts[2],
            new("demo-3", states.Accounts[1].Label, states.Accounts[1].View, Title: "Konto utan inloggning", Subtitle: states.Accounts[1].Subtitle),
            new("demo-4", loading.Accounts[1].Label, loading.Accounts[1].View, Title: "Nytt konto", Subtitle: loading.Accounts[1].Subtitle),
        };
        frames.Add(new DemoFrame("combined_detail", AccountDisplayMode.PerAccount, threeAccounts, FocusIndex: 1, Panel: PanelDisplayMode.Cards, DetailOfCombined: true));
        frames.Add(new DemoFrame("combined_5_binding", AccountDisplayMode.Binding, mixed, FocusIndex: 2, Panel: PanelDisplayMode.Cards));

        // Full panels side by side (docs/multi-account.md "Panels"): two and three accounts (one marked, one
        // hovered), and a row that does not fit the work area width.
        frames.Add(new DemoFrame("full_2", AccountDisplayMode.PerAccount, threeAccounts.Take(2).ToList(), FocusIndex: 0, Panel: PanelDisplayMode.Full, HoverIndex: 1));
        frames.Add(new DemoFrame("full_3", AccountDisplayMode.PerAccount, threeAccounts, FocusIndex: 1, Panel: PanelDisplayMode.Full, HoverIndex: 2));
        var wide = new List<DisplayAccount>();
        for (int i = 0; i < 8; i++)
        {
            DisplayAccount source = mixed[i % mixed.Count];
            wide.Add(source with { Slot = $"demo-w{i}" });
        }
        frames.Add(new DemoFrame("full_8_wide", AccountDisplayMode.PerAccount, wide, FocusIndex: 6, Panel: PanelDisplayMode.Full));

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
        _capturing = true;
        _loadingTimer.Stop();
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

            _explicitPanelSlot = frames[i].Accounts[frames[i].FocusIndex < frames[i].Accounts.Count ? frames[i].FocusIndex : 0].Slot;
            ApplyFramePanelMode(frames[i]);
            RenderAccounts(frames[i].Accounts, frames[i].Mode, DemoMaxIcons);
            _panel.SetHoverPanelForDemo(frames[i].HoverIndex >= 0 ? frames[i].Accounts[frames[i].HoverIndex].Slot : null);
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

    /// <summary>
    /// The task's demo/capture item: renders the statistics window to in-memory bitmaps via
    /// DrawToBitmap (never CopyFromScreen -- a locked/inactive session cannot be screen-captured
    /// at all, which is exactly what broke the earlier attempt this task's own notes describe).
    /// Demo mode captures BOTH required scenarios (a fresh install, mostly in the learning state,
    /// and a full year, fully unlocked) as "learning.png"/"unlocked.png"; live mode captures the
    /// real window for the real account(s) as "real.png" -- the honest result with however little
    /// history actually exists on this machine. Exits the app when done, mirroring
    /// RunAutomatedCapture's own capture-then-exit contract.
    /// </summary>
    public void RunStatisticsCapture(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (!_demoMode) WaitForFirstPollsToLand();
            using StatisticsForm form = BuildStatisticsForm();
            if (_demoMode)
            {
                form.SelectAccount(0); // fresh install -- default "2 veckor" preset is exactly the scenario's own story
                SaveStatisticsCapture(form, Path.Combine(dir, "learning.png"));
                form.SelectAccount(1); // a full year of data -- "År" is the period that actually shows everything unlocked
                form.SelectPeriod(StatisticsPeriod.Year);
                SaveStatisticsCapture(form, Path.Combine(dir, "unlocked.png"));
            }
            else
            {
                SaveStatisticsCapture(form, Path.Combine(dir, "real.png"));
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"RunStatisticsCapture failed: {ex.Message}");
        }
        ExitApp();
    }

    static void SaveStatisticsCapture(StatisticsForm form, string path)
    {
        using Bitmap bmp = form.CaptureFullContent();
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Task item 4's other half of the fix: RunStatisticsCapture runs BEFORE Application.Run ever
    /// pumps a message (this class's own RunStatisticsCapture doc comment explains why --
    /// DrawToBitmap doesn't need a message loop). But AccountRuntime.PollAsync's completion DOES:
    /// it lands on the UI thread via _uiContext.Post (a queued window message), which only ever
    /// runs while something pumps the queue. Without this wait, every capture ran before ANY
    /// account's first poll could apply its SubscriptionType, so AccountLabel.Resolve had no
    /// plan tier to fall back on and produced the email-local-part label the task's real
    /// screenshot caught -- while the tray/panel, which always run under a real Application.Run
    /// pump, never hit this. Pumps with Application.DoEvents (never Application.Run -- that would
    /// start a second, nested message loop this method's caller does not expect) for up to the
    /// same ~3s the --show-panel path already waits (README: first get_usage takes ~1.1-1.6s),
    /// then applies the whole-set disambiguation pass so a multi-account capture also matches the
    /// tray's own disambiguated labels, not just each account's un-disambiguated base label. Best
    /// effort: an account whose channel never comes up within the wait still gets captured (its
    /// label falls back to on-disk data instead -- see StatisticsDataLoader.ResolveAccountLabel).
    /// </summary>
    void WaitForFirstPollsToLand()
    {
        if (_accounts.Count == 0) return;
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && _accounts.Any(a => a.SubscriptionType == null))
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
        }
        RefreshDisambiguatedLabels();
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
        try { _shutdownCts.Cancel(); } catch { /* best effort */ } // stops waiting for a login console; the console itself is left running (it may own the user's browser)
        try { _exitWait?.Unregister(null); _exitEvent?.Dispose(); } catch { /* best effort */ }

        // Panel hooks removed and the panel itself torn down FIRST (Codex review High #5):
        // HidePanel() stops the DismissWatcher's global hooks immediately, and Dispose()
        // finishes the rest (tick timer, fonts) before anything below can block.
        try { _panel.HidePanel(); } catch (Exception ex) { SafeLog.Warn($"HidePanel during shutdown threw: {ex.Message}"); }
        try { _panel.Dispose(); } catch (Exception ex) { SafeLog.Warn($"Panel.Dispose during shutdown threw: {ex.Message}"); }
        try { _statisticsForm?.Dispose(); } catch (Exception ex) { SafeLog.Warn($"StatisticsForm.Dispose during shutdown threw: {ex.Message}"); }

        try { _evalTimer.Stop(); _evalTimer.Dispose(); } catch { /* best effort */ }
        try { _loadingTimer.Stop(); _loadingTimer.Dispose(); } catch { /* best effort */ }
        try { _demoTimer?.Stop(); _demoTimer?.Dispose(); } catch { /* best effort */ }

        foreach (TrayIconHandle handle in _iconsBySlot.Values.Concat(_emptyIcon is null ? Array.Empty<TrayIconHandle>() : new[] { _emptyIcon }))
        {
            try { handle.Dispose(); } catch (Exception ex) { SafeLog.Warn($"TrayIconHandle.Dispose during shutdown threw: {ex.Message}"); }
        }
        _iconsBySlot.Clear();
        _emptyIcon = null;

        // Terminate every account's child and flush its log writer off the UI thread, bounded
        // (Codex review High #5): closing a redirected pipe/flushing the CSV can block if the
        // child is not reading or the disk is slow, and that must never hang the thread this
        // runs on across every exit route. One account's cleanup hanging must not stop another's:
        // all accounts are stopped in PARALLEL (graceful: stdin closed, bounded wait for the child to
        // exit, then a tree kill), so the whole shutdown costs one account's time, not the sum.
        AccountRuntime[] toStop = _accounts.ToArray();
        RunBoundedOnBackgroundThread(async () =>
        {
            if (!await StopAccountsAsync(toStop, ShutdownDeadline - TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                foreach (AccountRuntime account in toStop) account.KillChildren(); // whatever did not stop in time
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
