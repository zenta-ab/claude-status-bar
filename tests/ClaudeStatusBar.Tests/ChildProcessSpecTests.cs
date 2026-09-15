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
}
