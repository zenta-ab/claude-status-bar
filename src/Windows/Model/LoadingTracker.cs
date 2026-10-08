namespace ClaudeStatusBar.Model;

/// <summary>
/// The "Hämtar kvoten…" state of one account (docs/multi-account.md "Loading"): from the moment its
/// runtime starts until the FIRST result -- a successful read or a failure -- or until the timeout,
/// whichever comes first. After that the ordinary Unknown / NeedsLogin rules apply. Before it, an
/// account that has simply not answered yet must not look like an error ("Kan inte läsa kvoten"
/// with a "!").
///
/// Pure and monotonic-clock based, so the transitions are tested without a runtime or a timer.
/// A rebuild creates new runtimes, and a new runtime starts a new tracker: loading restarts.
/// </summary>
public sealed class LoadingTracker
{
    /// <summary>How long an account may stay in the loading state without any result.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    readonly long _timeoutMs;
    long _startMono;
    bool _ended;

    public LoadingTracker(long startMono, TimeSpan? timeout = null)
    {
        _startMono = startMono;
        _timeoutMs = (long)(timeout ?? DefaultTimeout).TotalMilliseconds;
    }

    /// <summary>True until the first result has arrived or the timeout has passed.</summary>
    public bool IsLoading(long monoMs) => !_ended && monoMs - _startMono < _timeoutMs;

    /// <summary>The first result (success or failure) arrived: loading is over for good.</summary>
    public void End() => _ended = true;

    /// <summary>The runtime was (re)started: loading begins again.</summary>
    public void Restart(long monoMs)
    {
        _startMono = monoMs;
        _ended = false;
    }
}
