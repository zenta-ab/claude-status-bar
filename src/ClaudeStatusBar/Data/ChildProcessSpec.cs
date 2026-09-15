namespace ClaudeStatusBar.Data;

/// <summary>
/// The child command line ClaudeCliChannel launches, made injectable so tests can
/// point the channel at a fake child (tests/FakeClaudeChild) instead of the real
/// claude.exe. ResolveExePath is a Func, not a string, because the review requires
/// the path be re-resolved on EVERY launch attempt (Codex review High #1): Claude
/// Code auto-update replaces the binary at that path, so a path cached once at
/// process start can go stale across a long-running supervised session.
/// </summary>
public sealed record ChildProcessSpec(Func<string> ResolveExePath, string Arguments, string WorkingDirectory)
{
    public static ChildProcessSpec Default() => new(
        ResolveExePath: () => ResolveClaudeExe(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("PATH"),
            File.Exists),
        Arguments: "-p --input-format stream-json --output-format stream-json --verbose",
        WorkingDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "agent"));

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
