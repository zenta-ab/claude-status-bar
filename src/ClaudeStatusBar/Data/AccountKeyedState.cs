using ClaudeStatusBar.Model;

namespace ClaudeStatusBar.Data;

/// <summary>
/// Owns the identity-keyed half of one account's runtime state: the QuotaModel and its paired
/// per-account DiskLogSink (raw log + logs\&lt;identityKey&gt;\window-shape-YYYY-MM.csv), keyed by
/// AccountIdentity.StateKey -- the (accountUuid, organizationUuid) pair, NOT accountUuid alone
/// (docs/multi-account.md "Identity guard"; AccountIdentity's own doc comment explains why
/// accountUuid alone is not unique per plan). Deliberately split out of AccountRuntime so the
/// rebuild-on-identity-change behaviour -- the actual fix for today's live bug (two logins for
/// the same person, in two organisations, silently sharing one forecast) -- is directly
/// unit-testable without spawning a real claude.exe child.
///
/// A null identity key (identity not known yet, or never logged in) is a valid, stable key too:
/// it lands under logs\_pending\ until a real identity is read, at which point that first
/// transition is handled by the exact same SyncIdentity path as any later account switch.
/// </summary>
public sealed class AccountKeyedState : IAsyncDisposable
{
    const string PendingKey = "_pending";

    readonly string _baseLogDir;
    string? _identityKey;
    bool _warmStarted;

    public string? IdentityKey => _identityKey;
    public DiskLogSink LogSink { get; private set; }
    public QuotaModel Model { get; private set; }
    public string CurrentLogDir { get; private set; }
    public bool WarmStarted => _warmStarted;

    public AccountKeyedState(string baseLogDir, string? initialIdentityKey)
    {
        _baseLogDir = baseLogDir;
        _identityKey = initialIdentityKey;
        CurrentLogDir = ResolveDir(initialIdentityKey);
        LogSink = new DiskLogSink(CurrentLogDir);
        Model = new QuotaModel();
    }

    string ResolveDir(string? key) => Path.Combine(_baseLogDir, key ?? PendingKey);

    /// <summary>
    /// Call with the latest known identity key (AccountIdentity.StateKey) before applying a poll
    /// result. If it differs from what this state is currently keyed to -- including when only
    /// the organisation half of the pair changed, e.g. the same person's Team login replacing
    /// their Max login in the same config directory -- drops the QuotaModel and swaps in a fresh
    /// per-account DiskLogSink pointed at the new key's directory: no envelope, rate or
    /// fingerprint history carries over. The old key's directory (and everything in it) is left
    /// on disk untouched, ready for when that identity comes back. Returns true iff a rebuild
    /// happened, so callers know WarmStart is eligible again and (for AccountRuntime) that the
    /// channel supervisor must be recreated against the new LogSink.
    /// </summary>
    public bool SyncIdentity(string? identityKey)
    {
        if (string.Equals(identityKey, _identityKey, StringComparison.Ordinal)) return false;

        DiskLogSink oldSink = LogSink;
        _ = oldSink.DisposeAsync(TimeSpan.FromSeconds(2)); // best-effort background flush; must never block the poll path

        _identityKey = identityKey;
        CurrentLogDir = ResolveDir(identityKey);
        LogSink = new DiskLogSink(CurrentLogDir);
        Model = new QuotaModel();
        _warmStarted = false;
        return true;
    }

    public void MarkWarmStarted() => _warmStarted = true;

    public async ValueTask DisposeAsync() => await LogSink.DisposeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
}
