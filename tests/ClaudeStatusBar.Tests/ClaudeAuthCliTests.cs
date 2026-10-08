using System.Diagnostics;
using System.Text;
using ClaudeStatusBar.Config;
using ClaudeStatusBar.Data;
using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Data/ClaudeAuthCli.cs: the `auth status --json` reading (only loggedIn and authMethod ever
/// survive; the email in that output never does), and the two commands run for real against
/// tests/FakeClaudeChild with the one environment builder applied -- including the visible login
/// console, launched exactly as production launches it, which proves that waiting for it works
/// with whatever the default terminal is.
/// </summary>
[Collection("process")]
public sealed class ClaudeAuthCliTests : IDisposable
{
    static string ExePath => Path.Combine(AppContext.BaseDirectory, "FakeClaudeChild.exe");

    // The long spelling of the temp path (GetTempPath may return 8.3 names), so a child's own report of its working directory can be compared as a full path.
    readonly string _temp = MakeLongTempDir();
    readonly SlotStore _store;
    readonly ClaudeAuthCli _cli;
    readonly string _dump;
    readonly List<string> _envToClean = new();

    public ClaudeAuthCliTests()
    {
        _store = new SlotStore(Path.Combine(_temp, "accounts"));
        _cli = new ClaudeAuthCli(_store, () => ExePath, agentRootOverride: Path.Combine(_temp, "agent"));
        _dump = Path.Combine(_temp, "dump.txt");
        SetEnv("FAKE_AUTH_DUMP", _dump);
    }

    void SetEnv(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envToClean.Add(name);
    }

    static string MakeLongTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "csb-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return SlotStore.ToLongPath(dir);
    }

    public void Dispose()
    {
        foreach (string name in _envToClean) Environment.SetEnvironmentVariable(name, null);
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    // ---- AuthStatusParser ----

    [Fact]
    public void Parser_ClaudeAiLogin_IsAClaudeAiLogin_AndKeepsNothingElse()
    {
        AuthStatus? status = AuthStatusParser.Parse("""{"loggedIn": true, "authMethod": "claude.ai", "email": "alex@example.com", "orgName": "Acme AB"}""");

        Assert.NotNull(status);
        Assert.True(status!.IsClaudeAiLogin);
        Assert.DoesNotContain("alex", status.ToString()!);
        Assert.DoesNotContain("Acme", status.ToString()!);
    }

    [Theory]
    [InlineData("""{"loggedIn": true, "authMethod": "console"}""")]
    [InlineData("""{"loggedIn": true, "authMethod": "api_key"}""")]
    [InlineData("""{"loggedIn": true}""")]
    [InlineData("""{"loggedIn": false, "authMethod": "claude.ai"}""")]
    [InlineData("""{"loggedIn": false, "authMethod": "none"}""")]
    public void Parser_AnythingButALoggedInClaudeAiLogin_IsNotOne(string json)
    {
        AuthStatus? status = AuthStatusParser.Parse(json);

        Assert.NotNull(status);
        Assert.False(status!.IsClaudeAiLogin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"authMethod": "claude.ai"}""")]
    [InlineData("""{"loggedIn": "yes", "authMethod": "claude.ai"}""")]
    public void Parser_NoUsableAnswer_IsNull(string? json)
    {
        Assert.Null(AuthStatusParser.Parse(json));
    }

    // ---- the login script ----

    [Fact]
    public void LoginScript_PrintsTheNotice_ThenRunsAuthLoginClaudeAi_AndPassesTheExitCodeOn()
    {
        string script = ClaudeAuthCli.BuildLoginScript(@"C:\Users\someone\.local\bin\claude.exe", "Logga in med kontot du vill följa.");

        Assert.Contains("Write-Host 'Logga in med kontot du vill följa.'", script);
        Assert.Contains(@"& 'C:\Users\someone\.local\bin\claude.exe' auth login --claudeai", script);
        Assert.EndsWith("exit $LASTEXITCODE", script);
        Assert.True(script.IndexOf("Write-Host 'Logga", StringComparison.Ordinal) < script.IndexOf("auth login", StringComparison.Ordinal));
    }

    [Fact]
    public void LoginScript_DoublesSingleQuotes_SoNeitherTheNoticeNorAPathCanBreakOutOfItsLiteral()
    {
        string script = ClaudeAuthCli.BuildLoginScript(@"C:\Users\someone\o'neil\claude.exe", "it's '; Remove-Item x; '");

        Assert.Contains("Write-Host 'it''s ''; Remove-Item x; '''", script);
        Assert.Contains(@"& 'C:\Users\someone\o''neil\claude.exe' auth login --claudeai", script);
    }

    [Fact]
    public void EncodedScript_RoundTripsThroughUtf16Base64_SoSwedishTextSurvives()
    {
        const string script = "Write-Host 'Fönstret stängs av sig självt.'";

        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(ClaudeAuthCli.EncodeScript(script)));

        Assert.Equal(script, decoded);
    }

    // ---- auth status, for real ----

    [Fact]
    public async Task Status_RunsWithTheEnvironmentBuilder_NotTheInheritedEnvironment()
    {
        string slot = _store.CreatePending();
        SetEnv("ANTHROPIC_API_KEY", "inherited-dummy");
        SetEnv("CLAUDE_CODE_OAUTH_TOKEN", "inherited-dummy");
        SetEnv("CLAUDE_CONFIG_DIR", @"C:\Users\someone\.claude");
        SetEnv("CLAUDE_SECURESTORAGE_CONFIG_DIR", @"C:\Users\someone\.claude");

        AuthStatus? status = await _cli.GetStatusAsync(slot, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.True(status is { IsClaudeAiLogin: true });
        string dump = File.ReadAllText(_dump);
        Assert.Contains("args=auth status --json", dump);
        Assert.Contains("CLAUDE_CONFIG_DIR=" + _store.ConfigDirOf(slot), dump);
        Assert.Contains("CLAUDE_SECURESTORAGE_CONFIG_DIR=" + _store.ConfigDirOf(slot), dump);
        // The full path, under the test's own agent root -- never the real %LOCALAPPDATA%\ClaudeStatusBar\agent.
        Assert.Contains("cwd=" + Path.Combine(_temp, "agent", slot) + Environment.NewLine, dump);
        Assert.False(Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeStatusBar", "agent", slot)), "a test created a working directory under the real agent folder");
        Assert.DoesNotContain("env:ANTHROPIC_API_KEY", dump);
        Assert.DoesNotContain("env:CLAUDE_CODE_OAUTH_TOKEN", dump);
    }

    [Theory]
    [InlineData("none", false, null)]
    [InlineData("console", true, "console")]
    public async Task Status_ReportsWhatTheCommandSaid(string behaviour, bool loggedIn, string? method)
    {
        string slot = _store.CreatePending();
        SetEnv("FAKE_AUTH_BEHAVIOUR", behaviour);

        AuthStatus? status = await _cli.GetStatusAsync(slot, TimeSpan.FromSeconds(20), CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(loggedIn, status!.LoggedIn);
        Assert.False(status.IsClaudeAiLogin);
        if (method != null) Assert.Equal(method, status.AuthMethod);
    }

    [Fact]
    public async Task Status_UnparseableOutput_IsNoAnswer()
    {
        string slot = _store.CreatePending();
        SetEnv("FAKE_AUTH_BEHAVIOUR", "garbage");

        Assert.Null(await _cli.GetStatusAsync(slot, TimeSpan.FromSeconds(20), CancellationToken.None));
    }

    [Fact]
    public async Task Status_ACommandThatNeverAnswers_IsKilledAtTheTimeout_AndLeavesNothingRunning()
    {
        string slot = _store.CreatePending();
        SetEnv("FAKE_AUTH_BEHAVIOUR", "hang");
        var sw = Stopwatch.StartNew();

        AuthStatus? status = await _cli.GetStatusAsync(slot, TimeSpan.FromSeconds(1.5), CancellationToken.None);
        sw.Stop();

        Assert.Null(status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"took {sw.Elapsed}");
        await AssertProcessGoneAsync(DumpedPid());
    }

    [Fact]
    public async Task Status_RefusesASlotThePathGuardRefuses()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cli.GetStatusAsync(@"..\1", TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    // ---- the visible login console, for real ----

    [Fact]
    public async Task LoginConsole_WaitsForTheRealProcess_PassesTheExitCodeBack_AndUsesTheEnvironmentBuilder()
    {
        // Launched exactly as production launches it: a visible console (hosted by the default
        // terminal -- Windows Terminal on a stock Windows 11), an EncodedCommand PowerShell, the
        // slot's environment. It must not return before the child has actually finished.
        string slot = _store.CreatePending();
        SetEnv("FAKE_AUTH_LOGIN_EXIT", "7");
        SetEnv("ANTHROPIC_API_KEY", "inherited-dummy");
        var sw = Stopwatch.StartNew();

        int? exit = await _cli.RunLoginConsoleAsync(slot, "Test notice", CancellationToken.None);
        sw.Stop();

        Assert.Equal(7, exit);
        Assert.True(File.Exists(_dump), "the login command never ran");
        string dump = File.ReadAllText(_dump);
        Assert.Contains("args=auth login --claudeai", dump);
        Assert.Contains("CLAUDE_CONFIG_DIR=" + _store.ConfigDirOf(slot), dump);
        Assert.DoesNotContain("env:ANTHROPIC_API_KEY", dump);
        await AssertProcessGoneAsync(DumpedPid()); // wait returned only after the whole console tree ended
    }

    [Fact]
    public async Task LoginConsole_WhenTheWaitIsCancelled_LeavesTheConsoleRunning()
    {
        // `claude auth login` may cold-start the browser, which then lives under the console: ending the
        // console (or a job holding it) would close the user's browser mid-login. So a cancelled wait
        // -- the app is exiting -- stops waiting and touches nothing; the next start adopts the slot.
        string slot = _store.CreatePending();
        SetEnv("FAKE_AUTH_BEHAVIOUR", "hang");
        using var cts = new CancellationTokenSource();
        Task<int?> running = _cli.RunLoginConsoleAsync(slot, "Test notice", cts.Token);

        // Wait until the grandchild (powershell -> fake claude) has really started, then cancel.
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(_dump) && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(File.Exists(_dump), "the login command never started");
        int pid = DumpedPid();
        try
        {
            cts.Cancel();
            int? exit = await running;

            Assert.Null(exit);
            await Task.Delay(1500);
            using Process still = Process.GetProcessById(pid); // throws if it was killed
            Assert.False(still.HasExited, "cancelling the wait must not end the login console's process tree");
        }
        finally
        {
            try { using Process p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task LoginConsole_RefusesASlotThePathGuardRefuses()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _cli.RunLoginConsoleAsync(@"..\..\x", "Test notice", CancellationToken.None));
    }

    int DumpedPid()
    {
        string line = File.ReadAllLines(_dump).First(l => l.StartsWith("pid=", StringComparison.Ordinal));
        return int.Parse(line["pid=".Length..]);
    }

    static async Task AssertProcessGoneAsync(int pid)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch (ArgumentException)
            {
                return; // no such process
            }
            await Task.Delay(100);
        }
        Assert.Fail($"process {pid} is still running");
    }
}
