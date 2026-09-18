namespace ClaudeStatusBar.Data;

/// <summary>
/// The child command line ClaudeCliChannel launches, made injectable so tests can
/// point the channel at a fake child (tests/FakeClaudeChild) instead of the real
/// claude.exe. ResolveExePath is a Func, not a string, because the review requires
/// the path be re-resolved on EVERY launch attempt (Codex review High #1): Claude
/// Code auto-update replaces the binary at that path, so a path cached once at
/// process start can go stale across a long-running supervised session.
///
/// EnvironmentOverrides (docs/multi-account.md) is how one account's child is
/// pointed at a config directory other than Claude Code's own default: setting
/// CLAUDE_CONFIG_DIR is exactly what "N accounts = N config directories = N child
/// processes" means in practice. It is null for the default account so its child's
/// environment is simply inherited unmodified -- CLAUDE_CONFIG_DIR is left for the
/// child to resolve itself exactly as claude.exe always has.
/// </summary>
public sealed record ChildProcessSpec(
    Func<string> ResolveExePath,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null)
{
    public static ChildProcessSpec Default() => ForAccount(slot: "default", configDir: null);

    /// <summary>
    /// Builds the spec for one account slot (docs/multi-account.md). `slot` is an opaque
    /// per-account identifier the caller controls (StatusBarApplicationContext uses "default"
    /// for accounts[0] and the account's index for every other one); it only affects the
    /// working directory, so two accounts never share one claude.exe session directory.
    ///
    /// `configDir` null means "Claude Code's own default login": CLAUDE_CONFIG_DIR is left
    /// unset in EnvironmentOverrides entirely (not set to the resolved default path) so the
    /// child inherits this process's own environment exactly as it always has -- if the user
    /// already has CLAUDE_CONFIG_DIR set for their whole session, that keeps working unchanged.
    /// A non-null configDir is a second/third account and gets CLAUDE_CONFIG_DIR pinned
    /// explicitly so it can never drift onto whatever the default account happens to resolve to.
    /// </summary>
    public static ChildProcessSpec ForAccount(string slot, string? configDir)
    {
        string agentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "agent");
        string workingDirectory = string.Equals(slot, "default", StringComparison.Ordinal)
            ? agentRoot // unchanged from the pre-multi-account layout: the sole/first account keeps its existing folder
            : Path.Combine(agentRoot, slot);

        Dictionary<string, string>? overrides = null;
        if (!string.IsNullOrEmpty(configDir))
            overrides = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDir };

        return new ChildProcessSpec(
            ResolveExePath: () => ResolveClaudeExe(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetEnvironmentVariable("PATH"),
                File.Exists),
            Arguments: "-p --input-format stream-json --output-format stream-json --verbose",
            WorkingDirectory: workingDirectory,
            EnvironmentOverrides: overrides);
    }

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
