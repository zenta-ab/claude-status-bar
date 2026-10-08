namespace ClaudeStatusBar.Data;

/// <summary>
/// What one get_usage poll produced: a snapshot, or a failure with its reason. NotLoggedIn is the
/// structured form of one specific failure -- the child answered, but with the "no subscription
/// login here" shape (docs/multi-account.md "NeedsLogin") -- so callers can tell it apart from a
/// transport failure or a response this parser does not understand without matching on text.
/// </summary>
public readonly record struct UsageReadResult(UsageSnapshot? Snapshot, string? Error, TimeSpan Latency, bool NotLoggedIn = false)
{
    /// <summary>Three-part deconstruction, kept so callers that only care about snapshot/error/latency read as before.</summary>
    public void Deconstruct(out UsageSnapshot? snapshot, out string? error, out TimeSpan latency)
    {
        snapshot = Snapshot;
        error = Error;
        latency = Latency;
    }
}

/// <summary>The outcome of parsing one control_response envelope (UsageParser.Parse).</summary>
public readonly record struct UsageParseResult(UsageSnapshot? Snapshot, string? Error, bool NotLoggedIn = false);
