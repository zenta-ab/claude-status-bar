using ClaudeStatusBar.Config;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Everything one account owns, end to end (docs/multi-account.md): its own claude.exe child
/// (via ChildProcessSpec.ForAccount + ClaudeCliChannelSupervisor), its own identity-keyed state
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
/// </summary>
public sealed class AccountRuntime : IAsyncDisposable
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    readonly string _slot;
    readonly AccountEntry _config;
    readonly int _index;
    readonly SynchronizationContext _uiContext;
    readonly string _configDir;
    readonly string _baseLogDir;
    readonly ChildProcessSpec _spec;
    readonly System.Windows.Forms.Timer _pollTimer;

    AccountKeyedState _state;
    ClaudeCliChannelSupervisor _channelSupervisor;
    bool _polling;
    bool _shuttingDown;

    public AccountIdentity? Identity { get; private set; }
    public string? SubscriptionType { get; private set; }
    public string Label { get; private set; }
    public QuotaView LastView { get; private set; } = QuotaView.Initial;
    public bool Enabled => _config.Enabled;
    public string? OverrideLabel => _config.Label;
    public string? IdentityKey => _state.IdentityKey;
    public string CurrentLogDir => _state.CurrentLogDir;

    /// <summary>
    /// True from the moment a poll (timer-driven or a forced one, see RequestImmediateRefresh)
    /// starts until ApplyResult finishes applying its outcome -- what the panel's reload button
    /// (the task's "Reload button in the panel") shows as busy/disabled. Backed by the same
    /// _polling flag PollAsync already used to guard against overlapping polls for this account,
    /// so there is exactly one source of truth for "is a poll for this account in flight".
    /// </summary>
    public bool IsPolling => _polling;

    /// <param name="slot">Opaque per-account identifier ("default" for accounts[0], else the account's index) -- only affects the child's working directory.</param>
    /// <param name="config">This account's accounts.json entry.</param>
    /// <param name="index">This account's position in the configured list, for the "Konto N" label fallback.</param>
    /// <param name="uiContext">The UI SynchronizationContext poll completions are marshalled back onto, exactly like StatusBarApplicationContext's own polling used to.</param>
    /// <param name="baseLogDirOverride">Test-only: replaces %LOCALAPPDATA%\ClaudeStatusBar\logs.</param>
    /// <param name="specOverride">Test-only: points the channel at tests/FakeClaudeChild instead of the real claude.exe.</param>
    /// <param name="configDirOverride">Test-only: replaces the resolved config directory identity is read from.</param>
    public AccountRuntime(
        string slot,
        AccountEntry config,
        int index,
        SynchronizationContext uiContext,
        string? baseLogDirOverride = null,
        ChildProcessSpec? specOverride = null,
        string? configDirOverride = null)
    {
        _slot = slot;
        _config = config;
        _index = index;
        _uiContext = uiContext;
        _configDir = configDirOverride ?? (string.IsNullOrWhiteSpace(config.ConfigDir)
            ? AccountIdentity.ResolveDefaultIdentityDir()
            : config.ConfigDir!);
        _baseLogDir = baseLogDirOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "logs");
        _spec = specOverride ?? ChildProcessSpec.ForAccount(slot, config.ConfigDir);

        Identity = AccountIdentity.ReadFrom(_configDir);
        _state = new AccountKeyedState(_baseLogDir, Identity?.StateKey);
        _channelSupervisor = new ClaudeCliChannelSupervisor(_spec, _state.LogSink);

        Label = AccountLabel.Resolve(new AccountLabelInput(OverrideLabel, Identity, SubscriptionType), index);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _pollTimer.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>Launches the child and starts polling. Does not wait for the first poll to land.</summary>
    public void Start()
    {
        _channelSupervisor.Start();
        _pollTimer.Start();
        _ = PollAsync();
    }

    /// <summary>The 1s UI tick for this account: cheap, safe every second, per QuotaModel.Evaluate's own contract.</summary>
    public void Evaluate(DateTimeOffset utcNow, long monoMs) => LastView = _state.Model.Evaluate(utcNow, monoMs);

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
        if (_polling || _shuttingDown) return;
        _polling = true;
        try
        {
            SyncIdentityIfChanged();

            (UsageSnapshot? snapshot, string? error, TimeSpan latency) =
                await _channelSupervisor.GetUsageAsync(RequestTimeout).ConfigureAwait(false);
            _uiContext.Post(_ =>
            {
                if (_shuttingDown) return;
                ApplyResult(snapshot, error, latency);
            }, null);
        }
        finally
        {
            _polling = false;
        }
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
                ClaudeCliChannelSupervisor oldSupervisor = _channelSupervisor;
                _channelSupervisor = new ClaudeCliChannelSupervisor(_spec, _state.LogSink);
                _channelSupervisor.Start();
                _ = oldSupervisor.DisposeAsync();
                SafeLog.Info($"account {_slot}: identity changed, rebuilt state (key={AccountIdentity.KeyPrefixOf(_state.IdentityKey)})");
            }
            Identity = identity;
            RefreshLabel();
        }
        // else: an unreadable/missing .claude.json is "no new information", never an implicit
        // logout -- keep the last known Identity and state exactly as they were.
    }

    void ApplyResult(UsageSnapshot? snapshot, string? error, TimeSpan latency)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        long monoMs = Environment.TickCount64;

        if (snapshot is null)
        {
            _state.Model.IngestFailure(error ?? "unknown error", utcNow, monoMs);
        }
        else
        {
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
        }

        TimeSpan next = _state.Model.NextPollDelay(utcNow);
        _pollTimer.Interval = Math.Max(1, (int)Math.Round(next.TotalMilliseconds));

        LastView = _state.Model.Evaluate(utcNow, monoMs);
        LogCommittedState();
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

    public async ValueTask DisposeAsync()
    {
        _shuttingDown = true;
        try { _pollTimer.Stop(); _pollTimer.Dispose(); } catch { /* best effort */ }
        await _channelSupervisor.DisposeAsync().ConfigureAwait(false);
        await _state.DisposeAsync().ConfigureAwait(false);
    }
}
