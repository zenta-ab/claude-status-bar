using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

public class ChildProcessSpecTests
{
    const string Profile = @"C:\Users\someone";
    static readonly string NativeInstall = Path.Combine(Profile, ".local", "bin", "claude.exe");

    [Fact]
    public void ResolveClaudeExe_PrefersNativeInstall_EvenWhenPathAlsoHasOne()
    {
        string path = ChildProcessSpec.ResolveClaudeExe(Profile, @"C:\tools",
            p => p == NativeInstall || p == @"C:\tools\claude.exe");

        Assert.Equal(NativeInstall, path);
    }

    [Fact]
    public void ResolveClaudeExe_FallsBackToFirstClaudeExeOnPath_InPathOrder()
    {
        string path = ChildProcessSpec.ResolveClaudeExe(Profile, @"C:\a; ""C:\b"" ;C:\c",
            p => p == @"C:\b\claude.exe" || p == @"C:\c\claude.exe");

        Assert.Equal(@"C:\b\claude.exe", path);
    }

    [Fact]
    public void ResolveClaudeExe_IgnoresNpmCmdShim()
    {
        string path = ChildProcessSpec.ResolveClaudeExe(Profile, @"C:\Users\someone\AppData\Roaming\npm",
            p => p.EndsWith("claude.cmd", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(NativeInstall, path);
    }

    [Fact]
    public void ResolveClaudeExe_NothingFound_ReturnsNativePathSoTheErrorNamesTheExpectedLocation()
    {
        Assert.Equal(NativeInstall, ChildProcessSpec.ResolveClaudeExe(Profile, null, _ => false));
        Assert.Equal(NativeInstall, ChildProcessSpec.ResolveClaudeExe(Profile, ";; ;", _ => false));
    }

    // ---- docs/multi-account.md: per-account environment and working directory ----

    [Fact]
    public void ForAccount_DefaultAccount_ConfigDirNull_DoesNotSetClaudeConfigDirEnvVar()
    {
        ChildProcessSpec spec = ChildProcessSpec.ForAccount("default", configDir: null);

        Assert.Null(spec.EnvironmentOverrides);
    }

    [Fact]
    public void ForAccount_NonDefaultAccount_SetsClaudeConfigDirEnvVar()
    {
        const string configDir = @"C:\Users\someone\AppData\Local\ClaudeStatusBar\accounts\1\config";
        ChildProcessSpec spec = ChildProcessSpec.ForAccount("1", configDir);

        Assert.NotNull(spec.EnvironmentOverrides);
        Assert.Equal(configDir, spec.EnvironmentOverrides!["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void ForAccount_DefaultSlot_KeepsThePreMultiAccountWorkingDirectory()
    {
        ChildProcessSpec spec = ChildProcessSpec.ForAccount("default", configDir: null);
        ChildProcessSpec legacyDefault = ChildProcessSpec.Default();

        Assert.Equal(legacyDefault.WorkingDirectory, spec.WorkingDirectory);
        Assert.EndsWith(Path.Combine("ClaudeStatusBar", "agent"), spec.WorkingDirectory);
    }

    [Fact]
    public void ForAccount_NonDefaultSlot_GetsItsOwnWorkingDirectorySubfolder()
    {
        ChildProcessSpec defaultSpec = ChildProcessSpec.ForAccount("default", configDir: null);
        ChildProcessSpec secondSpec = ChildProcessSpec.ForAccount("1", @"C:\somewhere\config");

        Assert.NotEqual(defaultSpec.WorkingDirectory, secondSpec.WorkingDirectory);
        Assert.EndsWith(Path.Combine("agent", "1"), secondSpec.WorkingDirectory);
    }
}
