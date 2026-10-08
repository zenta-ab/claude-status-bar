using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Diagnostics;
using ClaudeStatusBar.Model;
using ClaudeStatusBar.Ui;

namespace ClaudeStatusBar.Flow;

/// <summary>One running account as the flows need to see it (a snapshot taken from the live runtime).</summary>
/// <param name="HasRead">True once this runtime has had at least one successful reading -- the only thing that ends a NeedsLogin spell for toast purposes.</param>
public sealed record RunningAccount(string Slot, string Label, AccountIdentity? Identity, string? Plan, bool NeedsLogin, bool HasRead);

/// <summary>The live set of account runtimes, as seen by the flows. Implemented by StatusBarApplicationContext; faked in tests.</summary>
public interface IRuntimeHost
{
    IReadOnlyList<RunningAccount> Running { get; }

    /// <summary>
    /// Stops the runtimes of these slots gracefully and forgets them (they leave <see cref="Running"/>
    /// at once). False when that did not finish within the deadline: their children may still be alive.
    /// </summary>
    Task<bool> StopAsync(IReadOnlyCollection<string> slots);

    /// <summary>Kills whatever the runtimes stopped by <see cref="StopAsync"/> for these slots still have running, and returns once it is gone.</summary>
    Task KillAsync(IReadOnlyCollection<string> slots);

    /// <summary>Builds, starts and renders a runtime for every enabled entry of the config, with new icons.</summary>
    void StartAll(AccountsConfig config);
}

/// <summary>What the flows tell the user, and what they tell the rest of the UI about slot identity changes.</summary>
public interface IFlowUi
{
    /// <summary>A yes/no question in front of every other window (topmost, activated). True for yes.</summary>
    bool Confirm(string title, string body);

    /// <summary>A message in front of every other window.</summary>
    void Inform(string text, bool warning);

    /// <summary>A short tray balloon.</summary>
    void Toast(string text);

    /// <summary>A small topmost dialog with a text box prefilled with `current`. The text typed, or null when cancelled.</summary>
    string? PromptName(string title, string hint, string current, int maxLength);

    /// <summary>Labels changed: redraw icons, panel and menu now. Nothing is rebuilt or restarted.</summary>
    void LabelsChanged();

    /// <summary>The account that was at `oldSlot` is now at `newSlot` (a re-login). Anything remembered by slot id -- panel focus, the right-clicked icon -- follows it.</summary>
    void SlotReplaced(string oldSlot, string newSlot);

    /// <summary>The account at this slot is gone.</summary>
    void SlotRemoved(string slot);
}

/// <summary>
/// Every operation that changes which accounts exist -- add, re-login, remove, discard, rebuild --
/// and the NeedsLogin toast, in one class with injected seams (slot store, runtime host, auth CLI,
/// dialogs, config save), so the ORDER of their steps -- which is what keeps a good login from ever
/// being lost -- is tested, crash windows included (tests/.../AccountFlowControllerTests).
///
/// The rules it enforces (docs/multi-account.md "Login flow"):
///
///   * One operation at a time. The busy flag is set BEFORE any dialog, so nothing can start while
///     a confirmation is on screen.
///   * The slot a login console is writing into is PROTECTED: every reconciliation pass skips it,
///     so a rebuild can never retire and delete it under a running `auth login`.
///   * Never lose a good login. A re-login writes the new slot Active and saves the entry pointing at
///     it FIRST; only then is the old child stopped and the old slot retired and deleted, and a
///     failure after that point never discards the new slot. A removal writes the retired marker
///     FIRST, then saves the entry removal, and deletes nothing if the marker cannot be written.
///   * `auth status` with no answer (a cold first claude.exe run can be slow) is retried once with a
///     longer timeout; if there is still none but the slot holds an oauthAccount, the login is
///     accepted -- the new child's first poll flags NeedsLogin if it was not valid after all.
///   * Stopping runtimes that did not stop in time is followed by killing what is left and waiting
///     for it, BEFORE any slot is deleted or the new set starts.
///   * While the set changes (IsMutating) the UI must not tick: no focus is resolved against a half
///     changed list and the zero-accounts icon never flashes.
/// </summary>
public sealed class AccountFlowController
{
    /// <summary>First `auth status` attempt, then the retry for a slow cold start.</summary>
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StatusRetryTimeout = TimeSpan.FromSeconds(60);

    readonly SlotStore _slots;
    readonly IRuntimeHost _host;
    readonly IAuthCli _auth;
    readonly IFlowUi _ui;
    readonly Action<AccountsConfig> _save;
    readonly CancellationToken _shutdown;
    readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    readonly HashSet<string> _needsLoginNotified = new(StringComparer.Ordinal);
    int _mutating;

    public AccountFlowController(
        AccountsConfig config, SlotStore slots, IRuntimeHost host, IAuthCli auth, IFlowUi ui,
        Action<AccountsConfig> save, CancellationToken shutdown)
    {
        Config = config;
        _slots = slots;
        _host = host;
        _auth = auth;
        _ui = ui;
        _save = save;
        _shutdown = shutdown;
    }

    public AccountsConfig Config { get; }

    /// <summary>An add / re-login / remove is in progress: the menu entries that start one are disabled.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>The account set is being changed: the UI tick must neither resolve focus nor render.</summary>
    public bool IsMutating => Volatile.Read(ref _mutating) > 0;

    /// <summary>Slots currently protected from reconciliation (the pending slot of a running login).</summary>
    public IReadOnlyCollection<string> ProtectedSlots => _protected.ToArray();

    // ---- startup ----

    /// <summary>
    /// Loads accounts.json and reconciles it against the slots (migrates v1, finishes retired
    /// deletes, adopts or discards interrupted logins, rebuilds a quarantined file's set). Saved only
    /// when something changed. Never throws.
    /// </summary>
    public static AccountsConfig LoadAndReconcile(SlotStore slots, string? path = null)
    {
        AccountsConfigLoad load = AccountsConfig.Load(path);
        AccountsConfig config = load.Config;
        try
        {
            AccountReconciler.Result result = AccountReconciler.Reconcile(config, slots);
            if (result.Changed || load.Status != AccountsConfigStatus.Loaded) AccountsConfig.Save(config, path);
            SafeLog.Info($"accounts: {config.Accounts.Count} tracked (adopted {result.Adopted}, appended {result.Appended}, dropped {result.Dropped}, retired {result.Retired})");
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"reconciling accounts.json against the login slots failed: {ex.Message}");
        }
        return config;
    }

    // ---- rebuild ----

    /// <summary>
    /// Replaces the whole account set: every runtime is stopped (gracefully; whatever outlasts the
    /// deadline is killed and waited for), the config is reconciled against the slots again --
    /// off the UI thread, with the protected slots skipped -- and a new set is built and started.
    /// </summary>
    public async Task RebuildAsync()
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            await StopRuntimesAsync(_host.Running.Select(r => r.Slot).ToList());
            if (_shutdown.IsCancellationRequested) return;

            IReadOnlySet<string> protectedNow = _protected.ToHashSet(StringComparer.Ordinal);
            await Task.Run(() =>
            {
                try
                {
                    AccountReconciler.Reconcile(Config, _slots, protectedSlots: protectedNow);
                    TrySave();
                }
                catch (Exception ex)
                {
                    SafeLog.Warn($"reconciling after a change failed: {ex.Message}");
                }
            });

            _needsLoginNotified.IntersectWith(Config.Accounts.Where(a => a.Slot is not null).Select(a => a.Slot!));
            _host.StartAll(Config);
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    async Task StopRuntimesAsync(IReadOnlyCollection<string> slots)
    {
        if (slots.Count == 0) return;
        bool graceful = await _host.StopAsync(slots);
        if (!graceful)
        {
            SafeLog.Warn("stopping accounts did not finish in time; killing what is left");
            await _host.KillAsync(slots);
        }
    }

    // ---- NeedsLogin toast ----

    /// <summary>
    /// One toast per transition INTO NeedsLogin. The record of who has been told is kept by slot id
    /// across rebuilds, and only a successful reading ends a spell -- a freshly built runtime that has
    /// not read yet is not "logged in again", so a rebuild never re-toasts.
    /// </summary>
    public void NotifyNeedsLoginTransitions()
    {
        foreach (RunningAccount account in _host.Running)
        {
            if (account.NeedsLogin)
            {
                if (_needsLoginNotified.Add(account.Slot)) _ui.Toast(LoginText.NeedsLoginToast(account.Label));
            }
            else if (account.HasRead)
            {
                _needsLoginNotified.Remove(account.Slot);
            }
        }
    }

    // ---- add / re-login ----

    /// <summary>
    /// Add (reloginSlot null) or re-login of one account: ALWAYS into a NEW slot folder. See the type
    /// comment for the ordering rules; the outcome table is Model/LoginFlowDecision.
    /// </summary>
    public async Task RunLoginFlowAsync(string? reloginSlot)
    {
        if (IsBusy || _shutdown.IsCancellationRequested) return;
        IsBusy = true;
        bool relogin = reloginSlot is not null;
        string? newSlot = null;
        try
        {
            newSlot = _slots.CreatePending();
            _protected.Add(newSlot);

            int? exitCode = await _auth.RunLoginConsoleAsync(newSlot, LoginText.ConsoleNotice, _shutdown);
            if (_shutdown.IsCancellationRequested) return; // the console is left running; the next start adopts or discards the pending slot

            AuthStatus? status = null;
            if (exitCode is not null)
            {
                status = await _auth.GetStatusAsync(newSlot, StatusTimeout, _shutdown);
                status ??= await _auth.GetStatusAsync(newSlot, StatusRetryTimeout, _shutdown); // a cold first run can be slow
            }

            AccountIdentity? identity = IdentityOfSlot(newSlot);
            if (status is null && identity is not null)
            {
                // No answer from `auth status`, but the login left an oauthAccount behind: believe it.
                // If it was not valid after all, the new child's first poll flags NeedsLogin.
                SafeLog.Info($"slot {newSlot}: auth status gave no answer; accepting the login on its oauthAccount");
                status = new AuthStatus(true, "claude.ai");
            }
            if (_shutdown.IsCancellationRequested) return;

            LoginDecision decision = LoginFlowDecision.Decide(status, identity, TrackedAccounts(), reloginSlot);
            switch (decision.Outcome)
            {
                case LoginOutcome.Adopt:
                    await AdoptAsync(newSlot, decision);
                    break;

                case LoginOutcome.Swap when Config.Accounts.FirstOrDefault(a => a.Slot == reloginSlot) is { } entry:
                    await SwapAsync(entry, reloginSlot!, newSlot, decision);
                    break;

                case LoginOutcome.RejectDuplicate:
                    await DiscardSlotAsync(newSlot);
                    _ui.Inform(LoginText.Duplicate(decision.MatchedLabel ?? decision.NewLabel), warning: true);
                    break;

                case LoginOutcome.RejectDifferentAccount:
                    await DiscardSlotAsync(newSlot);
                    _ui.Inform(LoginText.DifferentAccount(LabelOfSlot(reloginSlot) ?? "kontot", decision.MatchedLabel ?? decision.NewLabel), warning: true);
                    break;

                case LoginOutcome.RejectUnverifiable:
                    await DiscardSlotAsync(newSlot);
                    _ui.Inform(LoginText.CannotVerify(LabelOfSlot(reloginSlot) ?? "kontot"), warning: true);
                    break;

                default: // RejectNotLoggedIn, or a Swap whose entry vanished meanwhile
                    await DiscardSlotAsync(newSlot);
                    _ui.Inform(LoginText.NotLoggedIn(relogin), warning: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"login flow failed ({ex.GetType().Name}): {ex.Message}");
            try
            {
                // Only a slot that never became an account is discarded; an Active one is somebody's login.
                if (newSlot is not null && _slots.ReadMarker(newSlot)?.State != SlotState.Active) await DiscardSlotAsync(newSlot);
                await RebuildAsync(); // back to a consistent set whatever was half done
            }
            catch (Exception inner) { SafeLog.Warn($"recovery after a failed login flow failed: {inner.Message}"); }
            if (!_shutdown.IsCancellationRequested) _ui.Inform(LoginText.FlowFailed(relogin), warning: true);
        }
        finally
        {
            if (newSlot is not null) _protected.Remove(newSlot);
            IsBusy = false;
        }
    }

    async Task AdoptAsync(string newSlot, LoginDecision decision)
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            _slots.WriteMarker(newSlot, SlotState.Active);
            Config.Accounts.Add(new AccountEntry { Slot = newSlot, Enabled = true });
            TrySave(); // if this fails the rebuild's reconciliation appends the active slot anyway
            await RebuildAsync();
            _ui.Toast(LoginText.LoggedIn(decision.NewLabel));
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Re-login of the SAME account. The new slot becomes Active and the entry is saved pointing at it
    /// FIRST. Only then is the old child stopped and the old slot retired and deleted, and nothing
    /// that goes wrong after that point can discard the new slot -- the worst outcome is a leftover
    /// old folder that the next start finishes deleting.
    /// </summary>
    async Task SwapAsync(AccountEntry entry, string oldSlot, string newSlot, LoginDecision decision)
    {
        Interlocked.Increment(ref _mutating);
        try
        {
            try
            {
                _slots.WriteMarker(newSlot, SlotState.Active);
                entry.Slot = newSlot;
                if (!TrySave()) throw new IOException("accounts.json could not be saved");
            }
            catch (Exception ex)
            {
                // Nothing has been taken away from the old account: put the entry back, drop the new slot.
                SafeLog.Warn($"swap {oldSlot}: could not commit the new slot ({ex.GetType().Name}); the old login is untouched");
                entry.Slot = oldSlot;
                await DiscardSlotAsync(newSlot);
                _ui.Inform(LoginText.FlowFailed(relogin: true), warning: true);
                return;
            }

            _ui.SlotReplaced(oldSlot, newSlot); // focus follows the account BEFORE anything is awaited
            _needsLoginNotified.Remove(oldSlot);

            // From here the new slot is an account: it is never discarded, whatever fails below.
            try { await StopRuntimesAsync(new[] { oldSlot }); }
            catch (Exception ex) { SafeLog.Warn($"swap {oldSlot}: stopping the old child threw ({ex.GetType().Name})"); }

            bool retired = false;
            try { _slots.WriteMarker(oldSlot, SlotState.Retired); retired = true; }
            catch (Exception ex) { SafeLog.Warn($"swap {oldSlot}: could not retire the old slot ({ex.GetType().Name}); it is left in place"); }
            if (retired) await _slots.DeleteWithRetryAsync(oldSlot); // a locked folder is finished by the next start

            await RebuildAsync();
            _ui.Toast(LoginText.LoggedInAgain(decision.NewLabel));
        }
        finally
        {
            Interlocked.Decrement(ref _mutating);
        }
    }

    /// <summary>
    /// Retires a slot nobody wants and deletes its folder. If the retired marker cannot be written
    /// NOTHING is deleted (a folder that is not marked retired might still be a login somebody needs).
    /// No `auth logout`: revoking the grant could affect another slot's, unverified. False when the
    /// folder was not (completely) deleted.
    /// </summary>
    public async Task<bool> DiscardSlotAsync(string slot)
    {
        try { _slots.WriteMarker(slot, SlotState.Retired); }
        catch (Exception ex)
        {
            SafeLog.Warn($"slot {slot}: could not write the retired marker ({ex.GetType().Name}); not deleting it");
            return false;
        }
        return await _slots.DeleteWithRetryAsync(slot);
    }

    // ---- remove ----

    /// <summary>
    /// "Konton ▸ account ▸ Ta bort…". The busy flag is taken BEFORE the confirmation, so no other
    /// operation can start under the dialog; after the dialog the entry is looked up again and the
    /// removal is abandoned if it changed meanwhile. Order: retired marker first (no marker, no
    /// removal), then the entry removal is saved, then the child is stopped and the folder deleted.
    /// </summary>
    public async Task RemoveAsync(string slot)
    {
        if (IsBusy || _shutdown.IsCancellationRequested) return;
        IsBusy = true;
        try
        {
            AccountEntry? entry = Config.Accounts.FirstOrDefault(a => a.Slot == slot);
            if (entry is null) return;

            string label = LabelOfSlot(slot) ?? "kontot";
            string? plan = AccountLabel.TitleCasePlan(_host.Running.FirstOrDefault(r => r.Slot == slot)?.Plan);
            if (!_ui.Confirm(LoginText.RemoveTitle(label), LoginText.RemoveBody(label, plan))) return;

            if (!ReferenceEquals(Config.Accounts.FirstOrDefault(a => a.Slot == slot), entry))
            {
                SafeLog.Info($"remove {slot}: the entry changed while the dialog was open; nothing removed");
                return;
            }

            Interlocked.Increment(ref _mutating);
            try
            {
                try { _slots.WriteMarker(slot, SlotState.Retired); }
                catch (Exception ex)
                {
                    SafeLog.Warn($"remove {slot}: could not write the retired marker ({ex.GetType().Name}); nothing removed");
                    _ui.Inform(LoginText.RemoveFailed, warning: true);
                    return;
                }

                int position = Config.Accounts.IndexOf(entry);
                Config.Accounts.Remove(entry);
                if (!TrySave())
                {
                    Config.Accounts.Insert(Math.Min(position, Config.Accounts.Count), entry);
                    try { _slots.WriteMarker(slot, SlotState.Active); } catch { /* the next reconciliation re-reads the truth */ }
                    _ui.Inform(LoginText.RemoveFailed, warning: true);
                    return;
                }

                _ui.SlotRemoved(slot);
                _needsLoginNotified.Remove(slot);
                try { await StopRuntimesAsync(new[] { slot }); }
                catch (Exception ex) { SafeLog.Warn($"remove {slot}: stopping the child threw ({ex.GetType().Name})"); }
                await _slots.DeleteWithRetryAsync(slot);
                await RebuildAsync();
            }
            finally
            {
                Interlocked.Decrement(ref _mutating);
            }
        }
        catch (Exception ex)
        {
            SafeLog.Warn($"removing slot {slot} failed ({ex.GetType().Name}): {ex.Message}");
            try { await RebuildAsync(); } catch { /* best effort */ }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- rename ----

    /// <summary>The longest name a user may give an account.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>Trimmed, control characters dropped, capped at <see cref="MaxLabelLength"/>. Empty means "use the automatic name".</summary>
    public static string NormalizeLabel(string? input)
    {
        string cleaned = new string((input ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length <= MaxLabelLength ? cleaned : cleaned[..MaxLabelLength].TrimEnd();
    }

    /// <summary>
    /// "Konton > account > Byt namn...". The name is stored in the entry's label BY SLOT ID: the entry is
    /// looked up again after the dialog and the rename abandoned if it changed. An empty name clears the
    /// override (the automatic name returns). It touches no runtime and rebuilds nothing -- the labels
    /// are simply redrawn. Respects the busy flag like the other actions.
    /// </summary>
    public void Rename(string slot)
    {
        if (IsBusy || _shutdown.IsCancellationRequested) return;
        IsBusy = true;
        try
        {
            AccountEntry? entry = Config.Accounts.FirstOrDefault(a => a.Slot == slot);
            if (entry is null) return;

            string shown = LabelOfSlot(slot) ?? slot;
            string? typed = _ui.PromptName(LoginText.RenameTitle(shown), LoginText.RenameHint, shown, MaxLabelLength);
            if (typed is null) return;

            if (!ReferenceEquals(Config.Accounts.FirstOrDefault(a => a.Slot == slot), entry))
            {
                SafeLog.Info($"rename {slot}: the entry changed while the dialog was open; nothing renamed");
                return;
            }

            string name = NormalizeLabel(typed);
            string? newLabel = name.Length == 0 ? null : name;
            // Confirming the automatic name unchanged must not freeze it into an override.
            if (entry.Label is null && newLabel == shown) return;
            if (newLabel == entry.Label) return;

            string? previous = entry.Label;
            entry.Label = newLabel;
            if (!TrySave())
            {
                entry.Label = previous;
                _ui.Inform(LoginText.RenameFailed, warning: true);
                return;
            }
            _ui.LabelsChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- helpers ----

    bool TrySave()
    {
        try { _save(Config); return true; }
        catch (Exception ex)
        {
            SafeLog.Warn($"failed to save accounts.json: {ex.Message}");
            return false;
        }
    }

    AccountIdentity? IdentityOfSlot(string slot)
    {
        if (_host.Running.FirstOrDefault(r => r.Slot == slot) is { Identity: { } known }) return known;
        try { return AccountIdentity.ReadFrom(_slots.ConfigDirOf(slot)); }
        catch { return null; }
    }

    string? LabelOfSlot(string? slot)
    {
        if (slot is null) return null;
        if (_host.Running.FirstOrDefault(r => r.Slot == slot) is { } running) return running.Label;

        int index = Config.Accounts.FindIndex(a => a.Slot == slot);
        if (index < 0) return null;
        return AccountLabel.Resolve(new AccountLabelInput(Config.Accounts[index].Label, IdentityOfSlot(slot), null), index);
    }

    List<TrackedAccount> TrackedAccounts() =>
        Config.Accounts
            .Where(a => a.Slot is not null)
            .Select(a => new TrackedAccount(a.Slot!, LabelOfSlot(a.Slot)!, IdentityOfSlot(a.Slot!)))
            .ToList();

    /// <summary>The label the tray menu shows for a tracked slot.</summary>
    public string LabelForMenu(string slot) => LabelOfSlot(slot) ?? slot;
}
