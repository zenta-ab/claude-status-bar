using ClaudeStatusBar.Config;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Everything one account owns, end to end (docs/multi-account.md): its own claude.exe child
/// (via ChildProcessSpec.ForSlot + ClaudeCliChannelSupervisor), its own identity-keyed state
/// (AccountKeyedState: QuotaModel + per-account DiskLogSink), its own poll timer, and its last
/// evaluated QuotaView, identity and display label. StatusBarApplicationContext owns a list of
/// these and drives each independently -- see that type's doc comment for the wiring rules
/// (WarmStart before the first Ingest, Ingest/IngestFailure per poll, Evaluate on the 1s tick,
/// NextPollDelay after every poll) reproduced here per-account instead of once globally.
///
/// One account failing, logging out, or its config directory going missing must never affect
/// any other account: every failure this type can hit (channel launch, get_usage, identity
/// read) is caught and reported through this account's own QuotaModel.IngestFailure / LastView,
/// exactly like the single-account app already does for transport failures.
///
/// Identity guard (the live bug this exists to fix, see AccountKeyedState's doc comment for the
/// mechanics): SyncIdentityIfChanged re-reads &lt;configDir&gt;\.claude.json on every poll cycle. A
/// CONFIRMED different AccountIdentity.StateKey (accountUuid+organizationUuid -- see that
/// type's doc comment for why accountUuid alone is not unique per plan) rebuilds this account's
/// state; an unreadable/missing file is treated as "no new information" (never as an implicit
/// logout) so a transient read race while the CLI is mid-write can never tear down a perfectly
/// good in-memory model.
///
/// NeedsLogin (docs/multi-account.md "NeedsLogin"): a child whose login has expired or is missing
/// still answers get_usage, with the "not logged in" shape (UsageParser.Parse). The first such
/// answer restarts the child once -- a stale process is the usual cause, a fresh one is the cheap
/// test -- and only if the fresh child's first answer is the same shape does the account become
/// NeedsLogin. Any successful reading clears it. `auth status` is never consulted for this.
///
/// Duplicate accounts (docs/multi-account.md "Duplicate accounts"): two configured accounts can
/// resolve to the same StateKey -- for example a slot recovered after accounts.json was lost whose
/// login landed in an organisation another slot already tracks. StatusBarApplicationContext.RefreshDuplicates
/// (Model/AccountDuplicates.cs) detects this every eval tick and calls SetDuplicate on whichever
/// account is not first in config order: it stops that account's poll timer and child (never its
/// on-disk state) so there is no redundant claude.exe and no duplicate tray icon, while
/// RefreshIdentityOnly keeps its identity current from disk alone so the duplicate can still
/// resolve on its own once the identities diverge again.
/// </summary>
public sealed class AccountRuntime : IAsyncDisposable
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a restarted child gets to come up before the confirming poll is attempted anyway.</summary>
    static readonly TimeSpan RestartChannelWait = TimeSpan.FromSeconds(3);

    readonly string _slot;
    readonly AccountEntry _config;
    readonly int _index;
    readonly SynchronizationContext _uiContext;
    readonly string _configDir;
    readonly string _baseLogDir;
    readonly ChildProcessSpec _spec;
    readonly System.Windows.Forms.Timer _pollTimer;
    readonly LoadingTracker _loading;
    readonly object _swapLock = new(); // guards _channelSupervisor replacement against a concurrent stop

    AccountKeyedState _state;
    ClaudeCliChannelSupervisor _channelSupervisor;
    bool _polling;
    volatile bool _shuttingDown;
    volatile bool _restarting;
    bool _restartedForNotLoggedIn; // the one restart has been spent; the next not-logged-in answer is final

    /// <summary>The slot id (the folder name under the accounts root) -- how this account is addressed everywhere outside this list.</summary>
    public string Slot => _slot;

    /// <summary>True once a fresh child has confirmed that this account has no usable login (see the type doc comment). Shown as the grey "!" ring with a re-login prompt.</summary>
    public bool NeedsLogin { get; private set; }

    public AccountIdentity? Identity { get; private set; }
    public string? SubscriptionType { get; private set; }
    public string Label { get; private set; }
    /// <summary>Starts out "Hämtar kvoten…" (Model/LoadingTracker): not answered yet is not an error.</summary>
    public QuotaView LastView { get; private set; } = QuotaView.Initial with { Loading = true };
    public bool Enabled => _config.Enabled;
    public string? OverrideLabel => _config.Label;
    public string? IdentityKey => _state.IdentityKey;
    public string CurrentLogDir => _state.CurrentLogDir;

    /// <summary>docs/multi-account.md "Duplicate accounts": true once StatusBarApplicationContext.RefreshDuplicates (Model/AccountDuplicates.cs) has confirmed this account resolves to the same AccountIdentity.StateKey as an earlier one in config order. See SetDuplicate for what changing this does.</summary>
    public bool IsDuplicate { get; private set; }

    /// <summary>The earlier account's config-order index this one duplicates, or null when IsDuplicate is false.</summary>
    public int? DuplicateOfIndex { get; private set; }

    /// <summary>
    /// docs/statistics.md decision 4 "Proactive advice": the latest line StatusBarApplicationContext
    /// should show in the panel for this account, or null when nothing has newly cleared its
    /// threshold. Latches -- keeps returning the same line on every render until a NEW closed
    /// cycle produces a genuinely different one (Model/StatisticsAdvice.Decide's own signature
    /// check) -- it does not expire on its own; "shown once" means the underlying pattern is
    /// only ever announced once (persisted in StatisticsAdviceStore), not that the panel line
    /// vanishes after one frame.
    /// </summary>
    public string? AdviceLine { get; private set; }

    /// <summary>
    /// True from the moment a poll (timer-driven or a forced one, see RequestImmediateRefresh)
    /// starts until ApplyResult finishes applying its outcome -- what the panel's reload button
    /// (the task's "Reload button in the panel") shows as busy/disabled. Backed by the same
    /// _polling flag PollAsync already used to guard against overlapping polls for this account,
    /// so there is exactly one source of truth for "is a poll for this account in flight".
    /// </summary>
    public bool IsPolling => _polling;

    /// <param name="slot">The slot id: the folder under the accounts root that holds this account's login.</param>
    /// <param name="config">This account's accounts.json entry.</param>
    /// <param name="index">This account's position in the configured list, for the "Konto N" label fallback.</param>
    /// <param name="uiContext">The UI SynchronizationContext poll completions are marshalled back onto, exactly like StatusBarApplicationContext's own polling used to.</param>
    /// <param name="baseLogDirOverride">Test-only: replaces %LOCALAPPDATA%\ClaudeStatusBar\logs.</param>
    /// <param name="specOverride">Test-only: points the channel at tests/FakeClaudeChild instead of the real claude.exe.</param>
    /// <param name="configDirOverride">Test-only: replaces the resolved config directory identity is read from.</param>
    /// <param name="loadingTimeout">Test-only: replaces the 30 s the loading state lasts without any result.</param>
    /// <param name="slots">The slot store whose path guard resolves the slot (default: the app's own accounts root). Throws if the guard refuses the slot.</param>
    public AccountRuntime(
        string slot,
        AccountEntry config,
        int index,
        SynchronizationContext uiContext,
        string? baseLogDirOverride = null,
        ChildProcessSpec? specOverride = null,
        string? configDirOverride = null,
        SlotStore? slots = null,
        TimeSpan? loadingTimeout = null)
    {
        _slot = slot;
        _config = config;
        _index = index;
        _uiContext = uiContext;
        slots ??= SlotStore.Default;
        _configDir = configDirOverride ?? slots.ConfigDirOf(slot);
        _baseLogDir = baseLogDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
        _spec = specOverride ?? ChildProcessSpec.ForSlot(slots, slot);

        Identity = AccountIdentity.ReadFrom(_configDir);
        _state = new AccountKeyedState(_baseLogDir, Identity?.StateKey);
        _channelSupervisor = new ClaudeCliChannelSupervisor(_spec, _state.LogSink);

        Label = AccountLabel.Resolve(new AccountLabelInput(OverrideLabel, Identity, SubscriptionType), index);

        _loading = new LoadingTracker(Environment.TickCount64, loadingTimeout);
        _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _pollTimer.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>Launches the child and starts polling. Does not wait for the first poll to land.</summary>
    public void Start()
    {
        _loading.Restart(Environment.TickCount64);
        _channelSupervisor.Start();
        _pollTimer.Start();
        _ = PollAsync();
    }

    /// <summary>The 1s UI tick for this account: cheap, safe every second, per QuotaModel.Evaluate's own contract.</summary>
    public void Evaluate(DateTimeOffset utcNow, long monoMs) => LastView = WithLoginState(_state.Model.Evaluate(utcNow, monoMs), monoMs);

    /// <summary>
    /// NeedsLogin is overlaid on the model's view: grey ring (Unknown) plus the flag the panel and
    /// tooltip key on. So is Loading (no result yet, 30 s not passed): the same Unknown view, flagged so
    /// it reads "Hämtar kvoten…" rather than as an error. NeedsLogin wins -- it only exists after results.
    /// </summary>
    QuotaView WithLoginState(QuotaView view, long monoMs)
    {
        if (NeedsLogin) return view with { Freshness = Freshness.Unknown, NeedsLogin = true };
        if (_loading.IsLoading(monoMs)) return view with { Freshness = Freshness.Unknown, Loading = true };
        return view;
    }

    /// <summary>
    /// The panel's reload button (docs/panel-v2.md "Header"): an on-demand poll for this
    /// account only, identical to a normal timer-driven one -- no special-cased bookkeeping,
    /// so it can never corrupt the freshness deadlines QuotaModel.Ingest freezes on acceptance;
    /// it is simply an earlier poll than the current interval would otherwise have produced. A
    /// plain alias for PollAsync() so call sites read as "the user asked for this now" rather
    /// than "the timer fired", while sharing that method's _polling guard against a second
    /// forced click (or the regular timer) starting a concurrent poll for the same account.
    /// </summary>
    public Task RequestImmediateRefresh() => PollAsync();

    /// <summary>
    /// One full poll cycle: re-sync identity, poll the channel, apply the result. Public (not
    /// just timer-driven) so tests can step it deterministically instead of racing a real timer.
    /// </summary>
    public async Task PollAsync()
    {
        // IsDuplicate (docs/multi-account.md "Duplicate accounts"): SetDuplicate(true) already
        // stops the poll timer, but this guard is the actual, unconditional enforcement of "no
        // poll child for a confirmed duplicate" -- it must hold even against a stray direct call
        // (e.g. a reload-button click racing the exact tick that just marked this account a
        // duplicate), never just against the timer being stopped.
        if (_polling || _shuttingDown || IsDuplicate || _restarting) return;
        _polling = true;
        try
        {
            SyncIdentityIfChanged();

            UsageReadResult result = await _channelSupervisor.GetUsageAsync(RequestTimeout).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                if (_shuttingDown) return;
                ApplyResult(result);
            }, null);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Installs or clears this account's duplicate status (docs/multi-account.md "Duplicate
    /// accounts") -- the only caller is StatusBarApplicationContext.RefreshDuplicates, applying
    /// Model/AccountDuplicates.Detect's verdict for this account's config-order position every
    /// eval tick. Idempotent on the IsDuplicate transition itself (repeated calls with the same
    /// flag only update DuplicateOfIndex, e.g. because an earlier account in the group was
    /// disabled and the group's "first" shifted -- they never re-stop or re-start anything).
    ///
    /// Becoming a duplicate stops the poll timer and tears down the channel supervisor -- "no
    /// poll child -- do not keep a redundant claude.exe running" -- but never touches
    /// AccountKeyedState/DiskLogSink: this identity's on-disk history is left exactly as it is,
    /// ready for the moment this resolves (never merge or delete state on disk). Resolving
    /// (ceasing to be a duplicate) restarts exactly like Start() would for a brand new account,
    /// including an immediate poll rather than waiting for the timer's first tick.
    /// </summary>
    public void SetDuplicate(bool isDuplicate, int? duplicateOfIndex = null)
    {
        bool wasDuplicate = IsDuplicate;
        IsDuplicate = isDuplicate;
        DuplicateOfIndex = isDuplicate ? duplicateOfIndex : null;

        if (isDuplicate && !wasDuplicate)
        {
            _pollTimer.Stop();
            _ = ReplaceSupervisor(start: false)?.DisposeAsync(); // the replacement is an idle placeholder -- never Started while duplicate
            SafeLog.Info($"account {_slot}: marked duplicate of index {duplicateOfIndex} -- child stopped");
        }
        else if (!isDuplicate && wasDuplicate && !_shuttingDown)
        {
            _channelSupervisor.Start();
            _pollTimer.Start();
            _ = PollAsync();
            SafeLog.Info($"account {_slot}: duplicate resolved -- resuming polling");
        }
    }

    /// <summary>
    /// Cheap identity-only refresh, no channel/poll timer involved: reads &lt;configDir&gt;\
    /// .claude.json directly (the same file AccountIdentity.ReadFrom always reads), so a
    /// duplicate account's identity keeps advancing even though SetDuplicate(true) has stopped
    /// its own polling -- otherwise a duplicate could never resolve on its own once the user logs
    /// a different account into its directory (docs/multi-account.md's "resolves on its own"
    /// requirement). StatusBarApplicationContext.RefreshDuplicates calls this every eval tick for
    /// every currently-duplicate account; a plain file read is cheap enough for 1Hz. An
    /// unreadable/missing file is "no new information", exactly like SyncIdentityIfChanged.
    /// </summary>
    public void RefreshIdentityOnly()
    {
        AccountIdentity? identity = AccountIdentity.ReadFrom(_configDir);
        if (identity is null) return;
        Identity = identity;
        RefreshLabel();
    }

    void SyncIdentityIfChanged()
    {
        AccountIdentity? identity = AccountIdentity.ReadFrom(_configDir);
        if (identity is not null)
        {
            if (_state.SyncIdentity(identity.StateKey))
            {
                // ClaudeCliChannel bakes its DiskLogSink in at construction (raw + CSV writes
                // both go through it) -- the channel must be recreated against the new one so
                // every poll from here on logs under the new account's directory, never the
                // old one's. The old child is torn down in the background; a poll or two
                // failing right after a genuine account switch is an acceptable, self-healing
                // blip, not a correctness problem.
                _ = ReplaceSupervisor(start: true)?.DisposeAsync();
                SafeLog.Info($"account {_slot}: identity changed, rebuilt state (key={AccountIdentity.KeyPrefixOf(_state.IdentityKey)})");
            }
            Identity = identity;
            RefreshLabel();
        }
        // else: an unreadable/missing .claude.json is "no new information", never an implicit
        // logout -- keep the last known Identity and state exactly as they were.
    }

    /// <summary>
    /// Swaps in a new (idle or started) supervisor and hands back the old one for the caller to
    /// stop. Null -- and nothing swapped -- once this runtime is shutting down, so a late swap
    /// can never start a child nobody will stop.
    /// </summary>
    ClaudeCliChannelSupervisor? ReplaceSupervisor(bool start)
    {
        lock (_swapLock)
        {
            if (_shuttingDown) return null;
            ClaudeCliChannelSupervisor old = _channelSupervisor;
            _channelSupervisor = new ClaudeCliChannelSupervisor(_spec, _state.LogSink);
            if (start) _channelSupervisor.Start();
            return old;
        }
    }

    void ApplyResult(UsageReadResult result)
    {
        UsageSnapshot? snapshot = result.Snapshot;
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        long monoMs = Environment.TickCount64;

        if (result.NotLoggedIn)
        {
            _state.Model.IngestFailure(result.Error ?? UsageParser.NotLoggedInError, utcNow, monoMs);
            if (!_restartedForNotLoggedIn)
            {
                // First such answer: could be a stale process. Restart once, then ask again.
                _restartedForNotLoggedIn = true;
                SafeLog.Info($"account {_slot}: child answered not-logged-in -- restarting it once to confirm");
                _ = RestartChildAsync(); // still loading: the confirming answer is the first RESULT
            }
            else if (!NeedsLogin)
            {
                _loading.End();
                NeedsLogin = true;
                SafeLog.Info($"account {_slot}: a fresh child also answered not-logged-in -- needs login");
            }
        }
        else if (snapshot is null)
        {
            _loading.End(); // the first failure ends loading: from here on it is a real failure
            _state.Model.IngestFailure(result.Error ?? "unknown error", utcNow, monoMs);
        }
        else
        {
            _loading.End();
            if (NeedsLogin) SafeLog.Info($"account {_slot}: reading succeeded -- login restored");
            NeedsLogin = false;
            _restartedForNotLoggedIn = false;

            if (snapshot.SubscriptionType != null) SubscriptionType = snapshot.SubscriptionType;
            RefreshLabel();

            // WarmStart must run BEFORE Ingest for this same snapshot, and only once per
            // identity-keyed state -- see QuotaModel.WarmStart's doc comment for why the order
            // matters. A rebuild (AccountKeyedState.SyncIdentity) resets WarmStarted, so a newly
            // adopted account gets its own warm start from its own logs\<uuid> directory.
            if (!_state.WarmStarted)
            {
                _state.Model.WarmStart(snapshot, utcNow, logDirOverride: _state.CurrentLogDir);
                _state.MarkWarmStarted();
            }
            _state.Model.Ingest(snapshot, utcNow, monoMs);
            ArchiveClosedWindowsAndHours();
        }

        TimeSpan next = _state.Model.NextPollDelay(utcNow);
        _pollTimer.Interval = Math.Max(1, (int)Math.Round(next.TotalMilliseconds));

        LastView = WithLoginState(_state.Model.Evaluate(utcNow, monoMs), monoMs);
        LogCommittedState();
    }

    /// <summary>
    /// Stops this account's child and starts a fresh one, then polls it right away so the
    /// not-logged-in confirmation does not wait out a failure backoff. Polls are held off meanwhile.
    /// </summary>
    async Task RestartChildAsync()
    {
        _restarting = true;
        try
        {
            ClaudeCliChannelSupervisor? old = ReplaceSupervisor(start: false);
            if (old is null) return;
            try { await old.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { SafeLog.Warn($"account {_slot}: stopping the child for a restart threw: {ex.Message}"); }

            ClaudeCliChannelSupervisor fresh = _channelSupervisor;
            fresh.Start();

            DateTime deadline = DateTime.UtcNow + RestartChannelWait;
            while (!_shuttingDown && fresh.CurrentProcessId is null && DateTime.UtcNow < deadline)
                await Task.Delay(50).ConfigureAwait(false);
        }
        finally
        {
            _restarting = false;
        }

        if (!_shuttingDown) _ = PollAsync();
    }

    /// <summary>
    /// docs/statistics.md: drains any windows/clock hours QuotaModel just closed and enqueues
    /// them to this account's own cycles.csv/hourly-YYYY.csv, via the same per-account DiskLogSink
    /// (and its own async, non-blocking, drop-and-log queue) window-shape.csv already goes
    /// through. account_key mirrors the directory this account's own state already lives under
    /// (AccountKeyedState.ResolveDir) so a reader never has to cross-reference the two.
    /// </summary>
    void ArchiveClosedWindowsAndHours()
    {
        string accountKey = _state.IdentityKey ?? "_pending";

        IReadOnlyList<CycleClosed> closedCycles = _state.Model.TakeClosedCycles();
        foreach (CycleClosed closed in closedCycles)
        {
            _state.LogSink.EnqueueCycle(new CycleArchiveCsv.Row(
                accountKey, closed.Kind, closed.StartedUtc, closed.ResetUtc,
                closed.PeakPct, closed.FinalPct, closed.HitCeiling, closed.BlockedMinutes,
                closed.CoveredMinutes, closed.PlanTier, closed.WarnedDryEarly,
                closed.PredictedPeakPct, closed.PredictedAtUtc,
                closed.CeilingReachedAtUtc, closed.CeilingReachedCensored));
        }

        foreach (HourClosed closed in _state.Model.TakeClosedHours())
        {
            _state.LogSink.EnqueueHourly(new HourlyRollupCsv.Row(
                accountKey, closed.Kind, closed.HourStartUtc, closed.ConsumedPct,
                closed.Samples, closed.CoveredMinutes));
        }

        // docs/statistics.md decision 4: recomputing the two recommendations is only worth
        // doing right when a new cycle just closed (they read cycles.csv/hourly-*.csv fully off
        // disk) -- a session/weekly rollover is rare (at most a handful a day), so this never
        // runs on the 1Hz eval tick or even every poll, only when there is genuinely new data
        // that could change either recommendation's answer.
        if (closedCycles.Count > 0) RecomputeAdvice(accountKey);
    }

    /// <summary>
    /// Reads this account's own archive back (the same on-disk files EnqueueCycle/EnqueueHourly
    /// just wrote through the DiskLogSink queue -- a small race where this read runs just before
    /// those writes land is harmless: it simply means the newest cycle isn't reflected until the
    /// NEXT close, never a wrong/crashing read), runs it through the real engine in local time,
    /// and lets Model/StatisticsAdvice decide whether either recommendation is newly worth
    /// announcing. Best-effort: a failure here must never affect polling or archiving.
    /// </summary>
    void RecomputeAdvice(string accountKey)
    {
        try
        {
            StatisticsEngine.Result result = StatisticsEngine.ComputeForAccount(_state.CurrentLogDir, accountKey, TimeZoneInfo.Local);
            StatisticsAdvice.LastShown last = StatisticsAdviceStore.LoadAll().GetValueOrDefault(accountKey, StatisticsAdvice.LastShown.Empty);
            (StatisticsAdvice.Advice? advice, StatisticsAdvice.LastShown next) =
                StatisticsAdvice.Decide(result.CeilingRecommendation, result.WeeklyBudgetRecommendation, last);
            if (advice is { } a)
            {
                AdviceLine = a.Line;
                StatisticsAdviceStore.SaveOne(accountKey, next);
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"account {_slot}: proactive-advice recompute failed: {ex.Message}");
        }
    }

    /// <summary>Same one-line-per-poll diagnostic the single-account app already wrote, now per account (best-effort, never allowed to affect the poll path).</summary>
    void LogCommittedState()
    {
        _state.LogSink.EnqueueRaw("state",
            $"session={LastView.Session.State} sessionReason={LastView.Session.MeasuringReason ?? "-"} " +
            $"weekly={LastView.Weekly.State} weeklyReason={LastView.Weekly.MeasuringReason ?? "-"} freshness={LastView.Freshness}");
    }

    void RefreshLabel() => Label = AccountLabel.Resolve(new AccountLabelInput(OverrideLabel, Identity, SubscriptionType), _index);

    /// <summary>Lets the caller install a whole-set-disambiguated label (AccountLabel.Disambiguate) over this account's own base label -- see StatusBarApplicationContext for where that pass runs.</summary>
    public void ApplyDisambiguatedLabel(string label) => Label = label;

    /// <summary>Kills whatever this account's supervisor started and is still running -- the fallback when a graceful stop did not finish in time.</summary>
    public int KillChildren() => _channelSupervisor.KillLaunchedChildren();

    /// <summary>
    /// Graceful stop (docs/multi-account.md "Child lifecycle"): no more polls, then the child's
    /// stdin is closed and it is given time to exit by itself before anything is killed
    /// (ClaudeCliChannelSupervisor.StopAsync), then the log sink is flushed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ClaudeCliChannelSupervisor supervisor;
        lock (_swapLock)
        {
            _shuttingDown = true;
            supervisor = _channelSupervisor;
        }
        try { _pollTimer.Stop(); _pollTimer.Dispose(); } catch { /* best effort */ }
        await supervisor.DisposeAsync().ConfigureAwait(false);
        await _state.DisposeAsync().ConfigureAwait(false);
    }
}
