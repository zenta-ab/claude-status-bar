using ClaudeStatusBar.Config;

namespace ClaudeStatusBar.Data;

/// <summary>
/// The child command line ClaudeCliChannel launches, made injectable so tests can
/// point the channel at a fake child (tests/FakeClaudeChild) instead of the real
/// claude.exe. ResolveExePath is a Func, not a string, because the review requires
/// the path be re-resolved on EVERY launch attempt (Codex review High #1): Claude
/// Code auto-update replaces the binary at that path, so a path cached once at
/// process start can go stale across a long-running supervised session.
///
/// EnvironmentOverrides (docs/multi-account.md "Environment") is how one account's child is
/// pointed at its own slot: ChildEnvironment.ForSlot builds it -- the slot's config dir, plus the
/// removal of every variable that would make Claude Code use some other login. A null value in
/// the dictionary means "remove this variable from the child's environment". Tests that point
/// the channel at a fake child leave it null.
/// </summary>
public sealed record ChildProcessSpec(
    Func<string> ResolveExePath,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentOverrides = null)
{
    /// <summary>The long-lived polling child: stream-json in and out.</summary>
    public const string PollingArguments = "-p --input-format stream-json --output-format stream-json --verbose";

    /// <summary>
    /// Builds the spec for one account slot (docs/multi-account.md). Every account has a slot, so
    /// every child gets a pinned config dir and its own working directory; there is no "inherit
    /// whatever the default login is" spec any more. `arguments` lets `auth login` / `auth
    /// status` share exactly the same environment as the polling child. `agentRootOverride` is
    /// test-only: it keeps a test's working directories out of the real
    /// %LOCALAPPDATA%\ClaudeStatusBar\agent.
    /// </summary>
    public static ChildProcessSpec ForSlot(SlotStore slots, string slotId, string arguments = PollingArguments, string? agentRootOverride = null)
    {
        ChildEnvironment.Plan plan = ChildEnvironment.ForSlot(slots, slotId, agentRootOverride);
        return new ChildProcessSpec(
            ResolveExePath: ResolveInstalledClaudeExe,
            Arguments: arguments,
            WorkingDirectory: plan.WorkingDirectory,
            EnvironmentOverrides: plan.Overrides);
    }

    /// <summary>Re-resolves on every call: the native installer replaces the binary on auto-update.</summary>
    public static string ResolveInstalledClaudeExe() => ResolveClaudeExe(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("PATH"),
        File.Exists);

    /// <summary>
    /// The native installer's location first (%USERPROFILE%\.local\bin\claude.exe), then the
    /// first claude.exe on PATH. Only a real .exe qualifies: an npm install's claude.cmd shim
    /// runs node under cmd.exe and can't be supervised the same way. When nothing is found the
    /// native path is returned, so the launch failure names where Claude Code was expected.
    /// </summary>
    public static string ResolveClaudeExe(string userProfile, string? pathVariable, Func<string, bool> exists)
    {
        string nativeInstall = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        if (exists(nativeInstall)) return nativeInstall;

        foreach (string entry in (pathVariable ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(entry.Trim('"'), "claude.exe"); }
            catch (ArgumentException) { continue; }
            if (exists(candidate)) return candidate;
        }
        return nativeInstall;
    }
}
