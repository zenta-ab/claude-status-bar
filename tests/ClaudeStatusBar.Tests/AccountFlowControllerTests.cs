using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using ClaudeStatusBar.Flow;
using ClaudeStatusBar.Ui;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Flow/AccountFlowController: add / re-login / remove / discard / rebuild, with every seam faked
/// (slot store subclass that records and can fail, runtime host, auth CLI, dialogs, config save), so
/// the ORDER of the steps -- which is what keeps a good login from being lost -- and the crash
/// windows between them are tested. Real folders in a temp dir stand in for the slots. All
/// identities are placeholders.
/// </summary>
public sealed class AccountFlowControllerTests : IDisposable
{
    const string Person = "11111111-1111-4111-8111-111111111111";
    const string OrgX = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    const string OrgY = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    const string NewSlot = "cccccccc";

    // ---- seams ----

    sealed class EventLog
    {
        readonly List<string> _events = new();
        public void Add(string e) { lock (_events) _events.Add(e); }
        public IReadOnlyList<string> All { get { lock (_events) return _events.ToList(); } }
        public int IndexOf(string e) => All.ToList().IndexOf(e);
        public bool Has(string e) => IndexOf(e) >= 0;
        public bool Before(string first, string second) => IndexOf(first) >= 0 && IndexOf(second) > IndexOf(first);
    }

    sealed class RecordingSlotStore : SlotStore
    {
        readonly EventLog _log;
        public Func<string, SlotState, bool>? FailMarker;

        public RecordingSlotStore(string root, EventLog log, Func<string> idSource) : base(root, idSource) => _log = log;

        public override void WriteMarker(string slotId, SlotState state)
        {
            _log.Add($"marker:{slotId}:{state}");
            if (FailMarker?.Invoke(slotId, state) == true) throw new IOException("injected marker failure");
            base.WriteMarker(slotId, state);
        }

        public override Task<bool> DeleteWithRetryAsync(string slotId, int attempts = 4, int delayMs = 500)
        {
            _log.Add($"delete:{slotId}");
            return base.DeleteWithRetryAsync(slotId, attempts: 1, delayMs: 0);
        }
    }

    sealed class FakeHost : IRuntimeHost
    {
        readonly EventLog _log;
        readonly Func<AccountFlowController?> _controller;
        readonly string[] _stopWatch;
        public List<RunningAccount> Accounts = new();
        public bool StopTimesOut;
        public Exception? StopThrows;
        public Action<string>? OnKill;
        public bool? MutatingDuringStop;
        public bool? MutatingDuringStart;

        public FakeHost(EventLog log, Func<AccountFlowController?> controller, params string[] stopWatch)
        {
            _log = log;
            _controller = controller;
            _stopWatch = stopWatch;
        }

        public IReadOnlyList<RunningAccount> Running => Accounts.ToList();

        public Task<bool> StopAsync(IReadOnlyCollection<string> slots)
        {
            _log.Add("stop:" + string.Join(",", slots.OrderBy(s => s)));
            MutatingDuringStop = _controller()!.IsMutating;
            if (StopThrows is not null) throw StopThrows;
            Accounts.RemoveAll(a => slots.Contains(a.Slot));
            return Task.FromResult(!StopTimesOut);
        }

        public Task KillAsync(IReadOnlyCollection<string> slots)
        {
            _log.Add("kill:" + string.Join(",", slots.OrderBy(s => s)));
            OnKill?.Invoke(string.Join(",", slots));
            return Task.CompletedTask;
        }

        public void StartAll(AccountsConfig config)
        {
            MutatingDuringStart = _controller()!.IsMutating;
            Accounts = config.Accounts.Where(a => a.Enabled && a.Slot is not null)
                .Select(a => new RunningAccount(a.Slot!, a.Label ?? a.Slot!, null, null, false, false))
                .ToList();
            _log.Add("start:" + string.Join(",", Accounts.Select(a => a.Slot)));
        }
    }

    sealed class FakeUi : IFlowUi
    {
        readonly EventLog _log;
        public bool ConfirmAnswer = true;
        public Action? OnConfirm;
        public readonly List<string> Informed = new();
        public readonly List<string> Toasts = new();

        public FakeUi(EventLog log) => _log = log;

        public bool Confirm(string title, string body) { _log.Add("confirm"); OnConfirm?.Invoke(); return ConfirmAnswer; }
        public void Inform(string text, bool warning) { _log.Add("inform"); Informed.Add(text); }
        public void Toast(string text) { _log.Add("toast"); Toasts.Add(text); }
        public void SlotReplaced(string oldSlot, string newSlot) => _log.Add($"replaced:{oldSlot}->{newSlot}");
        public void SlotRemoved(string slot) => _log.Add($"removed:{slot}");

        public string? PromptAnswer;
        public Action? OnPrompt;
        public int PromptCount;
        public string? PromptedTitle, PromptedHint, PromptedCurrent;
        public int PromptedMaxLength;

        public string? PromptName(string title, string hint, string current, int maxLength)
        {
            PromptCount++;
            (PromptedTitle, PromptedHint, PromptedCurrent, PromptedMaxLength) = (title, hint, current, maxLength);
            _log.Add("prompt");
            OnPrompt?.Invoke();
            return PromptAnswer;
        }

        public void LabelsChanged() => _log.Add("labels-changed");
    }

    sealed class FakeAuth : IAuthCli
    {
        readonly EventLog _log;
        public Func<string, Task>? OnConsole;
        public int? ExitCode = 0;
        public Exception? ConsoleThrows;
        public readonly Queue<AuthStatus?> StatusAnswers = new();
        public readonly List<TimeSpan> StatusTimeouts = new();
        public string? LastSlot;

        public FakeAuth(EventLog log) => _log = log;

        public async Task<int?> RunLoginConsoleAsync(string slotId, string notice, CancellationToken ct)
        {
            LastSlot = slotId;
            _log.Add("console:" + slotId);
            if (ConsoleThrows is not null) throw ConsoleThrows;
            if (OnConsole is not null) await OnConsole(slotId);
            return ct.IsCancellationRequested ? null : ExitCode;
        }

        public Task<AuthStatus?> GetStatusAsync(string slotId, TimeSpan timeout, CancellationToken ct)
        {
            _log.Add("status:" + slotId);
            StatusTimeouts.Add(timeout);
            return Task.FromResult(StatusAnswers.Count > 0 ? StatusAnswers.Dequeue() : null);
        }
    }

    // ---- rig ----

    readonly string _temp = Path.Combine(Path.GetTempPath(), "csb-flow-" + Guid.NewGuid().ToString("N"));
    readonly EventLog _log = new();
    readonly RecordingSlotStore _store;
    readonly FakeHost _host;
    readonly FakeUi _ui;
    readonly FakeAuth _auth;
    readonly AccountsConfig _config = new() { Version = AccountsConfig.CurrentVersion };
    readonly CancellationTokenSource _shutdown = new();
    readonly AccountFlowController? _controller0;
    AccountFlowController _controller => _controller0!;
    Exception? _saveThrows;

    public AccountFlowControllerTests()
    {
        Directory.CreateDirectory(_temp);
        _store = new RecordingSlotStore(Path.Combine(_temp, "accounts"), _log, () => NewSlot);
        _host = new FakeHost(_log, () => _controller!);
        _ui = new FakeUi(_log);
        _auth = new FakeAuth(_log);
        _controller0 = new AccountFlowController(_config, _store, _host, _auth, _ui, SaveConfig, _shutdown.Token);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    void SaveConfig(AccountsConfig config)
    {
        _log.Add("save:" + string.Join(",", config.Accounts.Select(a => a.Slot)));
        if (_saveThrows is not null) throw _saveThrows;
    }

    string ConfigDir(string slot) => Path.Combine(_temp, "accounts", slot, "config");
    string SlotDir(string slot) => Path.Combine(_temp, "accounts", slot);

    void WriteIdentity(string slot, string account, string org)
    {
        Directory.CreateDirectory(ConfigDir(slot));
        File.WriteAllText(Path.Combine(ConfigDir(slot), ".claude.json"),
            $$"""{ "oauthAccount": { "accountUuid": "{{account}}", "organizationUuid": "{{org}}", "organizationName": "Acme AB" } }""");
    }

    /// <summary>An existing, active, tracked, running account (its login is on disk, its identity is known to the runtime).</summary>
    void AddTracked(string slot, string account, string org, string label = "Acme AB", bool identityOnDisk = true)
    {
        Directory.CreateDirectory(ConfigDir(slot));
        if (identityOnDisk) WriteIdentity(slot, account, org);
        _store.WriteMarker(slot, SlotState.Active);
        _config.Accounts.Add(new AccountEntry { Slot = slot, Enabled = true });
        _host.Accounts.Add(new RunningAccount(slot, label, identityOnDisk ? Identity(account, org) : null, "team", false, true));
    }

    static AccountIdentity Identity(string account, string org) => new(account, org, "Acme AB", "claude_team", null, null, "alex@example.com", "Alex");

    /// <summary>The login console "logs in" as this identity: writes it into the new slot's config dir.</summary>
    void LoginAs(string account, string org) =>
        _auth.OnConsole = slot => { WriteIdentity(slot, account, org); return Task.CompletedTask; };

    void LoggedIn() => _auth.StatusAnswers.Enqueue(new AuthStatus(true, "claude.ai"));

    // ---- re-login (swap): the new login is committed BEFORE the old one is touched ----

    [Fact]
    public async Task Swap_CommitsTheNewSlotBeforeAnythingIsTakenFromTheOldOne()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgX);
        LoggedIn();

        await _controller.RunLoginFlowAsync("1");

        Assert.True(_log.Before($"marker:{NewSlot}:Active", $"save:{NewSlot}"), "the new slot is Active, then the entry is saved pointing at it");
        Assert.True(_log.Before($"save:{NewSlot}", "stop:1"), "the old child is stopped only after the entry points at the new slot");
        Assert.True(_log.Before("stop:1", "marker:1:Retired"));
        Assert.True(_log.Before("marker:1:Retired", "delete:1"));
        Assert.True(_log.Before($"replaced:1->{NewSlot}", "stop:1"), "panel focus follows the account before anything is awaited");
        Assert.Equal(new[] { NewSlot }, _config.Accounts.Select(a => a.Slot));
        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);
        Assert.False(Directory.Exists(SlotDir("1")));
        Assert.Contains(LoginText.LoggedInAgain("Acme AB"), _ui.Toasts);
    }

    [Fact]
    public async Task Swap_WhenRetiringTheOldSlotFails_TheNewSlotSurvives_AndTheOldOneIsNotDeleted()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgX);
        LoggedIn();
        _store.FailMarker = (slot, state) => slot == "1" && state == SlotState.Retired;

        await _controller.RunLoginFlowAsync("1");

        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);
        Assert.True(Directory.Exists(ConfigDir(NewSlot)), "the new login survives");
        Assert.False(_log.Has($"marker:{NewSlot}:Retired"));
        Assert.False(_log.Has("delete:1"), "an old slot that could not be marked retired is not deleted");
        // The entry points at the new slot. The old slot is still Active, so reconciliation lists it again (as a duplicate row the user can remove) rather than losing a login.
        Assert.Equal(NewSlot, _config.Accounts[0].Slot);
    }

    [Fact]
    public async Task Swap_AFailureWhileStoppingTheOldChild_NeverDiscardsTheNewSlot()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgX);
        LoggedIn();
        _host.StopThrows = new InvalidOperationException("injected");

        await _controller.RunLoginFlowAsync("1");

        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);
        Assert.True(Directory.Exists(ConfigDir(NewSlot)));
        Assert.False(_log.Has($"marker:{NewSlot}:Retired"));
        Assert.Equal(NewSlot, _config.Accounts.Single().Slot);
    }

    [Fact]
    public async Task Swap_WhenTheEntryCannotBeSaved_TheOldLoginIsUntouched_AndTheNewSlotIsDropped()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgX);
        LoggedIn();
        _saveThrows = new IOException("disk full");

        await _controller.RunLoginFlowAsync("1");

        Assert.Equal("1", _config.Accounts.Single().Slot);                // entry put back
        Assert.True(Directory.Exists(ConfigDir("1")));                    // old login still there
        Assert.False(_log.Has("marker:1:Retired"));
        Assert.False(_log.Has("stop:1"));
        Assert.False(Directory.Exists(SlotDir(NewSlot)));                 // only the NEW slot is thrown away
        Assert.Single(_ui.Informed);
    }

    // ---- status without an answer ----

    [Fact]
    public async Task StatusTimeout_WithAReadableIdentity_RetriesOnceLonger_ThenAcceptsTheLogin()
    {
        LoginAs(Person, OrgX);
        _auth.StatusAnswers.Enqueue(null);   // first attempt: the cold start was too slow
        _auth.StatusAnswers.Enqueue(null);   // the longer retry: still nothing

        await _controller.RunLoginFlowAsync(null);

        Assert.Equal(new[] { AccountFlowController.StatusTimeout, AccountFlowController.StatusRetryTimeout }, _auth.StatusTimeouts);
        Assert.True(AccountFlowController.StatusRetryTimeout > AccountFlowController.StatusTimeout);
        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);   // a just-completed login is not deleted
        Assert.Equal(NewSlot, _config.Accounts.Single().Slot);
        Assert.Contains(_ui.Toasts, t => t.StartsWith("Inloggad: "));
        Assert.Empty(_ui.Informed);
    }

    [Fact]
    public async Task StatusTimeout_ThenTheRetryAnswers_UsesTheAnswer()
    {
        LoginAs(Person, OrgX);
        _auth.StatusAnswers.Enqueue(null);
        LoggedIn();

        await _controller.RunLoginFlowAsync(null);

        Assert.Equal(2, _auth.StatusTimeouts.Count);
        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);
    }

    [Fact]
    public async Task StatusTimeout_WithNoIdentityOnDisk_IsNotALogin_AndTheEmptySlotIsDiscarded()
    {
        // The console closed without a login: nothing readable, no answer.
        await _controller.RunLoginFlowAsync(null);

        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.Empty(_config.Accounts);
        Assert.Equal(new[] { LoginText.NotLoggedIn(relogin: false) }, _ui.Informed);
    }

    [Fact]
    public async Task AnExplicitNotLoggedInAnswer_IsBelievedEvenIfAnIdentityIsOnDisk()
    {
        LoginAs(Person, OrgX);
        _auth.StatusAnswers.Enqueue(new AuthStatus(false, "none"));

        await _controller.RunLoginFlowAsync(null);

        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.Single(_auth.StatusTimeouts); // an answer, so no retry
    }

    // ---- the protected pending slot ----

    [Fact]
    public async Task ARebuildDuringALogin_NeverRetiresOrDeletesTheSlotTheConsoleIsWritingInto()
    {
        AddTracked("1", Person, OrgX);
        LoggedIn();
        bool survived = false;
        _auth.OnConsole = async slot =>
        {
            // The console is open and nothing has been logged in yet. Something else rebuilds the set
            // meanwhile (a stop timeout, a failed removal's recovery ...): its reconciliation sees a
            // pending slot with no identity, which it would retire and DELETE -- under the running login.
            await _controller.RebuildAsync();
            survived = Directory.Exists(ConfigDir(slot)) && _store.ReadMarker(slot)?.State == SlotState.Pending;
            Assert.Contains(slot, _controller.ProtectedSlots);
            WriteIdentity(slot, Person, OrgY); // ... and now the user finishes logging in
        };

        await _controller.RunLoginFlowAsync(null);

        Assert.True(survived, "the pending slot under a running login was deleted by a rebuild's reconciliation");
        Assert.Equal(SlotState.Active, _store.ReadMarker(NewSlot)!.State);
        Assert.Equal(new[] { "1", NewSlot }, _config.Accounts.Select(a => a.Slot));
        Assert.Empty(_controller.ProtectedSlots);
    }
    [Fact]
    public async Task OnceTheLoginIsDone_TheSlotIsNoLongerProtected_AndTheNextReconciliationTreatsItNormally()
    {
        LoginAs(Person, OrgX);
        _auth.StatusAnswers.Enqueue(new AuthStatus(false, "none"));
        await _controller.RunLoginFlowAsync(null);

        Assert.Empty(_controller.ProtectedSlots);
        Assert.False(_controller.IsBusy);
    }

    [Fact]
    public async Task NothingCanStartWhileAConfirmationIsOnScreen_TheBusyFlagComesBeforeTheDialog()
    {
        AddTracked("1", Person, OrgX);
        bool busyInsideDialog = false;
        Task? startedDuringDialog = null;
        _ui.OnConfirm = () =>
        {
            busyInsideDialog = _controller.IsBusy;
            startedDuringDialog = _controller.RunLoginFlowAsync(null); // "Lägg till konto…" clicked under the dialog
        };
        _ui.ConfirmAnswer = false;

        await _controller.RemoveAsync("1");
        await startedDuringDialog!;

        Assert.True(busyInsideDialog);
        Assert.False(_log.Has($"console:{NewSlot}"), "a login started under the remove dialog");
        Assert.False(_controller.IsBusy, "answering No releases the flag");
        Assert.Equal("1", _config.Accounts.Single().Slot);
    }

    [Fact]
    public async Task ARemoveAttemptedDuringALogin_IsRefused_WithoutAsking()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgY);
        LoggedIn();
        _auth.OnConsole = async slot =>
        {
            WriteIdentity(slot, Person, OrgY);
            await _controller.RemoveAsync("1");
        };

        await _controller.RunLoginFlowAsync(null);

        Assert.False(_log.Has("confirm"));
        Assert.Contains(_config.Accounts, a => a.Slot == "1");
    }

    // ---- remove ----

    [Fact]
    public async Task Remove_WritesTheRetiredMarkerFirst_ThenSavesTheEntryRemoval_ThenStopsAndDeletes()
    {
        AddTracked("1", Person, OrgX);

        await _controller.RemoveAsync("1");

        Assert.True(_log.Before("confirm", "marker:1:Retired"));
        Assert.True(_log.Before("marker:1:Retired", "save:"), "marker first, then the entry removal is saved");
        Assert.True(_log.Before("save:", "removed:1"));
        Assert.True(_log.Before("save:", "stop:1"));
        Assert.True(_log.Before("stop:1", "delete:1"));
        Assert.Empty(_config.Accounts);
        Assert.False(Directory.Exists(SlotDir("1")));
        Assert.False(_controller.IsBusy);
    }

    [Fact]
    public async Task Remove_WhenTheRetiredMarkerCannotBeWritten_NothingIsSavedStoppedOrDeleted()
    {
        AddTracked("1", Person, OrgX);
        _store.FailMarker = (slot, state) => slot == "1" && state == SlotState.Retired;

        await _controller.RemoveAsync("1");

        Assert.Equal("1", _config.Accounts.Single().Slot);
        Assert.False(_log.Has("save:"));
        Assert.False(_log.Has("stop:1"));
        Assert.False(_log.Has("delete:1"));
        Assert.True(Directory.Exists(ConfigDir("1")));
        Assert.Equal(new[] { LoginText.RemoveFailed }, _ui.Informed);
    }

    [Fact]
    public async Task Remove_WhenTheEntryRemovalCannotBeSaved_TheEntryAndTheLoginAreKept()
    {
        AddTracked("1", Person, OrgX);
        _saveThrows = new IOException("disk full");

        await _controller.RemoveAsync("1");

        Assert.Equal("1", _config.Accounts.Single().Slot);
        Assert.Equal(SlotState.Active, _store.ReadMarker("1")!.State); // marker put back
        Assert.False(_log.Has("delete:1"));
        Assert.False(_log.Has("stop:1"));
    }

    [Fact]
    public async Task Remove_ReResolvesTheEntryAfterTheDialog_AndAbortsIfItChanged()
    {
        AddTracked("1", Person, OrgX);
        // While the dialog is open the entry for slot "1" is replaced by a different one (a swap repointed it).
        _ui.OnConfirm = () => _config.Accounts[0] = new AccountEntry { Slot = "1", Label = "replaced", Enabled = true };

        await _controller.RemoveAsync("1");

        Assert.False(_log.Has("marker:1:Retired"));
        Assert.False(_log.Has("delete:1"));
        Assert.False(_log.Has("stop:1"));
        Assert.Equal("replaced", _config.Accounts.Single().Label);
        Assert.True(Directory.Exists(ConfigDir("1")));
        Assert.False(_controller.IsBusy);
    }

    [Fact]
    public async Task Remove_AnEntryThatVanishedWhileTheDialogWasOpen_IsAbandoned()
    {
        AddTracked("1", Person, OrgX);
        _ui.OnConfirm = () => _config.Accounts.Clear();

        await _controller.RemoveAsync("1");

        Assert.False(_log.Has("marker:1:Retired"));
        Assert.False(_log.Has("delete:1"));
    }

    // ---- discard ----

    [Fact]
    public async Task Discard_DoesNotDeleteAFolderWhoseRetiredMarkerCouldNotBeWritten()
    {
        LoginAs(Person, OrgX);
        LoggedIn();
        AddTracked("1", Person, OrgX); // same identity: an add is a duplicate and its new slot is discarded
        _store.FailMarker = (slot, state) => slot == NewSlot && state == SlotState.Retired;

        await _controller.RunLoginFlowAsync(null);

        Assert.True(Directory.Exists(SlotDir(NewSlot)), "a folder that is not marked retired must not be deleted");
        Assert.False(_log.Has($"delete:{NewSlot}"));
        Assert.Single(_ui.Informed);
    }

    [Fact]
    public async Task ADuplicateLogin_IsDiscarded_AndNamesTheAccountItAlreadyIs()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgX);
        LoggedIn();

        await _controller.RunLoginFlowAsync(null);

        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.Equal(new[] { LoginText.Duplicate("Acme AB") }, _ui.Informed);
        Assert.Equal("1", _config.Accounts.Single().Slot);
    }

    [Fact]
    public async Task ReloginWhoseOwnIdentityIsUnreadable_ChangesNothing_AndSaysSo()
    {
        AddTracked("1", Person, OrgX, identityOnDisk: false);
        LoginAs(Person, OrgY);
        LoggedIn();

        await _controller.RunLoginFlowAsync("1");

        Assert.Equal("1", _config.Accounts.Single().Slot);
        Assert.False(_log.Has("stop:1"));
        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.Equal(new[] { LoginText.CannotVerify("Acme AB") }, _ui.Informed);
    }

    [Fact]
    public async Task ReloginAsADifferentAccount_ChangesNothing()
    {
        AddTracked("1", Person, OrgX);
        LoginAs(Person, OrgY);
        LoggedIn();

        await _controller.RunLoginFlowAsync("1");

        Assert.Equal("1", _config.Accounts.Single().Slot);
        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.False(_log.Has("stop:1"));
        Assert.Single(_ui.Informed);
    }

    // ---- shutdown and failure ----

    [Fact]
    public async Task WhenTheAppExitsDuringALogin_ThePendingSlotIsLeftForTheNextStartToAdopt()
    {
        LoginAs(Person, OrgX);
        _auth.OnConsole = slot => { WriteIdentity(slot, Person, OrgX); _shutdown.Cancel(); return Task.CompletedTask; };

        await _controller.RunLoginFlowAsync(null);

        Assert.True(Directory.Exists(ConfigDir(NewSlot)));
        Assert.Equal(SlotState.Pending, _store.ReadMarker(NewSlot)!.State);
        Assert.False(_log.Has($"delete:{NewSlot}"));
        Assert.Empty(_ui.Informed);

        // ... and the next start does adopt it.
        var next = new AccountsConfig { Version = AccountsConfig.CurrentVersion };
        AccountReconciler.Reconcile(next, new SlotStore(Path.Combine(_temp, "accounts")));
        Assert.Equal(NewSlot, next.Accounts.Single().Slot);
    }

    [Fact]
    public async Task AFailureInTheLoginConsole_DiscardsThePendingSlot_AndRestoresTheSet()
    {
        AddTracked("1", Person, OrgX);
        _auth.ConsoleThrows = new InvalidOperationException("injected");

        await _controller.RunLoginFlowAsync(null);

        Assert.False(Directory.Exists(SlotDir(NewSlot)));
        Assert.Equal("1", _config.Accounts.Single().Slot);
        Assert.True(_log.Has("start:1"), "the set is rebuilt after a failed flow");
        Assert.Equal(new[] { LoginText.FlowFailed(relogin: false) }, _ui.Informed);
        Assert.False(_controller.IsBusy);
    }

    // ---- rebuild ----

    [Fact]
    public async Task Rebuild_AStopThatTimesOut_IsFollowedByAKill_BeforeAnySlotIsDeleted()
    {
        AddTracked("1", Person, OrgX);
        // A retired folder the reconciliation of this rebuild will delete.
        Directory.CreateDirectory(ConfigDir("22222222"));
        _store.WriteMarker("22222222", SlotState.Retired);
        _host.StopTimesOut = true;
        bool retiredFolderStillThereAtKill = false;
        _host.OnKill = _ => retiredFolderStillThereAtKill = Directory.Exists(SlotDir("22222222"));

        await _controller.RebuildAsync();

        Assert.True(retiredFolderStillThereAtKill, "the kill must come before reconciliation deletes anything");
        Assert.True(_log.Before("stop:1", "kill:1"));
        Assert.True(_log.Before("kill:1", "start:1"), "and before the new set starts");
        Assert.False(Directory.Exists(SlotDir("22222222")));
    }

    [Fact]
    public async Task Rebuild_AGracefulStop_NeedsNoKill()
    {
        AddTracked("1", Person, OrgX);

        await _controller.RebuildAsync();

        Assert.False(_log.Has("kill:1"));
        Assert.True(_log.Has("start:1"));
    }

    [Fact]
    public async Task TheUiMustNotTickWhileTheSetChanges_ButTheLoginWaitDoesNotFreezeIt()
    {
        AddTracked("1", Person, OrgX);

        bool mutatingDuringConsole = true;
        _auth.OnConsole = slot => { mutatingDuringConsole = _controller.IsMutating; WriteIdentity(slot, Person, OrgY); return Task.CompletedTask; };
        LoggedIn();
        await _controller.RunLoginFlowAsync(null);
        Assert.False(mutatingDuringConsole, "waiting for the browser is not a mutation: the tray keeps updating");
        Assert.True(_host.MutatingDuringStop);
        Assert.True(_host.MutatingDuringStart);
        Assert.False(_controller.IsMutating);
    }

    // ---- NeedsLogin toast ----

    [Fact]
    public void NeedsLoginToast_IsShownOncePerSpell()
    {
        _host.Accounts = new List<RunningAccount> { new("1", "Acme AB", null, null, NeedsLogin: true, HasRead: false) };

        _controller.NotifyNeedsLoginTransitions();
        _controller.NotifyNeedsLoginTransitions();

        Assert.Equal(new[] { LoginText.NeedsLoginToast("Acme AB") }, _ui.Toasts);
    }

    [Fact]
    public async Task NeedsLoginToast_IsNotRepeatedByARebuild_OnlyASuccessfulReadEndsTheSpell()
    {
        AddTracked("1", Person, OrgX);
        _host.Accounts = new List<RunningAccount> { new("1", "Acme AB", null, null, NeedsLogin: true, HasRead: false) };
        _controller.NotifyNeedsLoginTransitions();
        Assert.Single(_ui.Toasts);

        // A rebuild gives the slot a brand new runtime: not NeedsLogin yet, and it has not read yet either.
        await _controller.RebuildAsync();
        Assert.Contains(_host.Accounts, a => a.Slot == "1" && !a.NeedsLogin && !a.HasRead);
        _controller.NotifyNeedsLoginTransitions();
        // ... and then it confirms the same thing again.
        _host.Accounts = new List<RunningAccount> { new("1", "Acme AB", null, null, NeedsLogin: true, HasRead: false) };
        _controller.NotifyNeedsLoginTransitions();
        Assert.Single(_ui.Toasts);

        // A successful reading ends the spell; the NEXT transition toasts again.
        _host.Accounts = new List<RunningAccount> { new("1", "Acme AB", null, null, NeedsLogin: false, HasRead: true) };
        _controller.NotifyNeedsLoginTransitions();
        _host.Accounts = new List<RunningAccount> { new("1", "Acme AB", null, null, NeedsLogin: true, HasRead: true) };
        _controller.NotifyNeedsLoginTransitions();
        Assert.Equal(2, _ui.Toasts.Count);
    }

    // ---- focus ----

    [Fact]
    public async Task Remove_TellsTheUiTheSlotIsGone_SoNoFocusPointsAtIt()
    {
        AddTracked("1", Person, OrgX);

        await _controller.RemoveAsync("1");

        Assert.True(_log.Has("removed:1"));
    }

    // ---- rename ("Konton > account > Byt namn...") ----

    [Fact]
    public void Rename_StoresTheTrimmedNameInTheEntryOfThatSlot_AndSaves_WithoutTouchingAnyRuntime()
    {
        AddTracked("1", Person, OrgX);
        AddTracked("4", Person, OrgY, label: "Max");
        _ui.PromptAnswer = "   Mitt jobb  ";

        _controller.Rename("4");

        Assert.Null(_config.Accounts.Single(a => a.Slot == "1").Label);
        Assert.Equal("Mitt jobb", _config.Accounts.Single(a => a.Slot == "4").Label);
        Assert.True(_log.Has("save:1,4"));
        Assert.True(_log.Has("labels-changed"));
        // A label change is not a change of the account set: nothing stopped, started, rebuilt or killed.
        Assert.DoesNotContain(_log.All, e => e.StartsWith("stop:") || e.StartsWith("start:") || e.StartsWith("kill:"));
        Assert.Equal(new[] { "1", "4" }, _host.Accounts.Select(a => a.Slot));
        Assert.False(_controller.IsBusy);
        Assert.False(_controller.IsMutating);
    }

    [Fact]
    public void Rename_PrefillsTheCurrentLabel_AndExplainsThatEmptyMeansAutomatic()
    {
        AddTracked("1", Person, OrgX, label: "Acme AB");
        _ui.PromptAnswer = null;

        _controller.Rename("1");

        Assert.Equal("Acme AB", _ui.PromptedCurrent);
        Assert.Equal(AccountFlowController.MaxLabelLength, _ui.PromptedMaxLength);
        Assert.Contains("automatiska", _ui.PromptedHint);
        Assert.Equal(LoginText.RenameTitle("Acme AB"), _ui.PromptedTitle);
    }

    [Fact]
    public void Rename_ToAnEmptyName_ClearsTheOverride_SoTheAutomaticNameReturns()
    {
        AddTracked("1", Person, OrgX);
        _config.Accounts[0].Label = "Mitt jobb";
        _ui.PromptAnswer = "   ";

        _controller.Rename("1");

        Assert.Null(_config.Accounts[0].Label);
        Assert.True(_log.Has("save:1"));
    }

    [Fact]
    public void Rename_ConfirmingTheAutomaticNameUnchanged_DoesNotFreezeItAsAnOverride()
    {
        AddTracked("1", Person, OrgX, label: "Acme AB");
        _ui.PromptAnswer = "Acme AB"; // OK pressed on the prefilled text

        _controller.Rename("1");

        Assert.Null(_config.Accounts[0].Label);
        Assert.False(_log.Has("save:1"));
    }

    [Fact]
    public void Rename_Cancelled_ChangesNothing_AndReleasesTheBusyFlag()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = null;

        _controller.Rename("1");

        Assert.Null(_config.Accounts[0].Label);
        Assert.False(_log.Has("save:1"));
        Assert.False(_controller.IsBusy);
    }

    [Fact]
    public void Rename_CapsTheNameAtTheMaximumLength()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = new string('x', 100);

        _controller.Rename("1");

        Assert.Equal(AccountFlowController.MaxLabelLength, _config.Accounts[0].Label!.Length);
        Assert.Equal(40, AccountFlowController.MaxLabelLength);
    }

    [Fact]
    public void NormalizeLabel_TrimsDropsControlCharactersAndCaps()
    {
        Assert.Equal("a b", AccountFlowController.NormalizeLabel("  a b \t"));
        Assert.Equal("ab", AccountFlowController.NormalizeLabel("a\r\nb"));
        Assert.Equal("", AccountFlowController.NormalizeLabel(null));
        Assert.Equal("", AccountFlowController.NormalizeLabel("  \t "));
        string capped = AccountFlowController.NormalizeLabel(new string('y', 39) + "  zzzz");
        Assert.True(capped.Length <= 40);
        Assert.False(capped.EndsWith(' '));
    }

    [Fact]
    public void Rename_ReResolvesTheEntryAfterTheDialog_AndAbortsIfItChanged()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = "Nytt namn";
        _ui.OnPrompt = () => _config.Accounts[0] = new AccountEntry { Slot = "1", Label = "replaced", Enabled = true };

        _controller.Rename("1");

        Assert.Equal("replaced", _config.Accounts.Single().Label);
        Assert.False(_log.Has("save:1"));
        Assert.False(_controller.IsBusy);
    }

    [Fact]
    public void Rename_AnEntryThatVanishedDuringTheDialog_IsAbandoned()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = "Nytt namn";
        _ui.OnPrompt = () => _config.Accounts.Clear();

        _controller.Rename("1");

        Assert.False(_log.Has("save:"));
        Assert.False(_log.Has("labels-changed"));
    }

    [Fact]
    public void Rename_FollowsTheSlotId_NotThePosition_WhenTheListChangesDuringTheDialog()
    {
        AddTracked("1", Person, OrgX);
        AddTracked("4", Person, OrgY);
        _ui.PromptAnswer = "Det andra";
        // An account is inserted ahead of both while the dialog is open: positions shift, slot ids do not.
        _ui.OnPrompt = () => _config.Accounts.Insert(0, new AccountEntry { Slot = "9", Enabled = true });

        _controller.Rename("4");

        Assert.Equal("Det andra", _config.Accounts.Single(a => a.Slot == "4").Label);
        Assert.Null(_config.Accounts.Single(a => a.Slot == "1").Label);
        Assert.Null(_config.Accounts.Single(a => a.Slot == "9").Label);
    }

    [Fact]
    public void Rename_RespectsTheBusyFlag_BothWays()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = "Nytt namn";
        bool busyInsideDialog = false;
        Task? loginDuringDialog = null;
        _ui.OnPrompt = () =>
        {
            busyInsideDialog = _controller.IsBusy;
            loginDuringDialog = _controller.RunLoginFlowAsync(null);   // started under the dialog: refused
            _controller.Rename("1");                                    // a second rename: refused
        };

        _controller.Rename("1");

        Assert.True(busyInsideDialog);
        Assert.True(loginDuringDialog!.IsCompleted);
        Assert.False(_log.Has($"console:{NewSlot}"));
        Assert.Equal(1, _ui.PromptCount);
        Assert.Equal("Nytt namn", _config.Accounts[0].Label);
    }

    [Fact]
    public async Task Rename_IsRefusedWhileALoginIsRunning()
    {
        AddTracked("1", Person, OrgX);
        _ui.PromptAnswer = "Nytt namn";
        _auth.OnConsole = slot => { _controller.Rename("1"); return Task.CompletedTask; };

        await _controller.RunLoginFlowAsync(null);

        Assert.Equal(0, _ui.PromptCount);
        Assert.Null(_config.Accounts.First(a => a.Slot == "1").Label);
    }

    [Fact]
    public void Rename_WhenTheSaveFails_TheOldNameIsKept_AndTheUserIsTold()
    {
        AddTracked("1", Person, OrgX);
        _config.Accounts[0].Label = "Gammalt";
        _ui.PromptAnswer = "Nytt";
        _saveThrows = new IOException("disk full");

        _controller.Rename("1");

        Assert.Equal("Gammalt", _config.Accounts[0].Label);
        Assert.Equal(new[] { LoginText.RenameFailed }, _ui.Informed);
        Assert.False(_log.Has("labels-changed"));
    }

    [Fact]
    public void Rename_Wording()
    {
        Assert.Equal("Byt namn…", LoginText.RenameMenu);
        Assert.Equal("Lämna tomt för det automatiska namnet.", LoginText.RenameHint);
        Assert.Equal("OK", LoginText.RenameOk);
        Assert.Equal("Avbryt", LoginText.RenameCancel);
    }
}
