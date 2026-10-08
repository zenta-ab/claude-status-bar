using System.Diagnostics;
using ClaudeStatusBar.Config;
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

    // ---- docs/multi-account.md "Environment": every child is pinned to its own slot ----

    static SlotStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "csb-spec-" + Guid.NewGuid().ToString("N"), "accounts");
        return new SlotStore(root);
    }

    [Fact]
    public void ForSlot_PinsBothConfigVariablesToTheGuardedConfigDir()
    {
        SlotStore store = NewStore(out string root);

        ChildProcessSpec spec = ChildProcessSpec.ForSlot(store, "a1b2c3d4");

        string expected = Path.Combine(root, "a1b2c3d4", "config");
        Assert.NotNull(spec.EnvironmentOverrides);
        Assert.Equal(expected, spec.EnvironmentOverrides!["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(expected, spec.EnvironmentOverrides["CLAUDE_SECURESTORAGE_CONFIG_DIR"]);
        Assert.Equal(ChildProcessSpec.PollingArguments, spec.Arguments);
    }

    [Fact]
    public void ForSlot_EachSlotGetsItsOwnWorkingDirectory_UnderTheAgentFolder()
    {
        SlotStore store = NewStore(out _);

        ChildProcessSpec a = ChildProcessSpec.ForSlot(store, "a1b2c3d4");
        ChildProcessSpec b = ChildProcessSpec.ForSlot(store, "4");

        Assert.NotEqual(a.WorkingDirectory, b.WorkingDirectory);
        Assert.EndsWith(Path.Combine("ClaudeStatusBar", "agent", "a1b2c3d4"), a.WorkingDirectory);
        Assert.EndsWith(Path.Combine("ClaudeStatusBar", "agent", "4"), b.WorkingDirectory);
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData("A1B2C3D4")]
    [InlineData("")]
    [InlineData(@"1\config\..")]
    public void ForSlot_RefusesASlotThePathGuardRefuses(string slot)
    {
        SlotStore store = NewStore(out _);

        Assert.Throws<UnauthorizedAccessException>(() => ChildProcessSpec.ForSlot(store, slot));
    }

    [Fact]
    public void ForSlot_AuthCommandsShareTheSameEnvironmentAsThePollingChild()
    {
        SlotStore store = NewStore(out _);

        ChildProcessSpec polling = ChildProcessSpec.ForSlot(store, "a1b2c3d4");
        ChildProcessSpec status = ChildProcessSpec.ForSlot(store, "a1b2c3d4", "auth status --json");

        Assert.Equal("auth status --json", status.Arguments);
        Assert.Equal(polling.WorkingDirectory, status.WorkingDirectory);
        Assert.Equal(polling.EnvironmentOverrides!.OrderBy(kv => kv.Key), status.EnvironmentOverrides!.OrderBy(kv => kv.Key));
    }

    // ---- ChildEnvironment: what the child's environment ends up as ----

    static readonly string[] MustBeRemoved =
    {
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_BASE_URL",
    };

    [Fact]
    public void ChildEnvironment_RemovesEveryVariableTheSpecNames()
    {
        SlotStore store = NewStore(out _);

        ChildEnvironment.Plan plan = ChildEnvironment.ForSlot(store, "a1b2c3d4");

        foreach (string name in MustBeRemoved)
        {
            Assert.True(plan.Overrides.ContainsKey(name), $"{name} is not handled");
            Assert.Null(plan.Overrides[name]);
        }
    }

    [Fact]
    public void ChildEnvironment_OnlyTheTwoConfigVariablesAreEverSet_EverythingElseIsARemoval()
    {
        SlotStore store = NewStore(out _);

        ChildEnvironment.Plan plan = ChildEnvironment.ForSlot(store, "a1b2c3d4");

        var set = plan.Overrides.Where(kv => kv.Value is not null).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(new[] { "CLAUDE_CONFIG_DIR", "CLAUDE_SECURESTORAGE_CONFIG_DIR" }, set);
        Assert.True(plan.Overrides.Count > 20, "the removal list should cover the credential, provider, endpoint and identity overrides");
    }

    [Fact]
    public void ChildEnvironment_RemovalListHasNoDuplicatesAndNeverTouchesTheConfigVariables()
    {
        Assert.Equal(ChildEnvironment.Removed.Count, ChildEnvironment.Removed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(ChildEnvironment.ConfigDirVariable, ChildEnvironment.Removed);
        Assert.DoesNotContain(ChildEnvironment.SecureStorageVariable, ChildEnvironment.Removed);
    }

    [Fact]
    public void ChildEnvironment_Apply_StripsHostileInheritedValues_AndPinsTheSlot()
    {
        SlotStore store = NewStore(out string root);
        var psi = new ProcessStartInfo("claude.exe");
        // What a parent that was started from inside someone else's Claude Code session might hand down.
        psi.Environment["CLAUDE_CONFIG_DIR"] = @"C:\Users\someone\.claude";
        psi.Environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = @"C:\Users\someone\elsewhere";
        foreach (string name in MustBeRemoved) psi.Environment[name] = "inherited-value";
        psi.Environment["PATH_KEPT_BY_THE_BUILDER"] = "yes";

        ChildEnvironment.Apply(psi.Environment, ChildEnvironment.ForSlot(store, "a1b2c3d4").Overrides);

        string expected = Path.Combine(root, "a1b2c3d4", "config");
        Assert.Equal(expected, psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal(expected, psi.Environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"]);
        foreach (string name in MustBeRemoved)
            Assert.False(psi.Environment.ContainsKey(name), $"{name} survived");
        Assert.Equal("yes", psi.Environment["PATH_KEPT_BY_THE_BUILDER"]); // unrelated variables are left alone
    }

    [Fact]
    public void ChildEnvironment_Apply_MatchesVariableNamesCaseInsensitively_AsWindowsDoes()
    {
        SlotStore store = NewStore(out _);
        var psi = new ProcessStartInfo("claude.exe");
        psi.Environment["anthropic_api_key"] = "inherited-value";

        ChildEnvironment.Apply(psi.Environment, ChildEnvironment.ForSlot(store, "a1b2c3d4").Overrides);

        Assert.False(psi.Environment.ContainsKey("anthropic_api_key"));
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    }
}
